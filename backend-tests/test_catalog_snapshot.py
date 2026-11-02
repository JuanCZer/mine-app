"""Runs the catalog validation snapshot contract against the public backend types."""
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "backend-tests" / "CatalogSnapshotHarness" / "CatalogSnapshotHarness.csproj"


class CatalogSnapshotTests(unittest.TestCase):
    def test_validation_freezes_catalog_codes_labels_and_versions(self) -> None:
        result = subprocess.run(
            ["dotnet", "run", "--project", str(PROJECT)],
            cwd=ROOT,
            text=True,
            capture_output=True,
            timeout=120,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_postgresql_save_uses_the_incident_transaction_and_locks_catalog_rows(self) -> None:
        source = (ROOT / "backend" / "Data" / "PostgresNoteRepository.cs").read_text(
            encoding="utf-8"
        )
        self.assertGreaterEqual(source.count("FindCatalogInTransactionAsync"), 3)
        self.assertIn("FOR SHARE", source)
        self.assertNotIn(
            "await catalogs.ValidateSelectionsAsync(request.Data, stage, current.Data, cancellationToken)",
            source,
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
