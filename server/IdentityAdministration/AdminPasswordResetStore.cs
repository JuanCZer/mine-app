using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public sealed partial class IdentityAdministrationStore
{
    public async Task<AdminResetOutcome> ResetPasswordAsync(AdminResetCommand request,
        IPasswordHasher<ApplicationUser> hasher, CancellationToken cancellationToken)
    {
        if (request.Actor == request.Target) return new(AdminResetResult.SelfForbidden);
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes($"admin-password-reset:v1:{request.Actor:D}:{request.Key:D}"));
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Target:D}:{request.Version}"));
        try
        {
            await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await SetActorAsync(connection, tx, request.Actor, cancellationToken);
            await AdvisoryLockAsync(connection, tx, keyHash, cancellationToken);
            // Lock both user rows in UUID order. This serializes concurrent resets,
            // credential changes and role/status mutations without reversed lock order.
            await using (var lockUsers = new NpgsqlCommand("""
                SELECT "Id" FROM public."AspNetUsers"
                WHERE "Id" = @actor OR "Id" = @target
                ORDER BY "Id" FOR UPDATE
                """, connection, tx))
            {
                lockUsers.Parameters.AddWithValue("actor", request.Actor);
                lockUsers.Parameters.AddWithValue("target", request.Target);
                await using var locked = await lockUsers.ExecuteReaderAsync(cancellationToken);
                while (await locked.ReadAsync(cancellationToken)) { }
            }
            await LockMutationMembershipsAsync(connection, tx, request.Actor, cancellationToken);
            var actorRoles = await ReadMutationMembershipsAsync(connection, tx, request.Actor, cancellationToken);
            await using (var actor = new NpgsqlCommand("""
                SELECT "IsActive", "MustChangePassword", "SecurityStamp"
                FROM public."AspNetUsers" WHERE "Id" = @actor
                """, connection, tx))
            {
                actor.Parameters.AddWithValue("actor", request.Actor);
                await using var row = await actor.ExecuteReaderAsync(cancellationToken);
                if (!await row.ReadAsync(cancellationToken) || !row.GetBoolean(0) || row.GetBoolean(1)
                    || row.IsDBNull(2) || string.IsNullOrEmpty(request.ActorSecurityStamp)
                    || !string.Equals(row.GetString(2), request.ActorSecurityStamp, StringComparison.Ordinal)
                    || actorRoles.Count != 1 || !actorRoles[0].IsCanonical || actorRoles[0].Name != "administrador")
                    return new(AdminResetResult.ActorStateChanged);
            }
            var existing = await ReadReceiptAsync(connection, tx, keyHash, cancellationToken);
            if (existing is not null)
            {
                var matches = existing.ActorUserId == request.Actor && existing.UserId == request.Target
                    && CryptographicOperations.FixedTimeEquals(existing.PayloadHash, payloadHash);
                await tx.CommitAsync(cancellationToken);
                return new(matches ? AdminResetResult.ResultUnavailable : AdminResetResult.KeyReused);
            }
            if (!await LockMutationTargetAsync(connection, tx, request.Target, cancellationToken))
                return new(AdminResetResult.NotFound);
            var user = await ReadMutationTargetAsync(connection, tx, request.Target, cancellationToken)
                ?? throw new IdentityAdminUnavailableException();
            if (user.ManagementVersion != request.Version) return new(AdminResetResult.Stale);
            if (!user.IsActive) return new(AdminResetResult.Inactive);
            var temporary = IdentityCreationProtocol.GenerateTemporaryPassword();
            var hash = hasher.HashPassword(new ApplicationUser { Id = request.Target, UserName = user.Username }, temporary);
            var stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var concurrencyStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await using (var update = new NpgsqlCommand("""
                UPDATE public."AspNetUsers" SET "PasswordHash"=@hash, "SecurityStamp"=@stamp,
                    "ConcurrencyStamp"=@concurrency, "MustChangePassword"=TRUE,
                    "PasswordChangedAt"=@now, "UpdatedAt"=@now, "ManagementVersion"="ManagementVersion"+1
                WHERE "Id"=@id AND "IsActive"=TRUE AND "ManagementVersion"=@version
                """, connection, tx))
            {
                update.Parameters.AddWithValue("hash", hash);
                update.Parameters.AddWithValue("stamp", stamp);
                update.Parameters.AddWithValue("concurrency", concurrencyStamp);
                update.Parameters.AddWithValue("now", request.Now);
                update.Parameters.AddWithValue("id", request.Target);
                update.Parameters.AddWithValue("version", request.Version);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new IdentityAdminUnavailableException();
            }
            await using (var audit = new NpgsqlCommand("""
                INSERT INTO app.audit_events (actor_user_id, action, outcome, entity_type, entity_id,
                    correlation_id, source_ip_hash, changes, security_context)
                VALUES (@actor, 'admin.user.password_reset', 'succeeded', 'user', @entity, @correlation,
                    NULL, jsonb_build_object('mustChangePassword', TRUE, 'oldManagementVersion', @old,
                    'newManagementVersion', @new), '{}'::jsonb)
                """, connection, tx))
            {
                audit.Parameters.AddWithValue("actor", request.Actor);
                audit.Parameters.AddWithValue("entity", request.Target.ToString("D"));
                audit.Parameters.Add("correlation", NpgsqlDbType.Varchar).Value = SanitizeAuditValue(request.CorrelationId, 128) ?? (object)DBNull.Value;
                audit.Parameters.AddWithValue("old", request.Version);
                audit.Parameters.AddWithValue("new", checked(request.Version + 1));
                if (await audit.ExecuteNonQueryAsync(cancellationToken) != 1) throw new IdentityAdminUnavailableException();
            }
            await using (var receipt = new NpgsqlCommand("""
                INSERT INTO app.identity_idempotency_receipts
                    (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at)
                VALUES (@key, @actor, @payload, @user, NULL, @now, @expires)
                """, connection, tx))
            {
                receipt.Parameters.Add("key", NpgsqlDbType.Bytea).Value = keyHash;
                receipt.Parameters.AddWithValue("actor", request.Actor);
                receipt.Parameters.Add("payload", NpgsqlDbType.Bytea).Value = payloadHash;
                receipt.Parameters.AddWithValue("user", request.Target);
                receipt.Parameters.AddWithValue("now", request.Now);
                receipt.Parameters.AddWithValue("expires", request.Now.Add(IdentityCreationProtocol.ReceiptLifetime));
                if (await receipt.ExecuteNonQueryAsync(cancellationToken) != 1) throw new IdentityAdminUnavailableException();
            }
            await tx.CommitAsync(cancellationToken);
            return new(AdminResetResult.Succeeded, temporary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IdentityAdminUnavailableException) { throw; }
        catch (Exception error) { throw new IdentityAdminUnavailableException(error); }
    }
}
