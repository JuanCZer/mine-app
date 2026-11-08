using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Notes.Api.IdentityAdministration;

public sealed record IdentityRateLimitRequest(
    string Policy,
    string Subject,
    string? NormalizedIp,
    DateTimeOffset Now);

public sealed record IdentityRateLimitDecision(
    bool Allowed,
    DateTimeOffset? RetryAfter,
    int Remaining);

public sealed class IdentityRateLimitUnavailableException(Exception? inner = null)
    : InvalidOperationException("El control de intentos de identidad no está disponible.", inner);

public interface IIdentityRateLimiter
{
    Task<IdentityRateLimitDecision> ConsumeAsync(
        IdentityRateLimitRequest request,
        CancellationToken cancellationToken);

    Task<int> DeleteExpiredBatchAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed class IdentityRateLimiter(
    IdentityAdminConnection connectionFactory,
    IdentitySecurityOptions securityOptions) : IIdentityRateLimiter
{
    private const int OpportunisticCleanupBatchSize = 16;
    private static readonly TimeSpan CleanupTtl = TimeSpan.FromHours(24);
    private static readonly byte[] DomainSeparator = Encoding.ASCII.GetBytes("gds.identity-rate-limit.v1\0");

    public async Task<IdentityRateLimitDecision> ConsumeAsync(
        IdentityRateLimitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = PolicyDefinition.Parse(request);
        var now = request.Now.ToUniversalTime();
        var windowStartedAt = CanonicalWindowStart(now, policy.Window);
        var windowEndsAt = windowStartedAt.Add(policy.Window);
        var expiresAt = windowEndsAt.Add(CleanupTtl);
        var bucketMaterial = BuildBucketMaterial(request, policy, windowStartedAt);

        try
        {
            await using var connection = await connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            var state = await LockAllStateAsync(connection, transaction, cancellationToken);
            ValidateRequiredSecrets(state);

            // Opportunistic cleanup belongs to the consume transaction. It does
            // not open a second transaction and preserves state -> bucket order.
            _ = await DeleteExpiredRowsAsync(
                connection, transaction, OpportunisticCleanupBatchSize, now, cancellationToken);

            var candidateKeys = state.Rows
                .Where(row => row.State == "active"
                    || (row.State == "retained" && windowStartedAt < row.ChangedAt))
                .Select(row => row.KeyId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!candidateKeys.Contains(state.ActiveKeyId, StringComparer.Ordinal))
                throw new RateLimitProtocolException();

            var candidates = candidateKeys.Select(keyId => new BucketCandidate(
                keyId,
                HMACSHA256.HashData(securityOptions.SecretsByKeyId[keyId], bucketMaterial)))
                .ToArray();

            foreach (var candidate in candidates)
                await MaterializeCandidateAsync(
                    connection, transaction, candidate, windowStartedAt, windowEndsAt,
                    expiresAt, cancellationToken);

            var existing = await LockCandidatesAsync(
                connection, transaction, candidates, windowStartedAt, cancellationToken);
            if (existing.Count != candidates.Length
                || !existing.Select(row => row.KeyId).SequenceEqual(candidateKeys, StringComparer.Ordinal))
                throw new RateLimitProtocolException();

            var priorAttempts = existing.Sum(row => (long)row.AttemptCount);
            if (priorAttempts >= int.MaxValue) throw new RateLimitProtocolException();
            await IncrementActiveAsync(
                connection, transaction,
                candidates.Single(candidate => candidate.KeyId == state.ActiveKeyId),
                windowStartedAt, expiresAt, cancellationToken);

            var attemptsIncludingCurrent = priorAttempts + 1;
            var allowed = attemptsIncludingCurrent <= policy.Limit;
            var remaining = (int)Math.Max(0, policy.Limit - attemptsIncludingCurrent);
            await transaction.CommitAsync(cancellationToken);
            return new(
                allowed,
                allowed ? null : windowEndsAt,
                remaining);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new IdentityRateLimitUnavailableException(error);
        }
    }

    public async Task<int> DeleteExpiredBatchAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        try
        {
            await using var connection = await connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            _ = await LockAllStateAsync(connection, transaction, cancellationToken);
            var deleted = await DeleteExpiredRowsAsync(
                connection, transaction, batchSize, DateTimeOffset.UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new IdentityRateLimitUnavailableException(error);
        }
    }

    private void ValidateRequiredSecrets(KeyState state)
    {
        if (string.IsNullOrEmpty(state.ActiveKeyId)) throw new RateLimitProtocolException();
        foreach (var row in state.Rows.Where(row => row.State is "active" or "retained"))
            if (!securityOptions.SecretsByKeyId.ContainsKey(row.KeyId))
                throw new RateLimitProtocolException();
    }

    private static async Task<KeyState> LockAllStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT key_id, state, changed_at
            FROM app.lock_identity_rate_limit_key_state()
            ORDER BY key_id
            """, connection, transaction);
        var rows = new List<KeyStateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        var active = rows.Where(row => row.State == "active").Select(row => row.KeyId).ToArray();
        return new(active.Length == 1 ? active[0] : string.Empty, rows);
    }

    private static async Task MaterializeCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BucketCandidate candidate,
        DateTimeOffset windowStartedAt,
        DateTimeOffset windowEndsAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO app.identity_rate_limits
                (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at)
            VALUES
                (@key_id, @bucket_hmac, @window_started_at, @window_ends_at, 0, @expires_at)
            ON CONFLICT (key_id, bucket_hmac, window_started_at) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("key_id", candidate.KeyId);
        command.Parameters.Add("bucket_hmac", NpgsqlDbType.Bytea).Value = candidate.BucketHmac;
        command.Parameters.AddWithValue("window_started_at", windowStartedAt);
        command.Parameters.AddWithValue("window_ends_at", windowEndsAt);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<LockedBucket>> LockCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<BucketCandidate> candidates,
        DateTimeOffset windowStartedAt,
        CancellationToken cancellationToken)
    {
        var predicates = candidates.Select((_, index) =>
            $"(key_id = @key_{index} AND bucket_hmac = @bucket_{index})");
        await using var command = new NpgsqlCommand($"""
            SELECT key_id, attempt_count
            FROM app.identity_rate_limits
            WHERE window_started_at = @window_started_at
              AND ({string.Join(" OR ", predicates)})
            ORDER BY key_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("window_started_at", windowStartedAt);
        for (var index = 0; index < candidates.Count; index++)
        {
            command.Parameters.AddWithValue($"key_{index}", candidates[index].KeyId);
            command.Parameters.Add($"bucket_{index}", NpgsqlDbType.Bytea).Value = candidates[index].BucketHmac;
        }
        var rows = new List<LockedBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetInt32(1)));
        return rows;
    }

    private static async Task IncrementActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BucketCandidate active,
        DateTimeOffset windowStartedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE app.identity_rate_limits
            SET attempt_count = attempt_count + 1,
                expires_at = GREATEST(expires_at, @expires_at)
            WHERE key_id = @key_id
              AND bucket_hmac = @bucket_hmac
              AND window_started_at = @window_started_at
            """, connection, transaction);
        command.Parameters.AddWithValue("key_id", active.KeyId);
        command.Parameters.Add("bucket_hmac", NpgsqlDbType.Bytea).Value = active.BucketHmac;
        command.Parameters.AddWithValue("window_started_at", windowStartedAt);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new RateLimitProtocolException();
    }

    private static async Task<int> DeleteExpiredRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH expired AS (
                SELECT key_id, bucket_hmac, window_started_at
                FROM app.identity_rate_limits
                WHERE expires_at <= @now
                ORDER BY key_id, bucket_hmac, window_started_at
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            )
            DELETE FROM app.identity_rate_limits target
            USING expired
            WHERE target.key_id = expired.key_id
              AND target.bucket_hmac = expired.bucket_hmac
              AND target.window_started_at = expired.window_started_at
            """, connection, transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch_size", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset CanonicalWindowStart(DateTimeOffset now, TimeSpan window)
    {
        var seconds = now.ToUnixTimeSeconds();
        var width = checked((long)window.TotalSeconds);
        return DateTimeOffset.FromUnixTimeSeconds(seconds - (seconds % width));
    }

    private static byte[] BuildBucketMaterial(
        IdentityRateLimitRequest request,
        PolicyDefinition policy,
        DateTimeOffset windowStartedAt)
    {
        var subject = Encoding.UTF8.GetBytes(request.Subject);
        var ipValue = policy.UsesIp ? request.NormalizedIp! : string.Empty;
        var ip = Encoding.UTF8.GetBytes(ipValue);
        var body = Encoding.UTF8.GetBytes(
            $"policy:{request.Policy}\nsubject:{subject.Length}:{request.Subject}\nip:{ip.Length}:{ipValue}\nwindow:{windowStartedAt.ToUnixTimeSeconds()}");
        var material = new byte[DomainSeparator.Length + body.Length];
        DomainSeparator.CopyTo(material, 0);
        body.CopyTo(material, DomainSeparator.Length);
        return material;
    }

    private sealed record PolicyDefinition(int Limit, TimeSpan Window, bool UsesIp)
    {
        public static PolicyDefinition Parse(IdentityRateLimitRequest request)
        {
            if (string.IsNullOrEmpty(request.Subject) || request.Subject.Length > 512)
                throw new ArgumentException("Invalid rate-limit subject.", nameof(request));
            PolicyDefinition policy = request.Policy switch
            {
                "login" => new(10, TimeSpan.FromMinutes(1), true),
                "reauth" => new(5, TimeSpan.FromMinutes(1), true),
                "change-password" => new(5, TimeSpan.FromMinutes(15), false),
                "admin-mutation" => new(30, TimeSpan.FromMinutes(1), false),
                _ => throw new ArgumentException("Invalid rate-limit policy.", nameof(request))
            };
            if (policy.UsesIp && (string.IsNullOrEmpty(request.NormalizedIp) || request.NormalizedIp.Length > 128))
                throw new ArgumentException("Invalid normalized IP.", nameof(request));
            return policy;
        }
    }

    private sealed record KeyState(string ActiveKeyId, IReadOnlyList<KeyStateRow> Rows);
    private sealed record KeyStateRow(string KeyId, string State, DateTimeOffset ChangedAt);
    private sealed record BucketCandidate(string KeyId, byte[] BucketHmac);
    private sealed record LockedBucket(string KeyId, int AttemptCount);
    private sealed class RateLimitProtocolException : InvalidOperationException;
}
