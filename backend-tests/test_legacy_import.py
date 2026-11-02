"""Behavior tests for the legacy JSON analysis/import contract.

The test compiles a small consumer against Notes.Api.  This exercises the public
import API without wiring migration behavior into the web application's startup.
"""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[1]


NOTES_FIXTURE = [
    {
        "id": "11111111-1111-1111-1111-111111111111",
        "userId": "legacy-operator",
        "title": "Nota sin workflow",
        "content": "SENSITIVE-CONTENT-MUST-NOT-BE-IN-REPORT",
        "state": "Sector uno",
        "altitude": 19.4,
        "longitude": -99.1,
        "createdAt": "2026-09-27T10:00:00Z",
        "updatedAt": "2026-09-27T10:05:00Z",
        "revision": 2,
    },
    {
        "id": "22222222-2222-2222-2222-222222222222",
        "userId": "legacy-operator",
        "title": "Workflow completo",
        "content": "Con evidencia y validación heredada",
        "state": "Sector dos",
        "altitude": 20.1,
        "longitude": -100.2,
        "createdAt": "2026-09-27T10:00:00Z",
        "updatedAt": "2026-09-27T11:00:00Z",
        "revision": 8,
        "workflow": {
            "status": "open",
            "phases": [
                {
                    "stage": "indications",
                    "validatedAt": "2026-09-27T10:30:00Z",
                    "validatedBy": "Nombre declarado",
                    "data": {
                        "soil": "andosol",
                        "season": "dry",
                        "terrainNotes": "Terreno",
                        "review": {"finding": "confirmed", "environment": "pending"},
                        "evidence": [
                            {"id": "ev-1", "sensor": "LiDAR", "reference": "L-1", "summary": "Resumen"}
                        ],
                        "indications": {"platform": "if1200a", "signatures": ["surface-removal"]},
                        "reconnaissance": {},
                        "surveillance": {},
                        "mitigation": {},
                    },
                },
                {
                    "stage": "reconnaissance",
                    "data": {
                        "soil": "andosol", "season": "dry",
                        "review": {"finding": "pending", "environment": "pending"},
                        "evidence": [], "indications": {}, "reconnaissance": {},
                        "surveillance": {}, "mitigation": {},
                    },
                },
                {
                    "stage": "surveillance",
                    "data": {
                        "soil": "andosol", "season": "dry",
                        "review": {"finding": "pending", "environment": "pending"},
                        "evidence": [], "indications": {}, "reconnaissance": {},
                        "surveillance": {}, "mitigation": {},
                    },
                },
                {
                    "stage": "mitigation",
                    "data": {
                        "soil": "andosol", "season": "dry",
                        "review": {"finding": "pending", "environment": "pending"},
                        "evidence": [], "indications": {}, "reconnaissance": {},
                        "surveillance": {}, "mitigation": {},
                    },
                },
            ],
            "history": [{"id": "h-1", "at": "2026-09-27T10:30:00Z", "actor": "Nombre declarado", "text": "Validada"}],
        },
    },
    {
        "id": "INC-LEGACY-NON-UUID",
        "userId": "legacy-operator",
        "title": "ID público no UUID",
        "content": "Debe conservarse",
        "createdAt": "2026-09-27T10:00:00Z",
        "updatedAt": "2026-09-27T10:00:00Z",
    },
    {
        "id": "33333333-3333-3333-3333-333333333333",
        "userId": "legacy-operator",
        "title": "Objeto fuera de fase",
        "content": "Debe preservarse y revisarse",
        "createdAt": "2026-09-27T10:00:00Z",
        "updatedAt": "2026-09-27T10:00:00Z",
        "workflow": {
            "status": "open",
            "phases": [{
                "stage": "indications",
                "data": {
                    "soil": "andosol",
                    "season": "dry",
                    "review": {"finding": "pending", "environment": "pending"},
                    "evidence": [],
                    "indications": {},
                    "reconnaissance": {"magneticReadingNt": 42},
                    "surveillance": {},
                    "mitigation": {},
                },
            }],
            "history": [],
        },
    },
    {
        "id": "44444444-4444-4444-4444-444444444444",
        "userId": "demo-user",
        "title": "Catálogo desconocido y fecha ambigua",
        "content": "Debe ir a cuarentena",
        "createdAt": "2026-09-27T10:00:00",
        "updatedAt": "2026-09-27T10:05:00",
        "workflow": {
            "status": "open",
            "phases": [{
                "stage": "indications",
                "data": {
                    "soil": "soil-not-in-database",
                    "season": "dry",
                    "review": {"finding": "pending", "environment": "pending"},
                    "evidence": [],
                    "indications": {},
                    "reconnaissance": {},
                    "surveillance": {},
                    "mitigation": {},
                },
            }],
            "history": [],
        },
    },
]


