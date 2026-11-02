from __future__ import annotations

import os
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "backend-import" / "LegacyImport.Tool.csproj"


class LegacyImportToolTests(unittest.TestCase):
    def run_tool(self, *arguments: str, connection: str | None = None) -> subprocess.CompletedProcess[str]:
        environment = {
            key: value
            for key, value in os.environ.items()
            if key.lower() != "postgres_deploy_url" and not key.lower().startswith("connectionstrings__")
        }
        if connection is not None:
            environment["POSTGRES_DEPLOY_URL"] = connection
        return subprocess.run(
            ["dotnet", "run", "--no-build", "--project", str(PROJECT), "--", *arguments],
            cwd=ROOT,
            env=environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=30,
        )

    def test_requires_deployment_connection(self) -> None:
        completed = self.run_tool("analyze")
        self.assertEqual(completed.returncode, 1, completed.stdout)
        self.assertIn("POSTGRES_DEPLOY_URL", completed.stdout)

    def test_rejects_non_verifying_tls_without_leaking_connection(self) -> None:
        secret = "do-not-log-this-password"
        completed = self.run_tool(
            "analyze",
            connection=f"postgresql://operator:{secret}@127.0.0.1:5432/mine_app?sslmode=require",
        )
        self.assertEqual(completed.returncode, 1, completed.stdout)
        self.assertNotIn(secret, completed.stdout)
        self.assertIn("No se pudo ejecutar la importación", completed.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
