using Npgsql;

namespace Notes.Api.Import;

public sealed class PostgresLegacyCatalogLookup : ILegacyCatalogLookup
{
    private readonly HashSet<(string CatalogKey, string Code)> values;

    private PostgresLegacyCatalogLookup(HashSet<(string CatalogKey, string Code)> values) =>
        this.values = values;

    public bool Contains(string catalogKey, string code) => values.Contains((catalogKey, code));

    public static async Task<PostgresLegacyCatalogLookup> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        var values = new HashSet<(string CatalogKey, string Code)>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT catalog_key, code FROM app.catalog_options WHERE is_active ORDER BY catalog_key, code", connection)
        {
            CommandTimeout = 30
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values.Add((reader.GetString(0), reader.GetString(1)));
        return new PostgresLegacyCatalogLookup(values);
    }
}
