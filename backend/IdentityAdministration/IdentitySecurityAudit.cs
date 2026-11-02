using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Notes.Api.IdentityAdministration;

public static class IdentitySecurityActions
{
    public const string AuthorizationReject = "auth.authorization.reject";
    public const string PasswordChangeRequiredReject = "auth.password_change_required.reject";
    public const string AdminStepUpReject = "auth.admin_stepup.reject";
    public const string CsrfReject = "auth.csrf.reject";
    public const string ValidationReject = "auth.validation.reject";
    public const string RateLimitReject = "identity.rate_limit.reject";
    public const string ConcurrencyReject = "identity.concurrency.reject";

    internal static readonly HashSet<string> All =
    [AuthorizationReject, PasswordChangeRequiredReject, AdminStepUpReject, CsrfReject,
        ValidationReject, RateLimitReject, ConcurrencyReject];
}

public sealed record IdentitySecurityRejection(
    string Action,
    string Code,
    Guid? ActorUserId,
    string EntityType,
    string EntityId,
    string Outcome,
    string? CorrelationId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record IdentitySecurityAuditRow(
    string Action,
    string Outcome,
    string EntityType,
    string EntityId,
    Guid? ActorUserId,
    string? CorrelationId,
    string ChangesJson,
    string SecurityContextJson);

public interface IIdentitySecurityAudit
{
    Task RecordRejectionAsync(IdentitySecurityRejection rejection, CancellationToken cancellationToken = default);
}

public interface IIdentitySecurityAuditSink
{
    Task WriteInIndependentTransactionAsync(IdentitySecurityAuditRow row, CancellationToken cancellationToken);
}

public sealed class PostgresIdentitySecurityAuditSink(IdentityAdminConnection connectionFactory)
    : IIdentitySecurityAuditSink
{
    public async Task WriteInIndependentTransactionAsync(IdentitySecurityAuditRow row, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenVerifiedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO app.audit_events
                (actor_user_id, action, outcome, entity_type, entity_id,
                 correlation_id, source_ip_hash, changes, security_context)
            VALUES
                (@actor_user_id, @action, @outcome, @entity_type, @entity_id,
                 @correlation_id, NULL, @changes, @security_context)
            """, connection, transaction);
        command.Parameters.Add("actor_user_id", NpgsqlDbType.Uuid).Value = row.ActorUserId ?? (object)DBNull.Value;
        command.Parameters.AddWithValue("action", row.Action);
        command.Parameters.AddWithValue("outcome", row.Outcome);
        command.Parameters.AddWithValue("entity_type", row.EntityType);
        command.Parameters.AddWithValue("entity_id", row.EntityId);
        command.Parameters.Add("correlation_id", NpgsqlDbType.Varchar).Value = row.CorrelationId ?? (object)DBNull.Value;
        command.Parameters.Add("changes", NpgsqlDbType.Jsonb).Value = row.ChangesJson;
        command.Parameters.Add("security_context", NpgsqlDbType.Jsonb).Value = row.SecurityContextJson;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class IdentitySecurityAudit : IIdentitySecurityAudit
{
    private readonly Func<IIdentitySecurityAuditSink> _sink;
    private readonly ILogger<IdentitySecurityAudit> _logger;

    public IdentitySecurityAudit(IIdentitySecurityAuditSink sink, ILogger<IdentitySecurityAudit> logger)
        : this(() => sink, logger) { }

    public IdentitySecurityAudit(Func<IIdentitySecurityAuditSink> sink, ILogger<IdentitySecurityAudit> logger)
    {
        _sink = sink;
        _logger = logger;
    }

    private static readonly HashSet<string> StableCodes =
    [
        "validation_failed", "authentication_required", "permission_denied", "csrf_invalid",
        "current_password_invalid", "password_reuse", "password_change_required",
        "admin_reauthentication_required", "reauthentication_failed", "credential_state_changed",
        "username_conflict", "stale_management_version", "self_management_forbidden",
        "last_active_administrator", "idempotency_key_reused", "idempotency_result_expired",
        "rate_limited", "service_unavailable"
    ];
    private static readonly HashSet<string> EntityTypes =
        ["authentication", "http_route", "user", "user_directory"];
    private static readonly HashSet<string> MetadataKeys =
        ["httpMethod", "routeTemplate", "policy", "reason", "validationFields", "expectedVersion"];
    private static readonly HashSet<string> RouteIds =
    [
        "auth-login", "auth-logout", "auth-change-password", "admin-reauthenticate",
        "admin-users-list", "admin-users-create", "admin-user-role", "admin-user-status", "admin-user-password-reset",
        "notes-list", "notes-read", "notes-create", "notes-update", "notes-delete", "notes-phase-save", "notes-phase-validate"
    ];

    public async Task RecordRejectionAsync(IdentitySecurityRejection rejection, CancellationToken cancellationToken = default)
    {
        var row = CreateRow(Normalize(rejection));
        try
        {
            await _sink().WriteInIndependentTransactionAsync(row, cancellationToken);
        }
        catch (Exception)
        {
            // Rejection audit is deliberately best-effort. Never replace the
            // protected response or log credentials/connection diagnostics.
            _logger.LogWarning("Identity security rejection audit was unavailable.");
        }
    }

    public static IdentitySecurityRejection Normalize(IdentitySecurityRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        if (!IdentitySecurityActions.All.Contains(rejection.Action)
            || !StableCodes.Contains(rejection.Code)
            || !EntityTypes.Contains(rejection.EntityType)
            || !string.Equals(rejection.Outcome, "rejected", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(rejection.EntityId)
            || rejection.Metadata is null
            || rejection.Metadata.Count > 6)
            throw new ArgumentException("Invalid identity security rejection.", nameof(rejection));

        var entityId = rejection.EntityId;
        var entityIsValid = rejection.EntityType switch
        {
            "authentication" => entityId == "unknown",
            "user_directory" => entityId == "user-directory",
            "user" => Guid.TryParseExact(entityId, "D", out var userId)
                && string.Equals(userId.ToString("D"), entityId, StringComparison.Ordinal),
            "http_route" => RouteIds.Contains(entityId),
            _ => false
        };
        if (!entityIsValid) throw new ArgumentException("Invalid identity security entity.", nameof(rejection));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rejection.Metadata)
        {
            if (!MetadataKeys.Contains(key) || value is null)
                throw new ArgumentException("Invalid identity security metadata.", nameof(rejection));
            metadata.Add(key, Sanitize(value, 128));
        }
        var correlation = string.IsNullOrWhiteSpace(rejection.CorrelationId)
            ? null
            : Sanitize(rejection.CorrelationId.Trim(), 128);
        return rejection with { CorrelationId = correlation, Metadata = metadata };
    }

    public static IdentitySecurityAuditRow CreateRow(IdentitySecurityRejection rejection)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal) { ["Code"] = rejection.Code };
        foreach (var item in rejection.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
            context[item.Key] = item.Value;
        return new(rejection.Action, rejection.Outcome, rejection.EntityType, rejection.EntityId,
            rejection.ActorUserId, rejection.CorrelationId, "{}", JsonSerializer.Serialize(context));
    }

    private static string Sanitize(string value, int maximumRunes)
    {
        var result = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control) continue;
            if (count++ == maximumRunes) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
