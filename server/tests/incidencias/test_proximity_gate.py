"""Deterministic concurrency and cancellation tests for the map work gate."""
from pathlib import Path
import subprocess
import unittest

ROOT = next(path for path in Path(__file__).resolve().parents if (path / "server" / "MineApp.Api.csproj").is_file())

class ProximityGateTests(unittest.TestCase):
    def test_gate_rejects_overload_and_releases_on_success_failure_and_cancellation(self):
        result = subprocess.run(['dotnet', 'run', '--project', str(ROOT / 'server/tests/harness/ProximityGateHarness')],
                                cwd=ROOT, text=True, capture_output=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

if __name__ == '__main__':
    unittest.main()
