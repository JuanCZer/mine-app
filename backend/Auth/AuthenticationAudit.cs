using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Notes.Api.Database;

namespace Notes.Api.Auth;

public sealed record AuthenticationAuditRecord(
    string Action,
    string Outcome,
    Guid? ActorUserId,
    string EntityId,
    string? Reason,
    string? CorrelationId);

public interface IAuthenticationAuditStore
{
    Task WriteAsync(AuthenticationAuditRecord record, CancellationToken cancellationToken = default);
}

public sealed class AuthenticationAuditUnavailableException(Exception? inner = null)
    : Exception("Authentication audit storage is unavailable.", inner);

public sealed class PostgresAuthenticationAuditStore(
    PostgresDataSources database,
    ILogger<PostgresAuthenticationAuditStore> logger) : IAuthenticationAuditStore
{
    public async Task WriteAsync(AuthenticationAuditRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                INSERT INTO app.audit_events
                    (id, occurred_at, actor_user_id, action, outcome, entity_type, entity_id,
                     correlation_id, source_ip_hash, changes, security_context)
                VALUES
                    (@id, now(), @actor_id, @action, @outcome, 'authentication', @entity_id,
                     @correlation_id, NULL, '{}'::jsonb, @security_context)
                """, connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = record.ActorUserId ?? (object)DBNull.Value;
            command.Parameters.AddWithValue("action", record.Action);
            command.Parameters.AddWithValue("outcome", record.Outcome);
            command.Parameters.AddWithValue("entity_id", record.EntityId);
            command.Parameters.Add("correlation_id", NpgsqlDbType.Varchar).Value =
                string.IsNullOrEmpty(record.CorrelationId) ? DBNull.Value : record.CorrelationId;
            command.Parameters.Add("security_context", NpgsqlDbType.Jsonb).Value = record.Reason is null
                ? "{}"
                : JsonSerializer.Serialize(new { reason = record.Reason });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Authentication audit write failed.");
            throw new AuthenticationAuditUnavailableException(exception);
        }
    }
}
