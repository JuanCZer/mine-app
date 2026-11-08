using System.Globalization;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public interface IIdentityAdministrationStore
{
    Task<AdminUserDirectoryResponse> ListUsersAsync(
        AdminUserDirectoryQuery query,
        CancellationToken cancellationToken);

    Task RecordSuccessfulListAsync(
        AdminUserDirectoryAudit audit,
        CancellationToken cancellationToken);

    Task<AdminCreateUserOutcome> CreateUserAsync(
        AdminCreateUserCommand command,
        AdminTemporaryCredentialFactory credentialFactory,
        CancellationToken cancellationToken);

    Task<int> TombstoneExpiredReceiptsAsync(
        int batchSize,
        CancellationToken cancellationToken);

    Task<AdminUserMutationOutcome> MutateUserAsync(
        AdminUserMutationCommand command,
        CancellationToken cancellationToken);

    Task<AdminResetOutcome> ResetPasswordAsync(AdminResetCommand command, IPasswordHasher<ApplicationUser> hasher, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ChangePasswordOutcome> ChangePasswordAsync(
        ChangePasswordCommand command,
        IPasswordHasher<ApplicationUser> hasher,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Password change is not supported by this store.");
}

public sealed record ChangePasswordCommand(
    Guid ActorUserId,
    string CurrentPassword,
    string NewPassword,
    DateTimeOffset Now,
    string? CorrelationId);

public enum ChangePasswordOutcome
{
    Succeeded,
    CurrentPasswordInvalid,
    PasswordReuse,
    CredentialStateChanged
}

public sealed partial class IdentityAdministrationStore : IIdentityAdministrationStore
{
    private const int OpportunisticCleanupBatchSize = 16;
    private readonly IdentityAdminConnection _connectionFactory;
    private readonly IdentityCreationReceiptProtocol _receiptProtocol;
    private readonly TimeProvider _clock;

    public IdentityAdministrationStore(
        IdentityAdminConnection connectionFactory,
        IDataProtectionProvider protection,
        TimeProvider clock)
    {
        _connectionFactory = connectionFactory;
        _receiptProtocol = new IdentityCreationReceiptProtocol(protection);
        _clock = clock;
    }

    public async Task<AdminUserDirectoryResponse> ListUsersAsync(
        AdminUserDirectoryQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var total = await CountAsync(connection, transaction, query, cancellationToken);
        var items = await ReadPageAsync(connection, transaction, query, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(items, query.Page, query.PageSize, total);
    }

    public async Task RecordSuccessfulListAsync(
        AdminUserDirectoryAudit audit,
        CancellationToken cancellationToken)
    {
        if (audit.EntityType != "user_directory" || audit.EntityId != "user-directory")
            throw new ArgumentException("Invalid user-directory audit identity.", nameof(audit));

        var context = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["page"] = audit.Query.Page,
            ["pageSize"] = audit.Query.PageSize,
            ["q"] = SanitizeAuditValue(audit.Query.Search, 100),
            ["role"] = audit.Query.Role,
            ["status"] = audit.Query.Status
        });
        await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO app.audit_events
                (actor_user_id, action, outcome, entity_type, entity_id,
                 correlation_id, source_ip_hash, changes, security_context)
            VALUES
                (@actor_user_id, 'admin.users.list', 'succeeded', 'user_directory',
                 'user-directory', @correlation_id, NULL, '{}'::jsonb, @security_context)
            """, connection, transaction);
        command.Parameters.AddWithValue("actor_user_id", audit.ActorUserId);
        command.Parameters.Add("correlation_id", NpgsqlDbType.Varchar).Value =
            SanitizeAuditValue(audit.CorrelationId?.Trim(), 128) ?? (object)DBNull.Value;
        command.Parameters.Add("security_context", NpgsqlDbType.Jsonb).Value = context;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AdminCreateUserOutcome> CreateUserAsync(
        AdminCreateUserCommand command,
        AdminTemporaryCredentialFactory credentialFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        var keyHash = IdentityCreationProtocol.KeyHash(command.ActorUserId, command.IdempotencyKey);
        var payloadHash = command.Intent.PayloadHash();
        try
        {
            await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await SetActorAsync(connection, transaction, command.ActorUserId, cancellationToken);
            await AdvisoryLockAsync(connection, transaction, keyHash, cancellationToken);
            _ = await TombstoneExpiredReceiptsAsync(
                connection, transaction, OpportunisticCleanupBatchSize, command.Now, cancellationToken);

            var receipt = await ReadReceiptAsync(connection, transaction, keyHash, cancellationToken);
            if (receipt is not null)
            {
                var outcome = _receiptProtocol.ResolveExisting(command, receipt);
                if (outcome.Kind == AdminCreateUserOutcomeKind.IdempotencyResultExpired
                    && receipt.ProtectedResponse is not null)
                {
                    await TombstoneReceiptAsync(connection, transaction, keyHash, cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return outcome;
            }

            if (await UsernameExistsAsync(connection, transaction, command.Intent.NormalizedUsername, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(AdminCreateUserOutcomeKind.UsernameConflict);
            }

            var preparation = _receiptProtocol.CreateNew(command, credentialFactory);
            await InsertUserAsync(connection, transaction, command, preparation.Credential,
                preparation.CreatedAt, cancellationToken);
            await InsertMembershipAsync(connection, transaction, preparation.Credential.UserId,
                command.Intent.Role, cancellationToken);
            await SetConstraintsImmediateAsync(connection, transaction, cancellationToken);
            await InsertCreateAuditAsync(connection, transaction, command,
                preparation.Credential.UserId, cancellationToken);
            await InsertReceiptAsync(connection, transaction, command, keyHash, payloadHash,
                preparation.Credential.UserId, preparation.ProtectedResponse,
                preparation.CreatedAt, preparation.ExpiresAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return preparation.Outcome;
        }
        catch (PostgresException error) when (
            IdentityCreationProtocol.IsNormalizedUsernameConflict(
                error.SqlState, error.SchemaName, error.TableName, error.ConstraintName))
        {
            return new(AdminCreateUserOutcomeKind.UsernameConflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException error)
        {
            throw new IdentityAdminUnavailableException(error);
        }
    }

    public async Task<int> TombstoneExpiredReceiptsAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        ValidateBatchSize(batchSize);
        await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var count = await TombstoneExpiredReceiptsAsync(
            connection, transaction, batchSize, _clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<AdminUserMutationOutcome> MutateUserAsync(
        AdminUserMutationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await SetActorAsync(connection, transaction, command.ActorUserId, cancellationToken);
            if (!await LockMutationTargetAsync(
                    connection, transaction, command.Intent.TargetUserId, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(AdminUserMutationOutcomeKind.NotFound);
            }
            await LockMutationMembershipsAsync(
                connection, transaction, command.Intent.TargetUserId, cancellationToken);
            await LockAdministrationAsync(connection, transaction, cancellationToken);

            // READ COMMITTED gives this statement a fresh snapshot after all row
            // and advisory waits. Integrity is deliberately checked separately
            // from target existence so corrupt memberships never look missing.
            var current = await ReadMutationTargetAsync(
                connection, transaction, command.Intent.TargetUserId, cancellationToken)
                ?? throw new IdentityAdminUnavailableException();
            var memberships = await ReadMutationMembershipsAsync(
                connection, transaction, command.Intent.TargetUserId, cancellationToken);
            if (memberships.Count != 1 || !memberships[0].IsCanonical)
                throw new IdentityAdminUnavailableException();
            current = current with { Role = memberships[0].Name! };
            if (current.ManagementVersion != command.Intent.ExpectedManagementVersion)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(AdminUserMutationOutcomeKind.StaleManagementVersion);
            }
            if (command.ActorUserId == current.Id)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(AdminUserMutationOutcomeKind.SelfManagementForbidden);
            }

            var removesActiveAdministrator = current.IsActive
                && current.Role == "administrador"
                && (command.Kind == AdminUserMutationKind.Role
                    ? command.Intent.Role != "administrador"
                    : command.Intent.IsActive == false);
            if (removesActiveAdministrator
                && await CountActiveAdministratorsAsync(connection, transaction, cancellationToken) <= 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(AdminUserMutationOutcomeKind.LastActiveAdministrator);
            }

            var noOp = command.Kind == AdminUserMutationKind.Role
                ? current.Role == command.Intent.Role
                : current.IsActive == command.Intent.IsActive;
            if (noOp)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(AdminUserMutationOutcomeKind.NoOp, current.ToSummary());
            }

            var updatedAt = ToPostgresTimestamp(command.Now);
            var concurrencyStamp = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var securityStamp = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (command.Kind == AdminUserMutationKind.Role)
                await ReplaceRoleAsync(connection, transaction, current.Id, command.Intent.Role!, cancellationToken);
            var changed = await UpdateMutationStateAsync(
                connection, transaction, command, current, updatedAt,
                concurrencyStamp, securityStamp, cancellationToken);
            if (!changed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(AdminUserMutationOutcomeKind.StaleManagementVersion);
            }
            await SetConstraintsImmediateAsync(connection, transaction, cancellationToken);
            await InsertMutationAuditAsync(
                connection, transaction, command, current, updatedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AdminUserMutationOutcomeKind.Updated,
                current.ToChangedSummary(command, updatedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityAdminUnavailableException)
        {
            throw;
        }
        catch (PostgresException error) when (TryMapGuardFailure(error, out var guardOutcome))
        {
            return new(guardOutcome);
        }
        catch (Exception error)
        {
            throw new IdentityAdminUnavailableException(error);
        }
    }

    public async Task<ChangePasswordOutcome> ChangePasswordAsync(
        ChangePasswordCommand command,
        IPasswordHasher<ApplicationUser> hasher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(hasher);
        try
        {
            await using var connection = await _connectionFactory.OpenVerifiedAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await SetActorAsync(connection, transaction, command.ActorUserId, cancellationToken);
            var observed = await ReadPasswordStateAsync(
                connection, transaction, command.ActorUserId, cancellationToken);
            if (observed is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChangePasswordOutcome.CredentialStateChanged;
            }
            // The conditional write requires an exact, non-null concurrency
            // token. A legacy/corrupt row must fail closed before password
            // verification, hashing, or UPDATE execution.
            if (string.IsNullOrEmpty(observed.ConcurrencyStamp))
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChangePasswordOutcome.CredentialStateChanged;
            }

            var user = new ApplicationUser
            {
                Id = command.ActorUserId,
                PasswordHash = observed.PasswordHash,
                ConcurrencyStamp = observed.ConcurrencyStamp,
                SecurityStamp = observed.SecurityStamp,
                IsActive = observed.IsActive,
                MustChangePassword = observed.MustChangePassword,
                ManagementVersion = observed.ManagementVersion
            };
            if (observed.PasswordHash is null
                || hasher.VerifyHashedPassword(user, observed.PasswordHash, command.CurrentPassword)
                    == PasswordVerificationResult.Failed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChangePasswordOutcome.CurrentPasswordInvalid;
            }
            if (hasher.VerifyHashedPassword(user, observed.PasswordHash, command.NewPassword)
                != PasswordVerificationResult.Failed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ChangePasswordOutcome.PasswordReuse;
            }

            var newHash = hasher.HashPassword(user, command.NewPassword);
            var concurrencyStamp = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var securityStamp = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var changedAt = ToPostgresTimestamp(command.Now);
            var affectedRows = await UpdatePasswordStateAsync(
                connection, transaction, command.ActorUserId, observed, newHash,
                concurrencyStamp, securityStamp, changedAt, cancellationToken);
            var updateOutcome = ClassifyPasswordUpdateCount(affectedRows);
            if (updateOutcome is { } rejected)
            {
                await transaction.RollbackAsync(cancellationToken);
                return rejected;
            }
            await InsertPasswordChangeAuditAsync(
                connection, transaction, command, observed.ManagementVersion,
                changedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ChangePasswordOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityAdminUnavailableException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new IdentityAdminUnavailableException(error);
        }
    }

    private static async Task<PasswordState?> ReadPasswordStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT "PasswordHash", "ConcurrencyStamp", "SecurityStamp", "IsActive",
                   "ManagementVersion", "MustChangePassword", "PasswordChangedAt", "UpdatedAt"
            FROM public."AspNetUsers"
            WHERE "Id" = @actor AND "IsActive" = TRUE
            FOR KEY SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3), reader.GetInt64(4), reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static ChangePasswordOutcome? ClassifyPasswordUpdateCount(int affectedRows) =>
        affectedRows switch
        {
            0 => ChangePasswordOutcome.CredentialStateChanged,
            1 => null,
            _ => throw new IdentityAdminUnavailableException()
        };

    private static async Task<int> UpdatePasswordStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actor,
        PasswordState observed,
        string newHash,
        string concurrencyStamp,
        string securityStamp,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public."AspNetUsers"
            SET "PasswordHash" = @password_hash,
                "PasswordChangedAt" = @changed_at,
                "MustChangePassword" = FALSE,
                "ManagementVersion" = "ManagementVersion" + 1,
                "UpdatedAt" = @changed_at,
                "ConcurrencyStamp" = @concurrency_stamp,
                "SecurityStamp" = @security_stamp
            WHERE "Id" = @actor
              AND "IsActive" = TRUE
              AND "PasswordHash" IS NOT DISTINCT FROM @observed_password_hash
              AND "ConcurrencyStamp" = @observed_concurrency_stamp
              AND "ManagementVersion" = @observed_management_version
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.Add("observed_password_hash", NpgsqlDbType.Text).Value =
            observed.PasswordHash ?? (object)DBNull.Value;
        command.Parameters.AddWithValue("observed_concurrency_stamp", observed.ConcurrencyStamp!);
        command.Parameters.AddWithValue("observed_management_version", observed.ManagementVersion);
        command.Parameters.AddWithValue("password_hash", newHash);
        command.Parameters.AddWithValue("changed_at", changedAt);
        command.Parameters.AddWithValue("concurrency_stamp", concurrencyStamp);
        command.Parameters.AddWithValue("security_stamp", securityStamp);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPasswordChangeAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ChangePasswordCommand command,
        long oldVersion,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO app.audit_events
                (actor_user_id, action, outcome, entity_type, entity_id,
                 correlation_id, source_ip_hash, changes, security_context)
            VALUES
                (@actor, 'auth.password.change', 'succeeded', 'user', @entity,
                 @correlation, NULL,
                 jsonb_build_object(
                    'mustChangePassword', FALSE,
                    'oldManagementVersion', @old_version,
                    'newManagementVersion', @new_version,
                    'passwordChangedAt', @changed_at),
                 '{}'::jsonb)
            """, connection, transaction);
        audit.Parameters.AddWithValue("actor", command.ActorUserId);
        audit.Parameters.AddWithValue("entity", command.ActorUserId.ToString("D"));
        audit.Parameters.Add("correlation", NpgsqlDbType.Varchar).Value =
            SanitizeAuditValue(command.CorrelationId, 128) ?? (object)DBNull.Value;
        audit.Parameters.AddWithValue("old_version", oldVersion);
        audit.Parameters.AddWithValue("new_version", checked(oldVersion + 1));
        audit.Parameters.AddWithValue("changed_at", changedAt);
        if (await audit.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Password change audit was not persisted.");
    }

    private sealed record PasswordState(
        string? PasswordHash,
        string? ConcurrencyStamp,
        string? SecurityStamp,
        bool IsActive,
        long ManagementVersion,
        bool MustChangePassword,
        DateTimeOffset? PasswordChangedAt,
        DateTimeOffset UpdatedAt);

    private static async Task AdvisoryLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, byte[] keyHash,
        CancellationToken cancellationToken)
    {
        var lockId = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(keyHash.AsSpan(0, 8));
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(@lock_id)", connection, transaction);
        command.Parameters.AddWithValue("lock_id", lockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LockAdministrationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(714205736012345678::bigint)",
            connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> LockMutationTargetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT "Id"
            FROM public."AspNetUsers"
            WHERE "Id" = @target_user_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("target_user_id", targetUserId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    private static async Task LockMutationMembershipsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT role_id
            FROM app.lock_identity_user_roles(@target_user_id) AS role_id
            """, connection, transaction);
        command.Parameters.AddWithValue("target_user_id", targetUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { }
    }

    private static async Task<AdminMutationTarget?> ReadMutationTargetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT u."Id", u."UserName", u."DisplayName", u."IsActive",
                   u."MustChangePassword", u."CreatedAt", u."UpdatedAt", u."ManagementVersion"
            FROM public."AspNetUsers" AS u
            WHERE u."Id" = @target_user_id
            """, connection, transaction);
        command.Parameters.AddWithValue("target_user_id", targetUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), string.Empty,
            reader.GetBoolean(3), reader.GetBoolean(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7));
    }

    private static async Task<IReadOnlyList<AdminMutationMembership>> ReadMutationMembershipsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT membership."RoleId", role."Name", role."NormalizedName"
            FROM public."AspNetUserRoles" AS membership
            LEFT JOIN public."AspNetRoles" AS role ON role."Id" = membership."RoleId"
            WHERE membership."UserId" = @target_user_id
            ORDER BY membership."RoleId"
            """, connection, transaction);
        command.Parameters.AddWithValue("target_user_id", targetUserId);
        var memberships = new List<AdminMutationMembership>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memberships.Add(new(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return memberships;
    }

    private static async Task<long> CountActiveAdministratorsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM public."AspNetUsers" AS u
            INNER JOIN public."AspNetUserRoles" AS ur ON ur."UserId" = u."Id"
            INNER JOIN public."AspNetRoles" AS r ON r."Id" = ur."RoleId"
            WHERE u."IsActive" = TRUE
              AND r."Name" = 'administrador'
              AND r."NormalizedName" = 'ADMINISTRADOR'
            """, connection, transaction);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task ReplaceRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetUserId, string role,
        CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM public.\"AspNetUserRoles\" WHERE \"UserId\" = @target_user_id",
            connection, transaction))
        {
            delete.Parameters.AddWithValue("target_user_id", targetUserId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Canonical role membership is unavailable.");
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO public."AspNetUserRoles" ("UserId", "RoleId")
            SELECT @target_user_id, "Id"
            FROM public."AspNetRoles"
            WHERE "Name" = @role AND "NormalizedName" = @normalized_role
            """, connection, transaction);
        insert.Parameters.AddWithValue("target_user_id", targetUserId);
        insert.Parameters.AddWithValue("role", role);
        insert.Parameters.AddWithValue("normalized_role", role.ToUpperInvariant());
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Canonical role is unavailable.");
    }

    private static async Task<bool> UpdateMutationStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        AdminUserMutationCommand command, AdminMutationTarget current, DateTimeOffset updatedAt,
        string concurrencyStamp, string securityStamp, CancellationToken cancellationToken)
    {
        var sql = command.Kind == AdminUserMutationKind.Status
            ? """
                UPDATE public."AspNetUsers"
                SET "IsActive" = @is_active, "UpdatedAt" = @updated_at,
                    "ManagementVersion" = "ManagementVersion" + 1,
                    "ConcurrencyStamp" = @concurrency_stamp,
                    "SecurityStamp" = @security_stamp
                WHERE "Id" = @target_user_id AND "ManagementVersion" = @expected_version
                """
            : """
                UPDATE public."AspNetUsers"
                SET "UpdatedAt" = @updated_at,
                    "ManagementVersion" = "ManagementVersion" + 1,
                    "ConcurrencyStamp" = @concurrency_stamp,
                    "SecurityStamp" = @security_stamp
                WHERE "Id" = @target_user_id AND "ManagementVersion" = @expected_version
                """;
        await using var update = new NpgsqlCommand(sql, connection, transaction);
        update.Parameters.AddWithValue("target_user_id", current.Id);
        update.Parameters.AddWithValue("expected_version", command.Intent.ExpectedManagementVersion);
        update.Parameters.AddWithValue("updated_at", updatedAt);
        update.Parameters.AddWithValue("concurrency_stamp", concurrencyStamp);
        update.Parameters.AddWithValue("security_stamp", securityStamp);
        if (command.Kind == AdminUserMutationKind.Status)
            update.Parameters.AddWithValue("is_active", command.Intent.IsActive!.Value);
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertMutationAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        AdminUserMutationCommand command, AdminMutationTarget current, DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var newVersion = checked(current.ManagementVersion + 1);
        var action = command.Kind == AdminUserMutationKind.Role
            ? "admin.user.role.change" : "admin.user.status.change";
        object oldValue = command.Kind == AdminUserMutationKind.Role
            ? current.Role : current.IsActive;
        object newValue = command.Kind == AdminUserMutationKind.Role
            ? command.Intent.Role! : command.Intent.IsActive!.Value;
        await using var audit = new NpgsqlCommand("""
            INSERT INTO app.audit_events
                (actor_user_id, action, outcome, entity_type, entity_id, correlation_id,
                 source_ip_hash, changes, security_context)
            VALUES
                (@actor, @action, 'succeeded', 'user', @entity, @correlation, NULL,
                 jsonb_build_object(
                    'field', @field, 'oldValue', @old_value, 'newValue', @new_value,
                    'oldManagementVersion', @old_version, 'newManagementVersion', @new_version,
                    'updatedAt', @updated_at),
                 '{}'::jsonb)
            """, connection, transaction);
        audit.Parameters.AddWithValue("actor", command.ActorUserId);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", current.Id.ToString("D"));
        audit.Parameters.Add("correlation", NpgsqlDbType.Varchar).Value =
            SanitizeAuditValue(command.CorrelationId, 128) ?? (object)DBNull.Value;
        audit.Parameters.AddWithValue("field",
            command.Kind == AdminUserMutationKind.Role ? "role" : "isActive");
        audit.Parameters.AddWithValue("old_value", oldValue);
        audit.Parameters.AddWithValue("new_value", newValue);
        audit.Parameters.AddWithValue("old_version", current.ManagementVersion.ToString(CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue("new_version", newVersion.ToString(CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue("updated_at", updatedAt);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ToPostgresTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime();

    private static bool TryMapGuardFailure(
        PostgresException error, out AdminUserMutationOutcomeKind outcome)
    {
        outcome = error.MessageText switch
        {
            "identity_self_management_forbidden" =>
                AdminUserMutationOutcomeKind.SelfManagementForbidden,
            "identity_last_active_admin" =>
                AdminUserMutationOutcomeKind.LastActiveAdministrator,
            _ => default
        };
        return error.SqlState == PostgresErrorCodes.CheckViolation
            && error.MessageText is "identity_self_management_forbidden" or "identity_last_active_admin";
    }

    private sealed record AdminMutationMembership(Guid RoleId, string? Name, string? NormalizedName)
    {
        public bool IsCanonical => (Name, NormalizedName) is
            ("operador", "OPERADOR") or
            ("validador", "VALIDADOR") or
            ("administrador", "ADMINISTRADOR");
    }

    private sealed record AdminMutationTarget(
        Guid Id,
        string Username,
        string DisplayName,
        string Role,
        bool IsActive,
        bool MustChangePassword,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        long ManagementVersion)
    {
        public AdminUserSummary ToSummary() => new(
            Id.ToString("D"), Username, DisplayName, Role, IsActive, MustChangePassword,
            CreatedAt, UpdatedAt, ManagementVersion.ToString(CultureInfo.InvariantCulture));

        public AdminUserSummary ToChangedSummary(AdminUserMutationCommand command, DateTimeOffset updatedAt) => new(
            Id.ToString("D"), Username, DisplayName,
            command.Kind == AdminUserMutationKind.Role ? command.Intent.Role! : Role,
            command.Kind == AdminUserMutationKind.Status ? command.Intent.IsActive!.Value : IsActive,
            MustChangePassword, CreatedAt, updatedAt,
            checked(ManagementVersion + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static async Task SetActorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('app.actor_user_id', @actor, true)", connection, transaction);
        command.Parameters.AddWithValue("actor", actor.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AdminCreateReceiptSnapshot?> ReadReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, byte[] keyHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at
            FROM app.identity_idempotency_receipts
            WHERE key_sha256 = @key_sha256
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add("key_sha256", NpgsqlDbType.Bytea).Value = keyHash;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetFieldValue<byte[]>(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    private static async Task<bool> UsernameExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.\"AspNetUsers\" WHERE \"NormalizedUserName\" = @name)",
            connection, transaction);
        command.Parameters.AddWithValue("name", normalizedUsername);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task InsertUserAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AdminCreateUserCommand value,
        AdminTemporaryCredential credential,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public."AspNetUsers"
                ("Id", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp",
                 "ConcurrencyStamp", "DisplayName", "IsActive", "CreatedAt", "UpdatedAt",
                 "MustChangePassword", "PasswordChangedAt", "ManagementVersion")
            VALUES
                (@id, @username, @normalized_username, @password_hash, @security_stamp,
                 @concurrency_stamp, @display_name, TRUE, @created_at, @updated_at,
                 TRUE, NULL, 1)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", credential.UserId);
        command.Parameters.AddWithValue("username", value.Intent.Username);
        command.Parameters.AddWithValue("normalized_username", value.Intent.NormalizedUsername);
        command.Parameters.AddWithValue("password_hash", credential.PasswordHash);
        command.Parameters.AddWithValue("security_stamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("concurrency_stamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("display_name", value.Intent.DisplayName);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMembershipAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string role,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public."AspNetUserRoles" ("UserId", "RoleId")
            SELECT @user_id, "Id" FROM public."AspNetRoles"
            WHERE "Name" = @role AND "NormalizedName" = @normalized_role
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("normalized_role", role.ToUpperInvariant());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Canonical role is unavailable.");
    }

    private static async Task SetConstraintsImmediateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCreateAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AdminCreateUserCommand value,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO app.audit_events
                (actor_user_id, action, outcome, entity_type, entity_id, correlation_id,
                 source_ip_hash, changes, security_context)
            VALUES
                (@actor, 'admin.user.create', 'succeeded', 'user', @entity, @correlation,
                 NULL, jsonb_build_object('role', @role, 'isActive', TRUE, 'mustChangePassword', TRUE),
                 '{}'::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", value.ActorUserId);
        command.Parameters.AddWithValue("entity", userId.ToString("D"));
        command.Parameters.Add("correlation", NpgsqlDbType.Varchar).Value =
            SanitizeAuditValue(value.CorrelationId, 128) ?? (object)DBNull.Value;
        command.Parameters.AddWithValue("role", value.Intent.Role);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AdminCreateUserCommand value,
        byte[] keyHash, byte[] payloadHash, Guid userId, byte[] protectedResponse,
        DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO app.identity_idempotency_receipts
                (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at)
            VALUES (@key, @actor, @payload, @user, @response, @created, @expires)
            """, connection, transaction);
        command.Parameters.Add("key", NpgsqlDbType.Bytea).Value = keyHash;
        command.Parameters.AddWithValue("actor", value.ActorUserId);
        command.Parameters.Add("payload", NpgsqlDbType.Bytea).Value = payloadHash;
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.Add("response", NpgsqlDbType.Bytea).Value = protectedResponse;
        command.Parameters.AddWithValue("created", now);
        command.Parameters.AddWithValue("expires", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TombstoneReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, byte[] keyHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE app.identity_idempotency_receipts SET protected_response = NULL
            WHERE key_sha256 = @key AND protected_response IS NOT NULL
            """, connection, transaction);
        command.Parameters.Add("key", NpgsqlDbType.Bytea).Value = keyHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> TombstoneExpiredReceiptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int batchSize,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ValidateBatchSize(batchSize);
        await using var command = new NpgsqlCommand("""
            WITH expired AS (
                SELECT key_sha256
                FROM app.identity_idempotency_receipts
                WHERE protected_response IS NOT NULL AND expires_at <= @now
                ORDER BY expires_at, key_sha256
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            )
            UPDATE app.identity_idempotency_receipts AS receipt
            SET protected_response = NULL
            FROM expired
            WHERE receipt.key_sha256 = expired.key_sha256
            """, connection, transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch_size", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateBatchSize(int batchSize)
    {
        if (batchSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(batchSize));
    }

    public static string EscapeLikeLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public static string? SanitizeAuditValue(string? value, int maximumRunes)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var result = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control) continue;
            if (count++ == maximumRunes) break;
            result.Append(rune.ToString());
        }
        return result.Length == 0 ? null : result.ToString();
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminUserDirectoryQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            SELECT count(*)
            FROM public."AspNetUsers" AS u
            INNER JOIN public."AspNetUserRoles" AS ur ON ur."UserId" = u."Id"
            INNER JOIN public."AspNetRoles" AS r ON r."Id" = ur."RoleId"
            WHERE (@pattern IS NULL OR u."UserName" ILIKE @pattern ESCAPE '\' OR u."DisplayName" ILIKE @pattern ESCAPE '\')
              AND (@role IS NULL OR r."Name" = @role)
              AND (@is_active IS NULL OR u."IsActive" = @is_active)
            """, connection, transaction, query);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<AdminUserSummary>> ReadPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminUserDirectoryQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            SELECT u."Id", u."UserName", u."DisplayName", r."Name", u."IsActive",
                   u."MustChangePassword", u."CreatedAt", u."UpdatedAt", u."ManagementVersion"
            FROM public."AspNetUsers" AS u
            INNER JOIN public."AspNetUserRoles" AS ur ON ur."UserId" = u."Id"
            INNER JOIN public."AspNetRoles" AS r ON r."Id" = ur."RoleId"
            WHERE (@pattern IS NULL OR u."UserName" ILIKE @pattern ESCAPE '\' OR u."DisplayName" ILIKE @pattern ESCAPE '\')
              AND (@role IS NULL OR r."Name" = @role)
              AND (@is_active IS NULL OR u."IsActive" = @is_active)
            ORDER BY u."NormalizedUserName", u."Id"
            LIMIT @page_size OFFSET @offset
            """, connection, transaction, query);
        command.Parameters.AddWithValue("page_size", query.PageSize);
        command.Parameters.AddWithValue("offset", checked((query.Page - 1L) * query.PageSize));
        var items = new List<AdminUserSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new(
                reader.GetGuid(0).ToString("D"),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt64(8).ToString(CultureInfo.InvariantCulture)));
        }
        return items;
    }

    private static NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminUserDirectoryQuery query)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add("pattern", NpgsqlDbType.Text).Value = query.Search is null
            ? DBNull.Value : $"%{EscapeLikeLiteral(query.Search)}%";
        command.Parameters.Add("role", NpgsqlDbType.Varchar).Value = query.Role ?? (object)DBNull.Value;
        command.Parameters.Add("is_active", NpgsqlDbType.Boolean).Value = query.IsActive ?? (object)DBNull.Value;
        return command;
    }
}
