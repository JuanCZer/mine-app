using Npgsql;
using Notes.Api.Import;

if (args.Length == 0 || args[0] is not ("analyze" or "import"))
{
    Console.Error.WriteLine("Uso: analyze|import --source PATH --name NOMBRE --kind notes|detection --owner-map PATH --timezone-map PATH [...]");
    return 1;
}

var connectionValue = Environment.GetEnvironmentVariable("POSTGRES_DEPLOY_URL");
if (string.IsNullOrWhiteSpace(connectionValue))
{
    Console.Error.WriteLine("POSTGRES_DEPLOY_URL es obligatoria para el comando aislado de importación.");
    return 1;
}

try
{
    var connection = DeploymentConnection.Parse(connectionValue);
    await using var dataSource = NpgsqlDataSource.Create(connection);
    var catalogs = await PostgresLegacyCatalogLookup.LoadAsync(dataSource);
    var notes = new LegacyNoteImporter(catalogs);
    var detection = new LegacyDetectionImporter(catalogs);
    var commandArgs = args.Skip(1).ToArray();
    return args[0] == "analyze"
        ? await LegacyImportCommand.AnalyzeAsync(commandArgs, notes, detection, Console.Out, Console.Error)
        : await LegacyImportCommand.ImportAsync(
            commandArgs, notes, detection, new PostgresLegacyImportSink(dataSource), Console.Out, Console.Error);
}
catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine("No se pudo ejecutar la importación. Revise TLS, esquema, rol de despliegue, aprobación y archivos de entrada.");
    return 1;
}

static class DeploymentConnection
{
    public static NpgsqlConnectionStringBuilder Parse(string value)
    {
        NpgsqlConnectionStringBuilder builder;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "postgres" or "postgresql")
        {
            var userInfo = uri.UserInfo.Split(':', 2);
            builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = userInfo.Length == 2 ? Uri.UnescapeDataString(userInfo[1]) : ""
            };
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var item = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
                if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase)) builder.SslMode = ParseSslMode(item);
                else if (key.Equals("sslrootcert", StringComparison.OrdinalIgnoreCase)) builder.RootCertificate = item;
            }
        }
        else
        {
            builder = new NpgsqlConnectionStringBuilder(value);
        }

        if (builder.SslMode != SslMode.VerifyFull
            || builder.TryGetValue("Trust Server Certificate", out var trust) && trust is true)
            throw new InvalidOperationException("POSTGRES_DEPLOY_URL debe usar sslmode=verify-full y validar el certificado del servidor.");
        return builder;
    }

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "verify-full" or "verifyfull" => SslMode.VerifyFull,
        "verify-ca" or "verifyca" => SslMode.VerifyCA,
        "require" => SslMode.Require,
        "prefer" => SslMode.Prefer,
        "disable" => SslMode.Disable,
        _ => throw new InvalidOperationException("sslmode no es válido.")
    };
}
