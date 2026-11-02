using System.Text.Json;

namespace Notes.Api.Import;

internal static class LegacyImportPostgresPolicy
{
    public static string SourceLockIdentity(string source, string sourceRecordId) =>
        $"source:{source.Length}:{source}:{sourceRecordId.Length}:{sourceRecordId}";

    public static string[] TargetLockIdentities(Guid internalId, string publicId)
    {
        var canonicalId = internalId.ToString("D");
        return
        [
            $"target-id:{canonicalId.Length}:{canonicalId}",
            $"target-public:{publicId.Length}:{publicId}",
        ];
    }

    public static bool CanStoreParsedPayload(string originalJson)
    {
        using var document = JsonDocument.Parse(originalJson);
        return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    public static Guid[] DistinctOwnerIds(IReadOnlyDictionary<string, Guid> ownerMapping) =>
        ownerMapping.Values.Distinct().Order().ToArray();

    public static bool IsRecoverablePostgresSqlState(string sqlState) =>
        sqlState.StartsWith("22", StringComparison.Ordinal)
        || sqlState.StartsWith("23", StringComparison.Ordinal);

    public static LegacyBatchCompletion EvaluateBatchCompletion(
        int declaredRecords,
        int linkedRecords,
        int importedRecords,
        int quarantinedRecords,
        int pendingRecords)
    {
        var accountedRecords = importedRecords + quarantinedRecords + pendingRecords;
        var canComplete = linkedRecords == declaredRecords
            && accountedRecords == linkedRecords
            && pendingRecords == 0;
        return new LegacyBatchCompletion(
            canComplete,
            canComplete
                ? quarantinedRecords > 0 ? "completed_with_errors" : "completed"
                : "running");
    }
}

internal sealed record LegacyBatchCompletion(bool CanComplete, string Status);
