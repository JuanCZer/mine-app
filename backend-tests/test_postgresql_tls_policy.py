"""PostgreSQL TLS policy scenarios. The harness never opens a database connection."""
from pathlib import Path
import os
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "backend-tests" / "PostgresConfigurationHarness" / "PostgresConfigurationHarness.csproj"


class PostgresTlsPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            ["dotnet", "build", str(PROJECT), "--nologo"], capture_output=True, text=True
        )
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)

    def scenario(self, name):
        result = subprocess.run(
            ["dotnet", "run", "--no-build", "--project", str(PROJECT), "--", name],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_production_accepts_verify_full_and_reuses_the_primary_pool(self):
        self.scenario("production-secure")

    def test_production_rejects_prefer(self):
        self.scenario("production-prefer")

    def test_production_rejects_require(self):
        self.scenario("production-require")

    def test_production_rejects_trust_server_certificate(self):
        self.scenario("production-trust")

    def test_production_rejects_trust_server_certificate_alias(self):
        self.scenario("production-trust-alias")

    def test_catalog_override_obeys_the_same_policy(self):
        self.scenario("production-insecure-catalog")

    def test_development_temporarily_allows_current_server_limitation(self):
        self.scenario("development-prefer")

    def test_testing_temporarily_allows_current_server_limitation(self):
        self.scenario("testing-prefer")

    def test_testing_catalog_only_configuration_does_not_invent_primary_credentials(self):
        self.scenario("testing-catalog-only")

    def test_non_development_environment_fails_closed(self):
        self.scenario("staging-prefer")

    def test_malformed_connection_is_rejected_without_disclosure(self):
        self.scenario("invalid")

    def test_provision_user_path_fails_closed_without_disclosing_connection(self):
        secret = "never-print-this-secret"
        host = "sensitive-db.internal.example"
        env = {
            **os.environ,
            "ASPNETCORE_ENVIRONMENT": "Production",
            "ConnectionStrings__Postgres": (
                f"Host={host};Database=gds;Username=runtime;Password={secret};Ssl Mode=Prefer"
            ),
        }
        result = subprocess.run(
            [
                "dotnet", "run", "--no-build", "--project", str(ROOT / "backend"), "--",
                "provision-user", "--username", "unused", "--display-name", "Unused", "--role", "operador",
            ],
            env=env,
            capture_output=True,
            text=True,
        )
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 1, output)
        self.assertIn("No se pudo aprovisionar la cuenta", output)
        self.assertNotIn(secret, output)
        self.assertNotIn(host, output)


if __name__ == "__main__":
    unittest.main()
