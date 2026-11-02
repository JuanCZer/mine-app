using System.Text.Json.Serialization;

namespace Notes.Api.Import;

public enum LegacySourceKind
{
    Notes,
    DetectionCases
}

public enum LegacyIssueSeverity
{
    Information,
    Warning,
    Blocking
}

public enum LegacyImportDisposition
{
    Imported,
    Quarantined,
    AlreadyProcessed
}

public sealed record LegacyImportSource(string Name, string Path, LegacySourceKind Kind);

public sealed record LegacyOwnerMapping(IReadOnlyDictionary<string, Guid> Values)
{
    public static LegacyOwnerMapping Empty { get; } = new(new Dictionary<string, Guid>());
}

public sealed record LegacyTimeZoneMapping(IReadOnlyDictionary<string, string> Values)
{
    public static LegacyTimeZoneMapping Empty { get; } = new(new Dictionary<string, string>());
}

public sealed record LegacyImportIssue(string Code, LegacyIssueSeverity Severity, string Message);

public sealed record LegacyRecordAnalysis(
    int SourceIndex,
    string SourceId,
    string PublicId,
    string SourceHash,
    Guid InternalId,
    string? LegacyUserId,
    Guid? OwnerUserId,
    string? ResolvedTimeZoneId,
    [property: JsonIgnore] string OriginalJson,
    IReadOnlyList<LegacyImportIssue> Issues)
{
    [JsonIgnore]
    public bool RequiresQuarantine => Issues.Any(issue => issue.Severity == LegacyIssueSeverity.Blocking);
}

public sealed record LegacyImportAnalysis(
    LegacyImportSource Source,
    string SourceHash,
    long SourceBytes,
    IReadOnlyList<LegacyRecordAnalysis> Records,
    IReadOnlyList<LegacyImportIssue> SourceIssues,
    bool Approved = false,
    Guid? ApprovedByUserId = null)
{
    public LegacyImportAnalysis Approve(Guid approvedByUserId)
    {
        if (approvedByUserId == Guid.Empty)
            throw new ArgumentException("La aprobación requiere un usuario autenticado.", nameof(approvedByUserId));

        return this with { Approved = true, ApprovedByUserId = approvedByUserId };
    }

    public LegacySafeReport ToSafeReport() => new(
        Source.Name,
        Source.Kind,
        SourceHash,
        SourceBytes,
        Records.Count,
        Records.Count(record => !record.RequiresQuarantine),
        Records.Count(record => record.RequiresQuarantine),
        SourceIssues,
        Records.Select(record => new LegacySafeRecordReport(
            record.SourceIndex,
            record.SourceId,
            record.SourceHash,
            record.RequiresQuarantine,
            record.Issues)).ToArray());
}

public sealed record LegacySafeReport(
    string SourceName,
    LegacySourceKind SourceKind,
    string SourceHash,
    long SourceBytes,
    int RecordCount,
    int ReadyCount,
    int QuarantineCount,
    IReadOnlyList<LegacyImportIssue> SourceIssues,
    IReadOnlyList<LegacySafeRecordReport> Records);

public sealed record LegacySafeRecordReport(
    int SourceIndex,
    string SourceId,
    string SourceHash,
    bool RequiresQuarantine,
    IReadOnlyList<LegacyImportIssue> Issues);

public sealed record LegacyImportBatch(
    Guid Id,
    string SourceName,
    string SourceFileName,
    LegacySourceKind SourceKind,
    string SourceHash,
    long SourceBytes,
    Guid ApprovedByUserId,
    int RecordCount,
    LegacySafeReport SafeReport,
    IReadOnlyDictionary<string, Guid> OwnerMapping,
    IReadOnlyDictionary<string, string> TimeZoneMapping);

public sealed record LegacyImportAttemptResult(int Imported, int Quarantined, int AlreadyProcessed);

/// <summary>
/// Separates the outcome of this invocation from the canonical cumulative
/// state of the approved batch. Batch counters describe source occurrences,
/// including occurrences linked to an original archived by an earlier batch.
/// </summary>
public sealed record LegacyImportResult(
    LegacyImportAttemptResult Attempt,
    int BatchImported,
    int BatchQuarantined,
    int BatchLinked,
    int BatchRecordCount,
    string BatchStatus)
{
    public bool RequiresReconciliation => BatchQuarantined > 0;
}

/// <summary>
/// The PostgreSQL adapter must make each record operation transactional and enforce
/// uniqueness for (source name, source record ID, record hash). A retry must return
/// <see cref="LegacyImportDisposition.AlreadyProcessed"/> instead of inserting again.
/// </summary>
public interface ILegacyImportSink
{
    Task EnsureBatchAsync(LegacyImportBatch batch, CancellationToken cancellationToken = default);

    Task<LegacyImportDisposition> ImportRecordAsync(
        LegacyImportBatch batch,
        LegacyRecordAnalysis record,
        CancellationToken cancellationToken = default);

    Task<LegacyImportResult> CompleteBatchAsync(
        LegacyImportBatch batch,
        LegacyImportAttemptResult attempt,
        CancellationToken cancellationToken = default);
}

/// <summary>Snapshot of codes read from PostgreSQL for migration analysis.</summary>
public interface ILegacyCatalogLookup
{
    bool Contains(string catalogKey, string code);
}
