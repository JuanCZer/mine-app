"""Ownership and scenario contracts for the isolated user-administration runner."""
from pathlib import Path
import importlib.util
import inspect
import os
import socket
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "backend-tests" / "user_administration_postgres_runner.py"
SPEC = importlib.util.spec_from_file_location("user_administration_postgres_runner", MODULE_PATH)
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class UserAdministrationPostgresRunnerTests(unittest.TestCase):
    def test_runner_rejects_arguments_and_external_dsns(self):
        secret = "postgresql://admin:secret@remote/production"
        with self.assertRaises(runner.RunnerError) as caught:
            runner.validate_cli_arguments(["runner.py", "--dsn", secret])
        self.assertNotIn(secret, str(caught.exception))
        checked = []
        for key in runner.FORBIDDEN_CONNECTION_KEYS:
            with self.subTest(key=key), self.assertRaisesRegex(runner.RunnerError, "externas"):
                runner.preflight({key: secret}, find_executable=lambda name: checked.append(name) or "/docker")
        self.assertEqual([], checked)

    def test_runner_removes_only_its_verified_container(self):
        container = runner.DisposablePostgres("/usr/bin/docker")
        container.container_id = "a" * 64
        container.cleanup_reference = container.container_id
        container.started = True
        mismatch = subprocess.CompletedProcess([], 0, f"{'b' * 64} another-run\n", "")
        with patch.object(runner.subprocess, "run", return_value=mismatch) as command:
            with self.assertRaisesRegex(runner.RunnerError, "incompatible"):
                container.cleanup()
        self.assertEqual(1, command.call_count)
        self.assertNotIn("rm", command.call_args.args[0])

    def test_runner_exercises_clean_and_upgrade_databases_separately(self):
        plan = runner.scenario_plan()
        self.assertEqual({"fresh", "empty_upgrade", "upgrade"}, set(plan))
        self.assertNotEqual(plan["fresh"].database, plan["upgrade"].database)
        self.assertNotEqual(plan["empty_upgrade"].database, plan["upgrade"].database)
        self.assertEqual((runner.APPLY,), plan["fresh"].scripts)
        self.assertEqual((runner.FIXTURE, runner.DELTA, runner.DELTA), plan["empty_upgrade"].scripts)
        self.assertEqual((runner.FIXTURE, runner.DELTA, runner.DELTA), plan["upgrade"].scripts)
        self.assertNotIn(runner.FIXTURE, plan["fresh"].scripts)
        self.assertNotIn(runner.APPLY, plan["upgrade"].scripts)

    def test_runner_requires_and_pins_existing_local_unix_docker_socket(self):
        with tempfile.TemporaryDirectory() as directory:
            socket_path = Path(directory) / "docker.sock"
            listener = socket.socket(socket.AF_UNIX)
            listener.bind(str(socket_path))
            try:
                inspected = subprocess.CompletedProcess([], 0, f"unix://{socket_path}\n", "")
                endpoint = runner.preflight(
                    {"PATH": "/bin", "DOCKER_CONTEXT": "local-test"},
                    find_executable=lambda _: "/usr/bin/docker",
                    run_command=lambda *args, **kwargs: inspected,
                )
            finally:
                listener.close()
            self.assertEqual("/usr/bin/docker", endpoint.executable)
            self.assertEqual(f"unix://{socket_path}", endpoint.host)
            self.assertEqual(endpoint.host, endpoint.environment["DOCKER_HOST"])
            self.assertNotIn("DOCKER_CONTEXT", endpoint.environment)

    def test_runner_rejects_remote_or_missing_docker_endpoint_before_mutation(self):
        for endpoint in ("tcp://127.0.0.1:2375", "ssh://host/run/docker.sock", "npipe:////./pipe/docker_engine"):
            calls = []
            with self.subTest(endpoint=endpoint), self.assertRaisesRegex(runner.RunnerError, "Unix local"):
                runner.preflight(
                    {"PATH": "/bin", "DOCKER_HOST": endpoint},
                    find_executable=lambda _: "/usr/bin/docker",
                    run_command=lambda *args, **kwargs: calls.append(args) or None,
                )
            self.assertEqual([], calls)
        inspected = subprocess.CompletedProcess([], 0, "unix:///definitely/missing/docker.sock\n", "")
        with self.assertRaisesRegex(runner.RunnerError, "socket Unix local"):
            runner.preflight(
                {"PATH": "/bin"}, find_executable=lambda _: "/usr/bin/docker",
                run_command=lambda *args, **kwargs: inspected,
            )

    def test_runner_captures_every_reconciliation_problem_report(self):
        valid = "\n".join((
            "identity_reconciliation_begin",
            "identity_baseline:platform_public_database|true|true",
            "identity_baseline:platform_pg_catalog_execute|123",
            "identity_baseline:runtime_privilege|gds_app_runtime|false|false",
            runner.RECONCILIATION_MANIFEST_LINE,
            "identity_reconciliation_end",
        ))
        runner.assert_reconciliation_output(valid)
        with self.assertRaisesRegex(runner.RunnerError, "deriva"):
            runner.assert_reconciliation_output(valid.replace(
                runner.RECONCILIATION_MANIFEST_LINE,
                "identity_problem:managed_acl|unexpected|table|public.AspNetUsers|DELETE\n" + runner.RECONCILIATION_MANIFEST_LINE,
            ))
        with self.assertRaisesRegex(runner.RunnerError, "incompleta"):
            runner.assert_reconciliation_output(valid.replace("identity_reconciliation_end", ""))

    def test_runner_login_reconciles_noinherit_attributes_and_only_admin_membership(self):
        setup = inspect.getsource(runner._prepare_identity_harness_login)
        self.assertIn(
            "CREATE ROLE gds_identity_runner_login LOGIN NOINHERIT",
            setup,
        )
        self.assertIn(
            "ALTER ROLE gds_identity_runner_login LOGIN NOINHERIT NOSUPERUSER "
            "NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1",
            setup,
        )
        self.assertIn("ALTER ROLE gds_identity_runner_login RESET ALL", setup)
        self.assertIn("FROM pg_catalog.pg_auth_members", setup)
        self.assertIn("parent.rolname <> 'gds_identity_admin'", setup)
        self.assertIn("REVOKE gds_identity_admin FROM gds_identity_runner_login", setup)
        self.assertIn(
            "GRANT gds_identity_admin TO gds_identity_runner_login "
            "WITH ADMIN FALSE, INHERIT FALSE, SET TRUE",
            setup,
        )

    def test_real_identity_scenario_denies_managed_access_before_verified_set_role(self):
        harness = (ROOT / "backend-tests" / "UserAdministrationHarness" / "Program.cs").read_text()
        begin = harness.index('case "identity-admin-real-current-role"')
        end = harness.index('case "identity-admin-real-no-primary-fallback"', begin)
        scenario = harness[begin:end]
        self.assertIn("sources.IdentityAdmin.OpenConnectionAsync", scenario)
        self.assertIn("PostgresErrorCodes.InsufficientPrivilege", scenario)
        self.assertIn("managed access must be denied before SET ROLE", scenario)
        self.assertIn("factory.OpenVerifiedAsync", scenario)
        self.assertIn("managed access must succeed after verified SET ROLE", scenario)
        self.assertLess(
            scenario.index("sources.IdentityAdmin.OpenConnectionAsync"),
            scenario.index("factory.OpenVerifiedAsync"),
        )

    def test_upgrade_runner_injects_drift_preserves_other_roles_and_completes_r17(self):
        drift = inspect.getsource(runner._inject_upgrade_privilege_drift)
        self.assertIn("ALTER ROLE gds_identity_admin LOGIN INHERIT", drift)
        self.assertIn("CREATEDB CREATEROLE REPLICATION BYPASSRLS", drift)
        self.assertIn("GRANT gds_identity_drift_parent TO gds_identity_admin", drift)
        self.assertIn("GRANT CREATE ON SCHEMA app TO gds_identity_admin", drift)
        self.assertIn("GRANT CREATE ON SCHEMA public TO gds_identity_drift_parent", drift)
        self.assertIn("gds_identity_third_party", drift)

        class RecordingContainer:
            def __init__(self):
                self.query = ""

            def psql(self, database, query, *, step):
                self.query = query
                return '{"role": "gds_app_runtime"}'

        recording = RecordingContainer()
        snapshot = runner.role_privilege_snapshot(recording, "upgrade", "gds_app_runtime")
        self.assertEqual('{"role": "gds_app_runtime"}', snapshot)
        for catalog in (
            "pg_roles", "pg_auth_members", "pg_database", "pg_namespace",
            "pg_class", "pg_attribute", "pg_proc", "aclexplode",
        ):
            self.assertIn(catalog, recording.query)
        with self.assertRaisesRegex(runner.RunnerError, "manifiesto local"):
            runner.role_privilege_snapshot(recording, "upgrade", "unsafe';drop role x;--")

        upgrade = inspect.getsource(runner._upgrade_scenario)
        self.assertLess(
            upgrade.index("_inject_upgrade_privilege_drift"),
            upgrade.index("for pass_number"),
        )
        self.assertEqual(4, upgrade.count("role_privilege_snapshot("))
        self.assertIn("runtime_after != runtime_before", upgrade)
        self.assertIn("third_party_after != third_party_before", upgrade)
        self.assertIn('(\"REPEATABLE READ\", \"SERIALIZABLE\")', upgrade)
        self.assertIn("_read_committed_last_admin_race", upgrade)

        race = inspect.getsource(runner._read_committed_last_admin_race)
        self.assertNotIn("threading.Barrier", race)
        self.assertIn("session.start()", race)
        self.assertIn("session.release_mutation()", race)
        self.assertEqual(2, race.count("executor.submit("))
        self.assertIn("future.result(timeout=40)", race)
        self.assertIn("len(successes) != 1 or len(failures) != 1", race)
        self.assertIn("identity_last_active_admin", race)
        self.assertIn('if active != \"1\"', race)
        self.assertIn("finally:", race)
        self.assertIn("session.cleanup()", race)

        module_source = MODULE_PATH.read_text()
        persistent = module_source[
            module_source.index("class PostgresRaceSession"):
            module_source.index("\ndef _uuid", module_source.index("class PostgresRaceSession"))
        ]
        self.assertIn("subprocess.Popen", persistent)
        self.assertIn("BEGIN ISOLATION LEVEL READ COMMITTED", persistent)
        self.assertIn("SET LOCAL app.actor_user_id", persistent)
        self.assertIn("pg_backend_pid()", persistent)
        self.assertIn("GDS_RACE_READY", persistent)
        self.assertIn("GDS_RACE_RELEASED", persistent)
        self.assertIn("pg_catalog.pg_terminate_backend", persistent)

        events = []

        class FakeContainer:
            def psql(self, database, sql, *, step):
                events.append("final-state")
                return "1"

        class FakeSession:
            instances = []

            def __init__(self, container, database, actor_id, target_id):
                self.number = len(self.instances)
                self.instances.append(self)

            def start(self):
                events.append(f"ready-{self.number}")

            def release_mutation(self):
                self.assert_both_ready()
                events.append(f"release-{self.number}")

            def assert_both_ready(self):
                if events[:2] != ["ready-0", "ready-1"]:
                    raise AssertionError("mutation released before both sessions were ready")

            def wait_result(self):
                if self.number == 0:
                    return subprocess.CompletedProcess([], 0, "", "")
                return subprocess.CompletedProcess([], 1, "", "identity_last_active_admin")

            def cleanup(self):
                events.append(f"cleanup-{self.number}")

        runner._read_committed_last_admin_race(
            FakeContainer(), "upgrade", runner._uuid(20), runner._uuid(21),
            session_factory=FakeSession,
        )
        self.assertLess(events.index("ready-1"), events.index("release-0"))
        self.assertLess(events.index("release-1"), events.index("final-state"))
        self.assertIn("cleanup-0", events)
        self.assertIn("cleanup-1", events)


if __name__ == "__main__":
    unittest.main()
