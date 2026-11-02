using Npgsql;
using NpgsqlTypes;
using Notes.Api.Database;

namespace Notes.Api.Catalogs;

public sealed class PostgresCatalogRepository(PostgresDataSources database, ILogger<PostgresCatalogRepository> logger) : ICatalogRepository
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CatalogOptionRecord>>> GetAsync(
        IReadOnlyCollection<string> keys,
        string? stage,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<CatalogOptionRecord>>(StringComparer.Ordinal);
        try
        {
            await using var connection = await database.Catalogs.OpenConnectionAsync(cancellationToken);
            foreach (var key in keys)
            {
                if (!CatalogRegistry.TryGetTable(key, out var table))
                    throw new CatalogRequestException($"La clave de catálogo '{key}' no es válida.");

                // `table` comes only from CatalogRegistry's closed whitelist. User input is never interpolated.
                var sql = $"""
                    SELECT id, code, label, sort_order, is_active, version, stage_codes
                    FROM app.{table}
                    WHERE (@include_inactive OR is_active)
                      AND (@stage IS NULL OR cardinality(stage_codes) = 0 OR @stage = ANY(stage_codes))
                    ORDER BY sort_order, code
                    """;
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("include_inactive", includeInactive);
                command.Parameters.Add("stage", NpgsqlDbType.Text).Value = (object?)stage ?? DBNull.Value;
                var options = new List<CatalogOptionRecord>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    options.Add(new CatalogOptionRecord(
                        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                        reader.GetBoolean(4), reader.GetInt64(5), reader.GetFieldValue<string[]>(6).ToHashSet(StringComparer.Ordinal)));
                }
                result[key] = options;
            }
            return result;
        }
        catch (CatalogRequestException) { throw; }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning(exception, "PostgreSQL catalog query failed.");
            throw new CatalogUnavailableException(exception);
        }
    }

    public async Task<CatalogOptionRecord?> FindAsync(
        string key,
        string code,
        string? stage,
        CancellationToken cancellationToken)
    {
        if (!CatalogRegistry.TryGetTable(key, out var table))
            throw new CatalogRequestException($"La clave de catálogo '{key}' no es válida.");
        try
        {
            await using var connection = await database.Catalogs.OpenConnectionAsync(cancellationToken);
            var sql = $"""
                SELECT id, code, label, sort_order, is_active, version, stage_codes
                FROM app.{table}
                WHERE code = @code
                  AND (@stage IS NULL OR cardinality(stage_codes) = 0 OR @stage = ANY(stage_codes))
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("code", code);
            command.Parameters.Add("stage", NpgsqlDbType.Text).Value = (object?)stage ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new CatalogOptionRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4),
                reader.GetInt64(5), reader.GetFieldValue<string[]>(6).ToHashSet(StringComparer.Ordinal));
        }
        catch (CatalogRequestException) { throw; }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning(exception, "PostgreSQL catalog lookup failed.");
            throw new CatalogUnavailableException(exception);
        }
    }
}