DETECTION_FIXTURE = {
    "schemaVersion": 1,
    "exportedAt": "2026-09-27T09:30:00Z",
    "source": "Registro local, sin verificación de sensores",
    "cases": [
        {
            "id": "local-case-01",
            "code": "DET-001",
            "municipality": "Municipio sintético",
            "state": "mx-cmx",
            "latitude": 19.4,
            "longitude": -99.1,
            "priority": "high",
            "owner": "Operador local declarado",
            "stage": "surveillance",
            "evidence": [{"id": "local-ev", "sensor": "RGB", "recordedAt": "2026-09-27T09:00"}],
        },
        {
            "id": "local-case-invalid-latitude",
            "state": "mx-cmx",
            "latitude": 91,
            "longitude": -99.1,
            "priority": "medium",
            "owner": "Operador local declarado",
        },
        {
            "id": "local-case-malformed-coordinates",
            "state": "mx-cmx",
            "latitude": "19.4",
            "longitude": None,
            "priority": "medium",
            "owner": "Operador local declarado",
        },
        {
            "id": "local-case-structured-coordinates",
            "state": "mx-cmx",
            "latitude": {"value": 19.4},
            "longitude": [-99.1],
            "priority": "medium",
            "owner": "Operador local declarado",
        },
        {
            "id": "local-case-null-and-string-coordinates",
            "state": "mx-cmx",
            "latitude": None,
            "longitude": "-99.1",
            "priority": "medium",
            "owner": "Operador local declarado",
        },
        {
            "id": "local-case-array-and-object-coordinates",
            "state": "mx-cmx",
            "latitude": [19.4],
            "longitude": {"value": -99.1},
            "priority": "medium",
            "owner": "Operador local declarado",
        },
    ],
}


