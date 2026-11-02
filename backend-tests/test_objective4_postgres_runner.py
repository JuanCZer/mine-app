"""Safety contracts for the Objective 4 disposable PostgreSQL runner."""
from pathlib import Path
import importlib.util
import os
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "backend-tests" / "objective4_postgres_runner.py"
SPEC = importlib.util.spec_from_file_location("objective4_postgres_runner", MODULE_PATH)
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class Objective4PostgresRunnerTests(unittest.TestCase):
    def test_cli_rejects_every_argument_without_echoing_it(self):
        supplied = "postgresql://admin:top-secret@remote/production"

        with self.assertRaises(runner.RunnerError) as caught:
            runner.validate_cli_arguments(["objective4_postgres_runner.py", "--dsn", supplied])

        self.assertNotIn(supplied, str(caught.exception))
        self.assertIn("no acepta argumentos", str(caught.exception))

    def test_docker_secret_file_is_private_and_removed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "container.env"

            with runner.docker_secret_file(path, {"POSTGRES_PASSWORD": "secret"}) as created:
                self.assertEqual(created, path)
                self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
                self.assertIn("POSTGRES_PASSWORD=secret", path.read_text())

            self.assertFalse(path.exists())

    def test_cleanup_identity_requires_container_id_and_run_label(self):
        self.assertTrue(runner.container_identity_matches("abc123", "run-1", "abc123 run-1\n"))
        self.assertFalse(runner.container_identity_matches("abc123", "run-1", "different run-1\n"))
        self.assertFalse(runner.container_identity_matches("abc123", "run-1", "abc123 another-run\n"))
        self.assertFalse(runner.container_identity_matches("abc123", "run-1", "malformed"))

    def test_creation_identity_can_be_recovered_from_cidfile_after_timeout(self):
        identifier = "a" * 64

        self.assertEqual(runner.created_container_id("", identifier + "\n"), identifier)
        self.assertEqual(runner.created_container_id(identifier + "\n", identifier + "\n"), identifier)
        with self.assertRaises(runner.RunnerError):
            runner.created_container_id("b" * 64, identifier)
        with self.assertRaises(runner.RunnerError):
            runner.created_container_id("", "not-an-id")

    def test_empty_timeout_recovers_owned_identity_by_random_name_and_label(self):
        container = runner.DisposablePostgres("/usr/bin/docker")
        identifier = "a" * 64
        timeout = subprocess.TimeoutExpired(["docker", "create"], 120)
        inspected = subprocess.CompletedProcess(
            [], 0, f"{identifier} {container.run_id}\n", ""
        )

        with patch.object(runner.subprocess, "run", side_effect=[timeout, inspected]) as run:
            with self.assertRaisesRegex(runner.RunnerError, "identidad se conservó"):
                container.start()

        self.assertEqual(container.container_id, identifier)
        self.assertTrue(container.started)
        self.assertEqual(run.call_args_list[1].args[0][-1], container.name)

    def test_transient_name_inspect_failure_is_recovered_by_second_cleanup(self):
        container = runner.DisposablePostgres("/usr/bin/docker")
        identifier = "a" * 64
        timeout = subprocess.TimeoutExpired(["docker", "create"], 120)
        unavailable = subprocess.CompletedProcess([], 1, "", "daemon unavailable")

        with patch.object(runner.subprocess, "run", side_effect=[timeout, unavailable]):
            with self.assertRaises(runner.RunnerError):
                container.start()

        self.assertTrue(container.cleanup_pending)
        self.assertEqual(container.cleanup_reference, container.name)
        inspected = subprocess.CompletedProcess(
            [], 0, f"{identifier} {container.run_id}\n", ""
        )
        removed = subprocess.CompletedProcess([], 0, identifier, "")
        with patch.object(runner.subprocess, "run", side_effect=[inspected, removed]) as cleanup:
            container.cleanup()

        self.assertFalse(container.cleanup_pending)
        self.assertTrue(any("rm" in call.args[0] for call in cleanup.call_args_list))

    def test_name_identity_mismatch_is_permanently_blocked_from_removal(self):
        container = runner.DisposablePostgres("/usr/bin/docker")
        timeout = subprocess.TimeoutExpired(["docker", "create"], 120)
        mismatch = subprocess.CompletedProcess([], 0, f"{'b' * 64} wrong-run\n", "")
        with patch.object(runner.subprocess, "run", side_effect=[timeout, mismatch]):
            with self.assertRaises(runner.RunnerError):
                container.start()

        self.assertTrue(container.cleanup_pending)
        self.assertTrue(container.cleanup_blocked)
        with patch.object(runner.subprocess, "run") as cleanup:
            with self.assertRaisesRegex(runner.RunnerError, "incompatible"):
                container.cleanup()
        cleanup.assert_not_called()

    def test_successful_create_without_ids_recovers_by_name_before_start(self):
        container = runner.DisposablePostgres("/usr/bin/docker")
        identifier = "a" * 64
        created_without_id = subprocess.CompletedProcess([], 0, "", "")
        inspected = subprocess.CompletedProcess(
            [], 0, f"{identifier} {container.run_id}\n", ""
        )
        start_failed = subprocess.CompletedProcess([], 1, "", "start failed")

        with patch.object(
            runner.subprocess, "run", side_effect=[created_without_id, inspected, start_failed]
        ) as run:
            with self.assertRaises(runner.RunnerError):
                container.start()

        self.assertEqual(run.call_args_list[1].args[0][-1], container.name)
        self.assertEqual(container.container_id, identifier)
        self.assertTrue(container.cleanup_pending)

    def owned_container(self):
        container = runner.DisposablePostgres("/usr/bin/docker")
        container.container_id = "a" * 64
        container.run_id = "objective4-test"
        container.started = True
        return container

    def test_cleanup_inspect_failure_stays_retryable(self):
        container = self.owned_container()
        failed = subprocess.CompletedProcess([], 1, "", "daemon unavailable")

        with patch.object(runner.subprocess, "run", return_value=failed):
            with self.assertRaisesRegex(runner.RunnerError, "limpieza"):
                container.cleanup()

        self.assertTrue(container.started)

    def test_cleanup_remove_failure_stays_retryable(self):
        container = self.owned_container()
        inspected = subprocess.CompletedProcess(
            [], 0, f"{container.container_id} {container.run_id}\n", ""
        )
        failed = subprocess.CompletedProcess([], 1, "", "remove failed")

        with patch.object(runner.subprocess, "run", side_effect=[inspected, failed]):
            with self.assertRaisesRegex(runner.RunnerError, "eliminación"):
                container.cleanup()

        self.assertTrue(container.started)

    def test_cleanup_success_clears_owned_state(self):
        container = self.owned_container()
        inspected = subprocess.CompletedProcess(
            [], 0, f"{container.container_id} {container.run_id}\n", ""
        )
        removed = subprocess.CompletedProcess([], 0, container.container_id, "")

        with patch.object(runner.subprocess, "run", side_effect=[inspected, removed]):
            container.cleanup()

        self.assertFalse(container.started)

    def test_postgres_image_is_fixed_to_the_multi_arch_index_digest(self):
        self.assertEqual(
            runner.POSTGRES_IMAGE,
            "postgres:17.6-alpine@sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94",
        )

    def test_response_evidence_includes_headers_and_only_real_secret_matches_fail(self):
        real_hash = b"AQAAAA-real-database-hash"
        evidence = runner.response_evidence(
            b'{"field":"PasswordHash"}', {"X-Debug": real_hash.decode()}
        )

        self.assertIn(b"X-Debug", evidence)
        self.assertIn(real_hash, evidence)
        runner.ensure_secrets_absent(b'{"field":"PasswordHash"}', [real_hash])
        with self.assertRaisesRegex(runner.RunnerError, "material secreto"):
            runner.ensure_secrets_absent(evidence, [real_hash])

    def test_provision_password_is_sent_only_through_stdin(self):
        environment = {"PATH": os.environ.get("PATH", "")}

        with patch.object(runner, "_run") as run:
            runner._provision(environment, "tester", "Tester", "operador", "private-password")

        kwargs = run.call_args.kwargs
        self.assertEqual(kwargs["input_text"], "private-password\n")
        self.assertNotIn("GDS_PROVISION_PASSWORD", kwargs["env"])
        self.assertNotIn("private-password", " ".join(run.call_args.args[0]))

    def test_preflight_rejects_connection_overrides_before_docker(self):
        checked = []

        with self.assertRaisesRegex(runner.RunnerError, "conexiones externas"):
            runner.preflight(
                {"ConnectionStrings__Postgres": "Host=remote;Password=secret"},
                find_executable=lambda name: checked.append(name) or "/usr/bin/docker",
            )

        self.assertEqual(checked, [])

    def test_preflight_failure_is_sanitized(self):
        with self.assertRaisesRegex(runner.RunnerError, "Docker no está disponible") as caught:
            runner.preflight({}, find_executable=lambda _: None)

        self.assertNotIn("Password", str(caught.exception))
        self.assertNotIn("postgresql://", str(caught.exception))

    def test_generated_runtime_environment_does_not_inherit_external_configuration(self):
        inherited = {
            "PATH": "/bin",
            "HOME": "/tmp/home",
            "POSTGRES_DEPLOY_URL": "postgresql://remote/production",
            "ConnectionStrings__Postgres": "Host=remote;Password=production",
            "Catalogs__Provider": "InMemory",
        }

        result = runner.runtime_environment(
            inherited,
            "Host=127.0.0.1;Port=55432;Database=test;Username=test;Password=generated",
            Path("/tmp/keys"),
        )

        self.assertEqual(result["PATH"], "/bin")
        self.assertEqual(result["ConnectionStrings__Postgres"], "Host=127.0.0.1;Port=55432;Database=test;Username=test;Password=generated")
        self.assertNotIn("POSTGRES_DEPLOY_URL", result)
        self.assertEqual(result["Notes__Provider"], "PostgreSql")
        self.assertEqual(result["Catalogs__Provider"], "PostgreSql")
        self.assertEqual(result["Authentication__EnableTestHeaders"], "false")

    def test_container_is_removed_when_scenario_fails(self):
        events = []

        class FakeContainer:
            def start(self):
                events.append("start")

            def cleanup(self):
                events.append("cleanup")

        def fail(_container):
            events.append("scenario")
            raise RuntimeError("failure")

        with self.assertRaisesRegex(RuntimeError, "failure"):
            runner.with_disposable_container(FakeContainer(), fail)

        self.assertEqual(events, ["start", "scenario", "cleanup"])

    def test_cleanup_runs_when_start_fails(self):
        events = []

        class FakeContainer:
            def start(self):
                events.append("start")
                raise RuntimeError("failure")

            def cleanup(self):
                events.append("cleanup")

        with self.assertRaisesRegex(RuntimeError, "failure"):
            runner.with_disposable_container(FakeContainer(), lambda _: None)

        self.assertEqual(events, ["start", "cleanup"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
