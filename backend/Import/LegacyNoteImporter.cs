using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notes.Api.Import;

public sealed class LegacyNoteImporter(ILegacyCatalogLookup catalogs)
{
    private const long MaximumSourceBytes = 100 * 1024 * 1024;
    private const int MaximumRecords = 100_000;
    private static readonly string[] CanonicalStages = ["indications", "reconnaissance", "surveillance", "mitigation"];

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128
    };

    private static readonly IReadOnlyDictionary<string, string> CatalogFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["soil"] = "soils",
            ["season"] = "seasons",
            ["platform"] = "platforms",
            ["collectionSource"] = "indicationSources",
            ["probability"] = "riskLevels",
            ["priority"] = "riskLevels",
            ["finding"] = "findings",
            ["environment"] = "environmentReviews",
            ["deployment"] = "surveillanceDeployments",
            ["humanActivity"] = "humanActivities",
            ["environmentDecision"] = "environmentDecisions",
            ["protectionLevel"] = "protectionLevels",
            ["neutralizationResult"] = "neutralizationResults",
            ["routeResult"] = "routeResults"
        };

    private static readonly IReadOnlyDictionary<string, string> CatalogArrays =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["signatures"] = "indicationSignatures",
            ["radioTechnologies"] = "radioTechnologies",
            ["methods"] = "surveillanceMethods"
        };

    private static readonly HashSet<string> DateFields = new(StringComparer.Ordinal)
    {
        "createdAt", "updatedAt", "validatedAt", "at", "observedAt",
        "observationStartedAt", "observationEndedAt", "recordedAt"
    };

    private static readonly IReadOnlyDictionary<string, string> DetailStage =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["indications"] = "indications",
            ["reconnaissance"] = "reconnaissance",
            ["surveillance"] = "surveillance",
            ["mitigation"] = "mitigation"
        };

    public async Task<LegacyImportAnalysis> AnalyzeAsync(
        LegacyImportSource source,
        LegacyOwnerMapping ownerMapping,
        LegacyTimeZoneMapping timeZoneMapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ownerMapping);
        ArgumentNullException.ThrowIfNull(timeZoneMapping);
        if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 80 || source.Name != source.Name.Trim())
            throw new ArgumentException("La fuente requiere un nombre estable de hasta 80 caracteres, sin espacios exteriores.", nameof(source));

        var file = new FileInfo(source.Path);
        if (!file.Exists) throw new FileNotFoundException("No se encontró el archivo de importación.", source.Path);
        if (file.Length > MaximumSourceBytes) throw new InvalidDataException("El archivo supera el límite de 100 MiB para análisis local.");

        var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
        var sourceHash = Hash(bytes);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        var recordsElement = RecordsElement(document.RootElement, source.Kind);
        if (recordsElement.GetArrayLength() > MaximumRecords)
            throw new InvalidDataException("La fuente supera el máximo de 100,000 registros por lote.");

        var records = new List<LegacyRecordAnalysis>(recordsElement.GetArrayLength());
        var index = 0;
        foreach (var element in recordsElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(AnalyzeRecord(source, index, element, ownerMapping, timeZoneMapping));
            index++;
        }

        foreach (var duplicate in records.GroupBy(record => record.SourceId, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            foreach (var repeated in duplicate)
            {
                var issues = repeated.Issues.ToList();
                AddOnce(issues, Block("source-id-duplicate", "El identificador aparece más de una vez en el mismo archivo y requiere conciliación."));
                records[repeated.SourceIndex] = repeated with { Issues = issues };
            }
        }

        return new LegacyImportAnalysis(source, sourceHash, bytes.LongLength, records, []);
    }

    public async Task<LegacyImportResult> ImportAsync(
        LegacyImportAnalysis analysis,
        Guid batchId,
        ILegacyImportSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(sink);
        if (!analysis.Approved || analysis.ApprovedByUserId is not { } approvedByUserId || approvedByUserId == Guid.Empty)
            throw new InvalidOperationException("El análisis debe aprobarse explícitamente antes de importar.");
        if (analysis.SourceIssues.Any(issue => issue.Severity == LegacyIssueSeverity.Blocking))
            throw new InvalidOperationException("La fuente tiene errores globales que impiden la importación.");

        var batch = new LegacyImportBatch(
            batchId,
            analysis.Source.Name,
            Path.GetFileName(analysis.Source.Path),
            analysis.Source.Kind,
            analysis.SourceHash,
            analysis.SourceBytes,
            approvedByUserId,
            analysis.Records.Count,
            analysis.ToSafeReport(),
            analysis.Records
                .Where(record => !string.IsNullOrWhiteSpace(record.LegacyUserId) && record.OwnerUserId is not null)
                .GroupBy(record => record.LegacyUserId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().OwnerUserId!.Value, StringComparer.Ordinal),
            analysis.Records
                .Where(record => !string.IsNullOrWhiteSpace(record.ResolvedTimeZoneId))
                .GroupBy(record => record.SourceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().ResolvedTimeZoneId!, StringComparer.Ordinal));
        await sink.EnsureBatchAsync(batch, cancellationToken);

        var imported = 0;
        var quarantined = 0;
        var alreadyProcessed = 0;
        foreach (var record in analysis.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await sink.ImportRecordAsync(batch, record, cancellationToken))
            {
                case LegacyImportDisposition.Imported:
                    imported++;
                    break;
                case LegacyImportDisposition.Quarantined:
                    quarantined++;
                    break;
                case LegacyImportDisposition.AlreadyProcessed:
                    alreadyProcessed++;
                    break;
                default:
                    throw new InvalidOperationException("El adaptador devolvió un resultado de importación desconocido.");
            }
        }

        var attempt = new LegacyImportAttemptResult(imported, quarantined, alreadyProcessed);
        return await sink.CompleteBatchAsync(batch, attempt, cancellationToken);
    }

    private LegacyRecordAnalysis AnalyzeRecord(
        LegacyImportSource source,
        int index,
        JsonElement element,
        LegacyOwnerMapping ownerMapping,
        LegacyTimeZoneMapping timeZoneMapping)
    {
        var issues = new List<LegacyImportIssue>();
        var original = element.GetRawText();
        var recordHash = Hash(Encoding.UTF8.GetBytes(original));
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Block("record-not-object", "El registro no es un objeto JSON y requiere revisión."));
            return new LegacyRecordAnalysis(index, $"#record-{index + 1}", $"#record-{index + 1}", recordHash,
                DeterministicGuid(source.Name, $"#record-{index + 1}"), null, null, null, original, issues);
        }

        var publicId = ReadString(element, "id");
        var sourceId = publicId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            sourceId = $"#record-{index + 1}";
            publicId = sourceId;
            issues.Add(Block("source-id-missing", "El registro no tiene identificador de origen."));
        }
        var resolvedPublicId = publicId!;

        var internalId = Guid.TryParse(resolvedPublicId, out var parsedId)
            ? parsedId
            : DeterministicGuid(source.Name, sourceId);
        if (!Guid.TryParse(resolvedPublicId, out _))
            issues.Add(Info("public-id-not-uuid", "El ID público se conservará y el UUID interno será determinista."));

        var legacyUserId = source.Kind == LegacySourceKind.Notes
            ? ReadString(element, "userId")
            : ReadString(element, "owner");
        Guid? ownerUserId = null;
        if (!string.IsNullOrWhiteSpace(legacyUserId) && ownerMapping.Values.TryGetValue(legacyUserId, out var mappedOwner))
            ownerUserId = mappedOwner;
        else
            issues.Add(Block("owner-unmapped", "El propietario heredado requiere una correspondencia explícita con un usuario custodio."));

        if (source.Kind == LegacySourceKind.Notes)
            AnalyzeNote(element, sourceId, timeZoneMapping, issues);
        else
            AnalyzeDetectionCase(element, sourceId, timeZoneMapping, issues);

        var resolvedTimeZoneId = ResolveTimeZone(sourceId, timeZoneMapping);
        return new LegacyRecordAnalysis(index, sourceId, resolvedPublicId, recordHash, internalId,
            legacyUserId, ownerUserId, resolvedTimeZoneId, original, issues);
    }

    private void AnalyzeNote(
        JsonElement element,
        string sourceId,
        LegacyTimeZoneMapping timeZoneMapping,
        List<LegacyImportIssue> issues)
    {
        if (!element.TryGetProperty("workflow", out var workflow) || workflow.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            issues.Add(Info("workflow-missing", "La nota se importará con cuatro fases pendientes."));
        else
            AnalyzePhaseSet(workflow, issues);

        AnalyzeCoordinates(element, issues);
        AnalyzeDates(element, sourceId, timeZoneMapping, issues);
        AnalyzeCatalogs(element, issues);
        AnalyzeCrossPhaseData(workflow, issues);
        AnalyzeLegacyValidations(workflow, issues);
    }

    private void AnalyzeDetectionCase(
        JsonElement element,
        string sourceId,
        LegacyTimeZoneMapping timeZoneMapping,
        List<LegacyImportIssue> issues)
    {
        AnalyzeCoordinates(element, issues, includeDetectionLatitude: true);
        AnalyzeDates(element, sourceId, timeZoneMapping, issues);
        AnalyzeCatalogs(element, issues);
        if (element.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
            CheckCatalogCode("mexicoStates", state.GetString(), issues);
        if (element.TryGetProperty("stage", out var stage) && stage.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(stage.GetString()))
            issues.Add(Info("local-stage-not-validation", "La etapa local se archivará como dato de origen y no como validación del servidor."));
    }

    private static void AnalyzeCoordinates(
        JsonElement element,
        List<LegacyImportIssue> issues,
        bool includeDetectionLatitude = false)
    {
        ValidateCoordinate(element, "altitude", -90, 90, "latitude-invalid", "La latitud heredada está fuera del rango permitido.", issues);
        if (includeDetectionLatitude)
            ValidateCoordinate(element, "latitude", -90, 90, "latitude-invalid", "La latitud heredada está fuera del rango permitido.", issues);
        ValidateCoordinate(element, "longitude", -180, 180, "longitude-invalid", "La longitud heredada está fuera del rango permitido.", issues);
    }

    private static void ValidateCoordinate(
        JsonElement element,
        string propertyName,
        double minimum,
        double maximum,
        string issueCode,
        string message,
        List<LegacyImportIssue> issues)
    {
        if (!element.TryGetProperty(propertyName, out var coordinate)) return;
        if (coordinate.ValueKind != JsonValueKind.Number
            || !coordinate.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum)
            AddOnce(issues, Block(issueCode, message));
    }

    private static JsonElement RecordsElement(JsonElement root, LegacySourceKind kind)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (kind == LegacySourceKind.DetectionCases
            && root.ValueKind == JsonValueKind.Object
            && IsDetectionVersionOne(root)
            && root.TryGetProperty("cases", out var cases)
            && cases.ValueKind == JsonValueKind.Array)
            return cases;
        throw new InvalidDataException(
            kind == LegacySourceKind.DetectionCases
                ? "La fuente de detección debe ser el arreglo heredado o un contenedor version 1 con cases."
                : "La fuente heredada debe ser un arreglo JSON.");
    }

    private static bool IsDetectionVersionOne(JsonElement root)
    {
        foreach (var propertyName in new[] { "schemaVersion", "version" })
        {
            if (root.TryGetProperty(propertyName, out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number)
                && number == 1)
                return true;
        }
        return false;
    }

    private void AnalyzeCatalogs(JsonElement element, List<LegacyImportIssue> issues)
    {
        Walk(element, (name, value) =>
        {
            if (CatalogFields.TryGetValue(name, out var catalogKey) && value.ValueKind == JsonValueKind.String)
                CheckCatalogCode(catalogKey, value.GetString(), issues);
            else if (name == "sensor" && value.ValueKind == JsonValueKind.String)
                CheckCatalogCode("sensors", value.GetString(), issues);
            else if (CatalogArrays.TryGetValue(name, out catalogKey) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String) CheckCatalogCode(catalogKey, entry.GetString(), issues);
            }
        });
    }

    private void CheckCatalogCode(string catalogKey, string? code, List<LegacyImportIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(code) && !catalogs.Contains(catalogKey, code))
            AddOnce(issues, Block("catalog-value-unknown", "Existe un código de catálogo que no está disponible en la base de destino."));
    }

    private static void AnalyzeDates(
        JsonElement element,
        string sourceId,
        LegacyTimeZoneMapping timeZoneMapping,
        List<LegacyImportIssue> issues)
    {
        Walk(element, (name, value) =>
        {
            if (!DateFields.Contains(name) || value.ValueKind != JsonValueKind.String) return;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!DateTimeOffset.TryParse(text, out _))
            {
                AddOnce(issues, Block("date-invalid", "Existe una fecha heredada que no tiene un formato interpretable."));
                return;
            }

            if (HasExplicitOffset(text)) return;
            if (!TryGetTimeZone(sourceId, timeZoneMapping, out var zone) || !IsKnownTimeZone(zone))
            {
                AddOnce(issues, Block("date-timezone-required", "Existe una fecha sin zona; se requiere una correspondencia horaria explícita."));
                return;
            }
            if (DateTime.TryParse(text, out var local))
            {
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone!);
                if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local))
                    AddOnce(issues, Block("date-timezone-ambiguous", "Existe una hora local ambigua o inexistente para la zona aprobada."));
            }
        });
    }

    private static void AnalyzeCrossPhaseData(JsonElement workflow, List<LegacyImportIssue> issues)
    {
        if (workflow.ValueKind != JsonValueKind.Object
            || !workflow.TryGetProperty("phases", out var phases)
            || phases.ValueKind != JsonValueKind.Array) return;

        foreach (var phase in phases.EnumerateArray())
        {
            if (phase.ValueKind != JsonValueKind.Object) continue;
            var stage = ReadString(phase, "stage");
            if (!phase.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            foreach (var detail in DetailStage)
            {
                if (detail.Value == stage || !data.TryGetProperty(detail.Key, out var value) || !HasMeaningfulValue(value)) continue;
                AddOnce(issues, Block("cross-phase-data", "Hay datos de captura guardados bajo una fase distinta y requieren reconciliación."));
            }
        }
    }

    private void AnalyzePhaseSet(JsonElement workflow, List<LegacyImportIssue> issues)
    {
        if (workflow.ValueKind != JsonValueKind.Object
            || !workflow.TryGetProperty("phases", out var phases)
            || phases.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Block("workflow-phase-set", "El workflow no contiene un arreglo de fases válido."));
            return;
        }

        var stages = phases.EnumerateArray()
            .Where(phase => phase.ValueKind == JsonValueKind.Object)
            .Select(phase => ReadString(phase, "stage"))
            .Where(stage => !string.IsNullOrWhiteSpace(stage))
            .ToArray();
        var expected = DetailStage.Keys.Order(StringComparer.Ordinal).ToArray();
        if (stages.Length != 4 || stages.Distinct(StringComparer.Ordinal).Count() != 4
            || !stages.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            issues.Add(Block("workflow-phase-set", "El workflow no contiene exactamente las cuatro fases canónicas."));

        foreach (var stage in stages)
            CheckCatalogCode("workflowStages", stage, issues);
    }

    private static void AnalyzeLegacyValidations(JsonElement workflow, List<LegacyImportIssue> issues)
    {
        if (workflow.ValueKind != JsonValueKind.Object
            || !workflow.TryGetProperty("phases", out var phases)
            || phases.ValueKind != JsonValueKind.Array) return;

        var byStage = phases.EnumerateArray()
            .Where(phase => phase.ValueKind == JsonValueKind.Object)
            .Select(phase => (Stage: ReadString(phase, "stage"), Phase: phase))
            .Where(item => !string.IsNullOrWhiteSpace(item.Stage))
            .GroupBy(item => item.Stage!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Phase, StringComparer.Ordinal);
        var gap = false;
        var falsePositiveClosed = false;
        foreach (var stage in CanonicalStages)
        {
            if (!byStage.TryGetValue(stage, out var phase)) { gap = true; continue; }
            var validated = phase.TryGetProperty("validatedAt", out var validatedAt)
                && validatedAt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(validatedAt.GetString());
            if (!validated) { gap = true; continue; }
            AddOnce(issues, Info("legacy-validation", "La validación se conservará con origen legado y sin atribuir una identidad autenticada."));
            if (gap || falsePositiveClosed)
                AddOnce(issues, Block("legacy-validation-sequence", "Las validaciones heredadas no siguen la secuencia canónica o continúan después de un falso positivo."));
            if (string.IsNullOrWhiteSpace(ReadString(phase, "validatedBy")))
                AddOnce(issues, Block("legacy-validator-missing", "Una validación heredada no identifica al validador declarado."));
            if (stage == "reconnaissance"
                && phase.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("review", out var review)
                && review.ValueKind == JsonValueKind.Object
                && ReadString(review, "finding") == "false-positive")
                falsePositiveClosed = true;
        }
    }

    private static bool HasMeaningfulValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => HasMeaningfulValue(property.Value)),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Number => true,
        JsonValueKind.True => true,
        _ => false
    };

    private static void Walk(JsonElement element, Action<string, JsonElement> visit)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                visit(property.Name, property.Value);
                Walk(property.Value, visit);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Walk(item, visit);
        }
    }

    private static bool HasExplicitOffset(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) return true;
        var timeSeparator = trimmed.IndexOf('T');
        if (timeSeparator < 0) return false;
        return trimmed.IndexOf('+', timeSeparator) >= 0 || trimmed.IndexOf('-', timeSeparator) >= 0;
    }

    private static bool TryGetTimeZone(string sourceId, LegacyTimeZoneMapping mapping, out string? value) =>
        mapping.Values.TryGetValue(sourceId, out value) || mapping.Values.TryGetValue("*", out value);

    private static string? ResolveTimeZone(string sourceId, LegacyTimeZoneMapping mapping) =>
        TryGetTimeZone(sourceId, mapping, out var value) && IsKnownTimeZone(value) ? value : null;

    private static bool IsKnownTimeZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(value);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Guid DeterministicGuid(string sourceName, string sourceId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceName}\0{sourceId}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static LegacyImportIssue Info(string code, string message) => new(code, LegacyIssueSeverity.Information, message);
    private static LegacyImportIssue Block(string code, string message) => new(code, LegacyIssueSeverity.Blocking, message);
    private static void AddOnce(List<LegacyImportIssue> issues, LegacyImportIssue issue)
    {
        if (!issues.Any(existing => existing.Code == issue.Code)) issues.Add(issue);
    }
}