HARNESS = r'''
using System.Text.Json;
using Notes.Api.Import;

if (args.Length != 3) throw new ArgumentException("notes, exported detection and stored detection fixture paths are required");

var owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
var owners = new LegacyOwnerMapping(new Dictionary<string, Guid> { ["legacy-operator"] = owner });
var zones = new LegacyTimeZoneMapping(new Dictionary<string, string>());
var catalogs = new TestCatalogs();
var importer = new LegacyNoteImporter(catalogs);
var source = new LegacyImportSource("notes-fixture", args[0], LegacySourceKind.Notes);
var analysis = await importer.AnalyzeAsync(source, owners, zones);
var again = await importer.AnalyzeAsync(source, owners, zones);

Assert(analysis.Records.Count == 5, "all note fixtures must be analyzed");
Assert(analysis.SourceHash.Length == 64, "source checksum must be SHA-256 hex");
Assert(!analysis.Approved, "analysis is a dry-run until explicitly approved");
Assert(analysis.Records[2].PublicId == "INC-LEGACY-NON-UUID", "public non-UUID ID must be preserved");
Assert(analysis.Records[2].InternalId == again.Records[2].InternalId, "generated internal UUID must be deterministic");
Assert(analysis.Records[0].Issues.Any(x => x.Code == "workflow-missing"), "note without workflow must be identified");
Assert(!analysis.Records[0].Issues.Any(x => x.Code == "catalog-value-unknown"), "note area/state text must not be treated as a Mexico-state catalog code");
Assert(!analysis.Records[1].Issues.Any(x => x.Code == "workflow-phase-set"), "a complete four-phase workflow must not be quarantined as structurally incomplete");
Assert(analysis.Records[3].Issues.Any(x => x.Code == "cross-phase-data"), "data stored under the wrong phase must be identified");
Assert(analysis.Records[3].Issues.Any(x => x.Code == "workflow-phase-set"), "a partial workflow must require reconciliation");
Assert(analysis.Records[4].Issues.Any(x => x.Code == "catalog-value-unknown"), "unknown catalog value must be quarantined");
Assert(analysis.Records[4].Issues.Any(x => x.Code == "owner-unmapped"), "demo owner must not become an authenticated user");
Assert(analysis.Records[4].Issues.Any(x => x.Code == "date-timezone-required"), "ambiguous legacy date must require an explicit zone");
Assert(analysis.Records[4].RequiresQuarantine, "blocking discrepancies must quarantine a record");
Assert(analysis.Records[0].OriginalJson.Contains("SENSITIVE-CONTENT"), "the exact original must remain available to the protected sink");

var duplicatePath = Path.Combine(Path.GetDirectoryName(args[0])!, "duplicate-source.json");
await File.WriteAllTextAsync(duplicatePath, $"[{analysis.Records[0].OriginalJson},{analysis.Records[0].OriginalJson}]");
var duplicateAnalysis = await importer.AnalyzeAsync(
    new LegacyImportSource("duplicate-fixture", duplicatePath, LegacySourceKind.Notes), owners, zones);
Assert(duplicateAnalysis.Records.All(item => item.Issues.Any(issue => issue.Code == "source-id-duplicate")),
    "duplicate source IDs in the same file must be quarantined");

var safeJson = JsonSerializer.Serialize(analysis.ToSafeReport());
Assert(!safeJson.Contains("SENSITIVE-CONTENT"), "safe report must not disclose record contents");
Assert(!safeJson.Contains("Municipio sintético"), "safe report must contain metadata only");

var mappedZones = new LegacyTimeZoneMapping(new Dictionary<string, string> { ["*"] = "America/Mexico_City" });
var allOwners = new LegacyOwnerMapping(new Dictionary<string, Guid>
{
    ["legacy-operator"] = owner,
    ["demo-user"] = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
});
var mappedAnalysis = await importer.AnalyzeAsync(source, allOwners, mappedZones);
Assert(!mappedAnalysis.Records[4].Issues.Any(x => x.Code == "date-timezone-required"), "an explicit IANA zone mapping must resolve naive dates");

var sink = new MemorySink();
await AssertThrows<InvalidOperationException>(() => importer.ImportAsync(analysis, Guid.NewGuid(), sink), "unapproved analysis must not write");
var approver = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
var approved = analysis.Approve(approver);
var batch = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
var first = await importer.ImportAsync(approved, batch, sink);
var second = await importer.ImportAsync(approved, batch, sink);
Assert(first.BatchImported > 0, "ready records must be imported");
Assert(first.BatchQuarantined > 0, "blocking records must be quarantined with their original");
Assert(second.BatchImported == first.BatchImported && second.BatchQuarantined == first.BatchQuarantined,
    "rerunning an approved batch must return its cumulative outcome");
Assert(second.Attempt.Imported == 0 && second.Attempt.AlreadyProcessed == analysis.Records.Count,
    "rerunning an approved batch must be idempotent per attempt");
Assert(second.RequiresReconciliation, "a retry must preserve the reconciliation outcome of earlier quarantines");

var reusable = mappedAnalysis.Records.First(item => !item.RequiresQuarantine) with { SourceIndex = 0 };
var cleanReuseAnalysis = mappedAnalysis with { Records = new[] { reusable }, SourceHash = new string('a', 64) };
var reuseSink = new MemorySink();
var cleanReuse = await importer.ImportAsync(cleanReuseAnalysis.Approve(approver), Guid.NewGuid(), reuseSink);
Assert(cleanReuse.BatchImported == 1 && cleanReuse.BatchQuarantined == 0,
    "the first batch must import the reusable original");
var duplicateIssue = new LegacyImportIssue(
    "source-id-duplicate", LegacyIssueSeverity.Blocking, "duplicate in current source");
var duplicatedReuse = cleanReuseAnalysis with
{
    SourceHash = new string('b', 64),
    Records = new[]
    {
        reusable with { SourceIndex = 0, Issues = new[] { duplicateIssue } },
        reusable with { SourceIndex = 1, Issues = new[] { duplicateIssue } },
    }
};
var duplicateReuse = await importer.ImportAsync(duplicatedReuse.Approve(approver), Guid.NewGuid(), reuseSink);
Assert(duplicateReuse.BatchQuarantined == 2 && duplicateReuse.RequiresReconciliation,
    "a later batch must quarantine both duplicate occurrences even when their original was imported earlier");
Assert(reuseSink.OriginalCount == 1,
    "batch-specific quarantine must not duplicate an identical protected original");

var fixtureDirectory = Path.GetDirectoryName(args[0])!;
var ownerMapPath = Path.Combine(fixtureDirectory, "owners.json");
var zoneMapPath = Path.Combine(fixtureDirectory, "zones.json");
await File.WriteAllTextAsync(ownerMapPath, JsonSerializer.Serialize(new Dictionary<string, Guid> { ["legacy-operator"] = owner }));
await File.WriteAllTextAsync(zoneMapPath, "{}");
var commandOutput = new StringWriter();
var commandError = new StringWriter();
var commandExit = await LegacyImportCommand.AnalyzeAsync(
    ["--source", args[0], "--name", "notes-fixture", "--kind", "notes", "--owner-map", ownerMapPath, "--timezone-map", zoneMapPath],
    importer,
    new LegacyDetectionImporter(catalogs),
    commandOutput,
    commandError);
Assert(commandExit == 2, "analysis command must signal records awaiting reconciliation");
Assert(commandError.ToString().Length == 0, "valid analysis command must not write an error");
Assert(!commandOutput.ToString().Contains("SENSITIVE-CONTENT"), "command output must be redacted");
var invalidOptionExit = await LegacyImportCommand.AnalyzeAsync(
    ["--unknown", "value"], importer, new LegacyDetectionImporter(catalogs), TextWriter.Null, TextWriter.Null);
Assert(invalidOptionExit == 1, "unknown command options must be rejected before reading files");
var duplicateOptionExit = await LegacyImportCommand.AnalyzeAsync(
    ["--source", args[0], "--source", args[0], "--name", "notes-fixture", "--kind", "notes",
     "--owner-map", ownerMapPath, "--timezone-map", zoneMapPath],
    importer, new LegacyDetectionImporter(catalogs), TextWriter.Null, TextWriter.Null);
Assert(duplicateOptionExit == 1, "duplicate command options must be rejected");

var importSink = new MemorySink();
var importOutput = new StringWriter();
var importError = new StringWriter();
var importBatch = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
var importExit = await LegacyImportCommand.ImportAsync(
    ["--source", args[0], "--name", "notes-fixture", "--kind", "notes", "--owner-map", ownerMapPath,
     "--timezone-map", zoneMapPath, "--approve-by", approver.ToString(), "--batch-id", importBatch.ToString(),
     "--confirm-source-sha256", analysis.SourceHash],
    importer,
    new LegacyDetectionImporter(catalogs),
    importSink,
    importOutput,
    importError);
Assert(importExit == 2, "an approved import with quarantined records must return the reconciliation status");
Assert(importSink.Completed, "the approved command must complete its import batch");
Assert(importError.ToString().Length == 0, "a valid approved import must not write an error");
Assert(!importOutput.ToString().Contains("SENSITIVE-CONTENT"), "import output must remain redacted");
Assert(importOutput.ToString().Contains("alreadyProcessed", StringComparison.OrdinalIgnoreCase), "import output must expose safe counters");
var rerunOutput = new StringWriter();
var rerunExit = await LegacyImportCommand.ImportAsync(
    ["--source", args[0], "--name", "notes-fixture", "--kind", "notes", "--owner-map", ownerMapPath,
     "--timezone-map", zoneMapPath, "--approve-by", approver.ToString(), "--batch-id", importBatch.ToString(),
     "--confirm-source-sha256", analysis.SourceHash],
    importer,
    new LegacyDetectionImporter(catalogs),
    importSink,
    rerunOutput,
    importError);
Assert(rerunExit == 2, "a rerun must keep signaling quarantines already recorded by the batch");

var rejectedSink = new MemorySink();
var rejectedExit = await LegacyImportCommand.ImportAsync(
    ["--source", args[0], "--name", "notes-fixture", "--kind", "notes", "--owner-map", ownerMapPath,
     "--timezone-map", zoneMapPath, "--approve-by", approver.ToString(), "--batch-id", Guid.NewGuid().ToString(),
     "--confirm-source-sha256", new string('0', 64)],
    importer,
    new LegacyDetectionImporter(catalogs),
    rejectedSink,
    TextWriter.Null,
    TextWriter.Null);
Assert(rejectedExit == 1 && !rejectedSink.Completed, "a mismatched source checksum must prevent every write");

var detectionImporter = new LegacyDetectionImporter(catalogs);
var detection = await detectionImporter.AnalyzeAsync(
    new LegacyImportSource("detection-fixture", args[1], LegacySourceKind.DetectionCases),
    owners,
    zones);
Assert(detection.Records.Count == 6, "the canonical browser export envelope must remain recoverable");
Assert(detection.Records[0].Issues.Any(x => x.Code == "local-stage-not-validation"), "local stage must not be promoted to server validation");
Assert(detection.Records[0].OriginalJson.Contains("recordedAt"), "local-only evidence fields must be preserved");
Assert(detection.Records[1].Issues.Any(x => x.Code == "latitude-invalid"), "detection latitude must be validated by its actual field name");
Assert(detection.Records[1].RequiresQuarantine, "invalid detection coordinates must require reconciliation");
Assert(detection.Records[2].Issues.Any(x => x.Code == "latitude-invalid"), "a present non-numeric latitude must be rejected");
Assert(detection.Records[2].Issues.Any(x => x.Code == "longitude-invalid"), "a present null longitude must be rejected");
Assert(detection.Records[2].RequiresQuarantine, "malformed coordinate types must require reconciliation");
Assert(detection.Records[3].Issues.Any(x => x.Code == "latitude-invalid"), "an object latitude must be rejected");
Assert(detection.Records[3].Issues.Any(x => x.Code == "longitude-invalid"), "an array longitude must be rejected");
Assert(detection.Records[3].RequiresQuarantine, "structured coordinate values must require reconciliation");
Assert(detection.Records[4].Issues.Any(x => x.Code == "latitude-invalid"), "a present null latitude must be rejected");
Assert(detection.Records[4].Issues.Any(x => x.Code == "longitude-invalid"), "a present string longitude must be rejected");
Assert(detection.Records[4].RequiresQuarantine, "null and string coordinate values must require reconciliation");
Assert(detection.Records[5].Issues.Any(x => x.Code == "latitude-invalid"), "an array latitude must be rejected");
Assert(detection.Records[5].Issues.Any(x => x.Code == "longitude-invalid"), "an object longitude must be rejected");
Assert(detection.Records[5].RequiresQuarantine, "array and object coordinate values must require reconciliation");

var storedDetection = await detectionImporter.AnalyzeAsync(
    new LegacyImportSource("stored-detection-fixture", args[2], LegacySourceKind.DetectionCases),
    owners,
    zones);
Assert(storedDetection.Records.Count == 6, "the untouched localStorage version 1 envelope must remain recoverable after route retirement");
Assert(storedDetection.Records[0].OriginalJson.Contains("recordedAt"), "recovery must preserve the original local record");

Console.WriteLine("legacy-import-contract-ok");

static void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static async Task AssertThrows<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

sealed class TestCatalogs : ILegacyCatalogLookup
{
    private static readonly Dictionary<string, HashSet<string>> Values = new()
    {
        ["soils"] = ["unknown", "andosol"],
        ["seasons"] = ["unknown", "dry"],
        ["findings"] = ["pending", "confirmed", "false-positive"],
        ["environmentReviews"] = ["pending"],
        ["platforms"] = ["if1200a"],
        ["indicationSignatures"] = ["surface-removal"],
        ["sensors"] = ["LiDAR", "RGB"],
        ["riskLevels"] = ["low", "medium", "high"],
        ["workflowStages"] = ["indications", "reconnaissance", "surveillance", "mitigation"],
    };

    public bool Contains(string catalogKey, string code) =>
        Values.TryGetValue(catalogKey, out var codes) && codes.Contains(code);
}

sealed class MemorySink : ILegacyImportSink
{
    private readonly Dictionary<string, LegacyImportDisposition> processed = [];
    private readonly Dictionary<(Guid BatchId, int SourceIndex), (string Identity, LegacyImportDisposition Disposition)> links = [];

    public int OriginalCount => processed.Count;

    public bool Completed { get; private set; }

    public Task EnsureBatchAsync(LegacyImportBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<LegacyImportDisposition> ImportRecordAsync(
        LegacyImportBatch batch,
        LegacyRecordAnalysis record,
        CancellationToken cancellationToken = default)
    {
        var key = $"{batch.SourceName}|{record.SourceId}|{record.SourceHash}";
        var link = (batch.Id, record.SourceIndex);
        if (links.TryGetValue(link, out var linked))
        {
            if (linked.Identity != key)
                throw new InvalidOperationException("source index was linked to a different record");
            return Task.FromResult(LegacyImportDisposition.AlreadyProcessed);
        }

        if (processed.TryGetValue(key, out var originalDisposition))
        {
            var occurrenceDisposition = record.RequiresQuarantine
                ? LegacyImportDisposition.Quarantined
                : LegacyImportDisposition.Imported;
            links[link] = (key, occurrenceDisposition);
            if (record.RequiresQuarantine) return Task.FromResult(LegacyImportDisposition.Quarantined);
            if (originalDisposition == LegacyImportDisposition.Imported)
                return Task.FromResult(LegacyImportDisposition.AlreadyProcessed);
            processed[key] = LegacyImportDisposition.Imported;
            return Task.FromResult(LegacyImportDisposition.Imported);
        }

        var disposition = record.RequiresQuarantine
            ? LegacyImportDisposition.Quarantined
            : LegacyImportDisposition.Imported;
        processed[key] = disposition;
        links[link] = (key, disposition);
        return Task.FromResult(disposition);
    }

    public Task<LegacyImportResult> CompleteBatchAsync(
        LegacyImportBatch batch,
        LegacyImportAttemptResult attempt,
        CancellationToken cancellationToken = default)
    {
        var batchLinks = links.Where(item => item.Key.BatchId == batch.Id).ToArray();
        if (batchLinks.Length != batch.RecordCount)
            throw new InvalidOperationException("batch membership is incomplete");
        var imported = batchLinks.Count(item => item.Value.Disposition == LegacyImportDisposition.Imported);
        var quarantined = batchLinks.Count(item => item.Value.Disposition == LegacyImportDisposition.Quarantined);
        Completed = true;
        return Task.FromResult(new LegacyImportResult(
            attempt,
            imported,
            quarantined,
            batchLinks.Length,
            batch.RecordCount,
            quarantined > 0 ? "completed_with_errors" : "completed"));
    }
}
'''


