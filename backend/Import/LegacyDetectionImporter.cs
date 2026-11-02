namespace Notes.Api.Import;

/// <summary>
/// Explicit adapter for exports from gds-detection-cases-v1. It analyzes and
/// preserves local-only fields; it never infers server validations from a local stage.
/// </summary>
public sealed class LegacyDetectionImporter(ILegacyCatalogLookup catalogs)
{
    private readonly LegacyNoteImporter _inner = new(catalogs);

    public Task<LegacyImportAnalysis> AnalyzeAsync(
        LegacyImportSource source,
        LegacyOwnerMapping ownerMapping,
        LegacyTimeZoneMapping timeZoneMapping,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind != LegacySourceKind.DetectionCases)
            throw new ArgumentException("La fuente debe declararse como expediente local de detección.", nameof(source));
        return _inner.AnalyzeAsync(source, ownerMapping, timeZoneMapping, cancellationToken);
    }

    public Task<LegacyImportResult> ImportAsync(
        LegacyImportAnalysis analysis,
        Guid batchId,
        ILegacyImportSink sink,
        CancellationToken cancellationToken = default) =>
        _inner.ImportAsync(analysis, batchId, sink, cancellationToken);
}
