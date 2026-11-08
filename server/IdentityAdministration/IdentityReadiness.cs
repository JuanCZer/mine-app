using Npgsql;

namespace Notes.Api.IdentityAdministration;

public sealed record IdentityRateLimitKeyStateSnapshot(string ActiveKeyId, IReadOnlyList<string> RetainedKeyIds);
public sealed record IdentityReadinessResult(bool IsReady, string Code, IReadOnlyList<string> MissingKeyIds);

public interface IIdentityRateLimitKeyStateReader
{
    Task<IdentityRateLimitKeyStateSnapshot> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);
}

public sealed class PostgresIdentityRateLimitKeyStateReader : IIdentityRateLimitKeyStateReader
{
    public async Task<IdentityRateLimitKeyStateSnapshot> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT key_id, state
            FROM app.identity_rate_limit_key_state
            WHERE state IN ('active', 'retained')
            ORDER BY key_id
            """, connection, transaction);
        var active = new List<string>();
        var retained = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var keyId = reader.GetString(0);
            if (reader.GetString(1) == "active") active.Add(keyId); else retained.Add(keyId);
        }
        return new(active.Count == 1 ? active[0] : string.Empty, retained);
    }
}

public interface IIdentityReadiness
{
    Task<IdentityReadinessResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed class IdentityReadiness(
    IdentityAdminConnection connectionFactory,
    IIdentityRateLimitKeyStateReader reader,
    IdentitySecurityOptions options) : IIdentityReadiness
{
    public async Task<IdentityReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var snapshot = await reader.ReadAsync(connection, transaction, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return Evaluate(options, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(false, "identity_readiness_unavailable", Array.Empty<string>());
        }
    }

    public static IdentityReadinessResult Evaluate(IdentitySecurityOptions options, IdentityRateLimitKeyStateSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.ActiveKeyId))
            return new(false, "identity_rate_limit_active_key_invalid", Array.Empty<string>());
        var required = new[] { snapshot.ActiveKeyId }.Concat(snapshot.RetainedKeyIds).Distinct(StringComparer.Ordinal);
        var missing = required.Where(key => !options.SecretsByKeyId.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
        return missing.Length == 0
            ? new(true, "ready", Array.Empty<string>())
            : new(false, "identity_rate_limit_key_missing", missing);
    }
}
