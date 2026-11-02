from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[1]


HARNESS = r'''
using Notes.Api.Import;

Assert(LegacyImportPostgresPolicy.SourceLockIdentity("ab", "c") == "source:2:ab:1:c",
    "source lock identity must be length-delimited");
Assert(LegacyImportPostgresPolicy.SourceLockIdentity("ab", "c")
       != LegacyImportPostgresPolicy.SourceLockIdentity("a", "bc"),
    "different source identities must not collapse");
Assert(!LegacyImportPostgresPolicy.SourceLockIdentity("source", "record").Contains('\0'),
    "PostgreSQL text lock identities cannot contain NUL");

var targetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
var targetLocks = LegacyImportPostgresPolicy.TargetLockIdentities(targetId, "INC-1");
Assert(targetLocks.SequenceEqual(new[] {
        "target-id:36:11111111-1111-1111-1111-111111111111",
        "target-public:5:INC-1",
    }), "each unique destination key must have a deterministic independent lock");
Assert(targetLocks.Intersect(LegacyImportPostgresPolicy.TargetLockIdentities(targetId, "INC-2")).Any(),
    "records sharing the internal UUID must serialize even when public IDs differ");
Assert(targetLocks.Intersect(LegacyImportPostgresPolicy.TargetLockIdentities(Guid.NewGuid(), "INC-1")).Any(),
    "records sharing the public ID must serialize even when internal UUIDs differ");

Assert(LegacyImportPostgresPolicy.CanStoreParsedPayload("{}"), "objects may be stored as parsed JSON");
Assert(LegacyImportPostgresPolicy.CanStoreParsedPayload("[]"), "arrays may be stored as parsed JSON");
Assert(!LegacyImportPostgresPolicy.CanStoreParsedPayload("42"), "numeric scalars must remain only in the original archive");
Assert(!LegacyImportPostgresPolicy.CanStoreParsedPayload("\"text\""), "string scalars must remain only in the original archive");
Assert(!LegacyImportPostgresPolicy.CanStoreParsedPayload("null"), "null must remain only in the original archive");

var firstOwner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
var secondOwner = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
var owners = LegacyImportPostgresPolicy.DistinctOwnerIds(
    new Dictionary<string, Guid> { ["one"] = firstOwner, ["alias"] = firstOwner, ["two"] = secondOwner });
Assert(owners.SequenceEqual(new[] { firstOwner, secondOwner }),
    "owner validation must query every distinct mapped UUID exactly once in deterministic order");

var complete = LegacyImportPostgresPolicy.EvaluateBatchCompletion(3, 3, 2, 1, 0);
Assert(complete.CanComplete && complete.Status == "completed_with_errors",
    "a fully linked batch must preserve its cumulative quarantine status");
var missingLink = LegacyImportPostgresPolicy.EvaluateBatchCompletion(3, 2, 2, 0, 0);
Assert(!missingLink.CanComplete && missingLink.Status == "running",
    "a batch missing a source occurrence cannot complete");
var pending = LegacyImportPostgresPolicy.EvaluateBatchCompletion(3, 3, 2, 0, 1);
Assert(!pending.CanComplete && pending.Status == "running",
    "a batch with a pending original cannot complete");

Assert(LegacyImportPostgresPolicy.IsRecoverablePostgresSqlState("22P02"),
    "invalid data representation is deterministic record input");
Assert(LegacyImportPostgresPolicy.IsRecoverablePostgresSqlState("23503"),
    "a foreign-key violation is deterministic record data");
foreach (var sqlState in new[] { "40001", "40P01", "57014", "53300", "08006" })
    Assert(!LegacyImportPostgresPolicy.IsRecoverablePostgresSqlState(sqlState),
        $"transient or operational SQLSTATE {sqlState} must propagate for retry");

Console.WriteLine("legacy-import-postgres-policy-ok");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
'''


class LegacyImportPostgresPolicyTests(unittest.TestCase):
    def test_lock_payload_and_owner_policies(self) -> None:
        with tempfile.TemporaryDirectory(prefix="gds-import-policy-") as directory:
            temp = Path(directory)
            (temp / "Harness.csproj").write_text(textwrap.dedent(f"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="{(ROOT / 'backend' / 'Import' / 'LegacyImportPostgresPolicy.cs').as_posix()}" Link="LegacyImportPostgresPolicy.cs" />
                  </ItemGroup>
                </Project>
            """))
            (temp / "Program.cs").write_text(HARNESS)

            completed = subprocess.run(
                ["dotnet", "run", "--project", str(temp / "Harness.csproj")],
                cwd=ROOT,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                timeout=120,
            )

            self.assertEqual(completed.returncode, 0, completed.stdout)
            self.assertIn("legacy-import-postgres-policy-ok", completed.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
