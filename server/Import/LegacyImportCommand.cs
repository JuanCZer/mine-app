using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Notes.Api.Import;

/// <summary>
/// Host-neutral command handler. A deployment tool can call it without enabling
/// import behavior in the API startup. Only the redacted analysis report is written.
/// </summary>
public static class LegacyImportCommand
{
    public static async Task<int> AnalyzeAsync(
        string[] args,
        LegacyNoteImporter noteImporter,
        LegacyDetectionImporter detectionImporter,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = Parse(args);
            var source = new LegacyImportSource(options.SourceName, options.SourcePath, options.Kind);
            var owners = new LegacyOwnerMapping(await ReadMap<Guid>(options.OwnerMapPath, cancellationToken));
            var zones = new LegacyTimeZoneMapping(await ReadMap<string>(options.TimeZoneMapPath, cancellationToken));
            var analysis = options.Kind == LegacySourceKind.Notes
                ? await noteImporter.AnalyzeAsync(source, owners, zones, cancellationToken)
                : await detectionImporter.AnalyzeAsync(source, owners, zones, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(analysis.ToSafeReport(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
            return analysis.Records.Any(record => record.RequiresQuarantine) ? 2 : 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    public static async Task<int> ImportAsync(
        string[] args,
        LegacyNoteImporter noteImporter,
        LegacyDetectionImporter detectionImporter,
        ILegacyImportSink sink,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = Parse(args);
            if (!Guid.TryParse(options.ApprovedBy, out var approvedBy) || approvedBy == Guid.Empty)
                throw new ArgumentException("--approve-by debe ser el UUID de un usuario custodio existente.");
            if (!Guid.TryParse(options.BatchId, out var batchId) || batchId == Guid.Empty)
                throw new ArgumentException("--batch-id debe ser un UUID no vacío y estable para reanudar el lote.");
            if (string.IsNullOrWhiteSpace(options.ConfirmedSourceHash))
                throw new ArgumentException("--confirm-source-sha256 es obligatorio para autorizar exactamente el archivo analizado.");

            var analysis = await Analyze(options, noteImporter, detectionImporter, cancellationToken);
            if (!HashesEqual(options.ConfirmedSourceHash, analysis.SourceHash))
                throw new InvalidOperationException("La huella confirmada no corresponde al archivo analizado; no se escribió ningún registro.");

            var approved = analysis.Approve(approvedBy);
            var result = options.Kind == LegacySourceKind.Notes
                ? await noteImporter.ImportAsync(approved, batchId, sink, cancellationToken)
                : await detectionImporter.ImportAsync(approved, batchId, sink, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                sourceName = analysis.Source.Name,
                sourceHash = analysis.SourceHash,
                batchId,
                attempt = result.Attempt,
                batch = new
                {
                    imported = result.BatchImported,
                    quarantined = result.BatchQuarantined,
                    linked = result.BatchLinked,
                    records = result.BatchRecordCount,
                    status = result.BatchStatus
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return result.RequiresReconciliation ? 2 : 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or InvalidOperationException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static CommandOptions Parse(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "source", "name", "kind", "owner-map", "timezone-map",
            "approve-by", "batch-id", "confirm-source-sha256"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Uso: --source PATH --name NOMBRE --kind notes|detection --owner-map PATH --timezone-map PATH");
            var key = args[index][2..];
            if (!allowed.Contains(key) || !values.TryAdd(key, args[index + 1]))
                throw new ArgumentException($"La opción '--{key}' no es válida o está repetida.");
        }

        if (!values.TryGetValue("source", out var sourcePath)
            || !values.TryGetValue("name", out var sourceName)
            || !values.TryGetValue("kind", out var kindText)
            || !values.TryGetValue("owner-map", out var ownerMapPath)
            || !values.TryGetValue("timezone-map", out var timeZoneMapPath))
            throw new ArgumentException("Uso: --source PATH --name NOMBRE --kind notes|detection --owner-map PATH --timezone-map PATH");

        var kind = kindText switch
        {
            "notes" => LegacySourceKind.Notes,
            "detection" => LegacySourceKind.DetectionCases,
            _ => throw new ArgumentException("--kind debe ser notes o detection.")
        };
        values.TryGetValue("approve-by", out var approvedBy);
        values.TryGetValue("batch-id", out var batchId);
        values.TryGetValue("confirm-source-sha256", out var confirmedSourceHash);
        return new CommandOptions(sourcePath, sourceName, kind, ownerMapPath, timeZoneMapPath,
            approvedBy, batchId, confirmedSourceHash);
    }

    private static async Task<LegacyImportAnalysis> Analyze(
        CommandOptions options,
        LegacyNoteImporter noteImporter,
        LegacyDetectionImporter detectionImporter,
        CancellationToken cancellationToken)
    {
        var source = new LegacyImportSource(options.SourceName, options.SourcePath, options.Kind);
        var owners = new LegacyOwnerMapping(await ReadMap<Guid>(options.OwnerMapPath, cancellationToken));
        var zones = new LegacyTimeZoneMapping(await ReadMap<string>(options.TimeZoneMapPath, cancellationToken));
        return options.Kind == LegacySourceKind.Notes
            ? await noteImporter.AnalyzeAsync(source, owners, zones, cancellationToken)
            : await detectionImporter.AnalyzeAsync(source, owners, zones, cancellationToken);
    }

    private static bool HashesEqual(string supplied, string actual)
    {
        if (supplied.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(actual));
    }

    private static async Task<IReadOnlyDictionary<string, T>> ReadMap<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, T>>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("El archivo de correspondencias no contiene un objeto JSON.");
    }

    private sealed record CommandOptions(
        string SourcePath,
        string SourceName,
        LegacySourceKind Kind,
        string OwnerMapPath,
        string TimeZoneMapPath,
        string? ApprovedBy,
        string? BatchId,
        string? ConfirmedSourceHash);
}