class LegacyImportContractTests(unittest.TestCase):
    def test_analysis_and_import_contract(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gds-legacy-import-") as directory:
            temp = Path(directory)
            notes = temp / "notes.json"
            detection = temp / "detection.json"
            stored_detection = temp / "stored-detection.json"
            notes.write_text(json.dumps(NOTES_FIXTURE, ensure_ascii=False, separators=(",", ":")))
            detection.write_text(json.dumps(DETECTION_FIXTURE, ensure_ascii=False, separators=(",", ":")))
            stored_detection.write_text(json.dumps({"version": 1, "cases": DETECTION_FIXTURE["cases"]}, ensure_ascii=False, separators=(",", ":")))
            (temp / "Harness.csproj").write_text(textwrap.dedent(f"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="{(ROOT / 'backend' / 'Import' / '*.cs').as_posix()}" Link="Import/%(Filename)%(Extension)" />
                    <Compile Remove="{(ROOT / 'backend' / 'Import' / 'PostgresLegacy*.cs').as_posix()}" />
                  </ItemGroup>
                </Project>
            """))
            (temp / "Program.cs").write_text(HARNESS)

            completed = subprocess.run(
                ["dotnet", "run", "--project", str(temp / "Harness.csproj"), "--", str(notes), str(detection), str(stored_detection)],
                cwd=ROOT,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                timeout=120,
            )

            self.assertEqual(completed.returncode, 0, completed.stdout)
            self.assertIn("legacy-import-contract-ok", completed.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
