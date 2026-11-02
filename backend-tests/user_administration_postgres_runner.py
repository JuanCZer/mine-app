#!/usr/bin/env python3
"""Run user-administration SQL against one runner-owned disposable PostgreSQL.

The runner accepts neither arguments nor connection configuration. Fresh and
upgrade databases are created inside a uniquely labelled container, exercised
independently, and removed only after its ID and unguessable label are verified.
"""
from __future__ import annotations

from contextlib import contextmanager
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Callable, Mapping
import os
import queue
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time


ROOT = Path(__file__).resolve().parents[1]
APPLY = ROOT / "backend" / "Database" / "apply.sql"
DELTA = ROOT / "backend" / "Database" / "user-administration.sql"
FIXTURE = ROOT / "backend-tests" / "fixtures" / "pre-user-administration-apply.sql.fixture"
POSTGRES_IMAGE = (
    "postgres:17.6-alpine@sha256:"
    "ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94"
)
FORBIDDEN_CONNECTION_KEYS = frozenset({
    "DATABASE_URL", "POSTGRES_DEPLOY_URL", "POSTGRES_URL", "PGHOST", "PGHOSTADDR",
    "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD", "PGSERVICE", "PGSERVICEFILE",
    "ConnectionStrings__Postgres", "ConnectionStrings__Catalogs",
    "ConnectionStrings__IdentityAdmin",
})
SAFE_INHERITED_ENV = frozenset({
    "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL",
})
RECONCILIATION_PROBLEM_REPORTS = (
    "active_administrator", "active_rate_key", "constraint_metadata",
    "function_metadata", "identity_columns", "identity_tables", "managed_acl",
    "memberships", "owned_objects", "public_extension_execute", "role_attributes",
    "role_catalog", "trigger_metadata", "user_role_cardinality",
)
RECONCILIATION_MANIFEST_LINE = (
    "identity_reconciliation_manifest|" + ",".join(RECONCILIATION_PROBLEM_REPORTS)
)


class RunnerError(RuntimeError):
    """Expected runner failure whose message contains no subprocess output."""


class ContainerIdentityMismatch(RunnerError):
    """A Docker reference did not resolve to this runner's ID and label."""


class Scenario:
    def __init__(self, database: str, scripts: tuple[Path, ...]):
        self.database = database
        self.scripts = scripts


class LocalDocker:
    def __init__(self, executable: str, host: str, environment: Mapping[str, str]):
        self.executable = executable
        self.host = host
        self.environment = dict(environment)


def scenario_plan() -> dict[str, Scenario]:
    """Return the immutable clean/upgrade input split used by integration."""
    return {
        "fresh": Scenario("gds_identity_fresh", (APPLY,)),
        "empty_upgrade": Scenario("gds_identity_empty_upgrade", (FIXTURE, DELTA, DELTA)),
        "upgrade": Scenario("gds_identity_upgrade", (FIXTURE, DELTA, DELTA)),
    }


def validate_cli_arguments(arguments: list[str]):
    if len(arguments) != 1:
        raise RunnerError("Este runner no acepta argumentos ni conexiones externas.")


def preflight(
    environment: Mapping[str, str],
    find_executable: Callable[[str], str | None] = shutil.which,
    run_command: Callable = subprocess.run,
) -> LocalDocker:
    forbidden = {key.casefold() for key in FORBIDDEN_CONNECTION_KEYS}
    if any(key.casefold() in forbidden for key in environment):
        raise RunnerError(
            "El runner rechazó conexiones externas; elimine variables PostgreSQL/DSN del entorno."
        )
    for path in (APPLY, DELTA, FIXTURE):
        if not path.is_file():
            raise RunnerError("Falta un artefacto SQL local requerido; no se inició PostgreSQL.")
    docker = find_executable("docker")
    if not docker:
        raise RunnerError(
            "Docker local no está disponible; no se creó ni modificó ninguna base de datos."
        )
    selected_host = environment.get("DOCKER_HOST", "").strip()
    if not selected_host:
        context_name = environment.get("DOCKER_CONTEXT", "").strip()
        command = [docker, "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"]
        if context_name:
            command.append(context_name)
        try:
            inspected = run_command(
                command, cwd=ROOT, env=_safe_environment(environment), capture_output=True,
                text=True, timeout=15, check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RunnerError("No se pudo resolver el endpoint Docker local antes de mutar.") from error
        if inspected is None or inspected.returncode != 0:
            raise RunnerError("No se pudo resolver el endpoint Docker local antes de mutar.")
        selected_host = inspected.stdout.strip()
    if not selected_host.startswith("unix://"):
        raise RunnerError("Docker debe usar un socket Unix local; se rechazó el endpoint seleccionado.")
    socket_path = selected_host.removeprefix("unix://")
    if not socket_path.startswith("/") or "?" in socket_path or "#" in socket_path:
        raise RunnerError("Docker debe usar un socket Unix local absoluto.")
    try:
        mode = os.stat(socket_path).st_mode
    except OSError as error:
        raise RunnerError("El socket Unix local de Docker no existe o no es accesible.") from error
    if not stat.S_ISSOCK(mode):
        raise RunnerError("El endpoint Docker seleccionado no es un socket Unix local.")
    pinned = _safe_environment(environment)
    pinned["DOCKER_HOST"] = selected_host
    pinned.pop("DOCKER_CONTEXT", None)
    return LocalDocker(docker, selected_host, pinned)


def _safe_environment(inherited: Mapping[str, str]) -> dict[str, str]:
    return {key: value for key, value in inherited.items() if key in SAFE_INHERITED_ENV}


@contextmanager
def docker_secret_file(path: Path, values: Mapping[str, str]):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            for key, value in values.items():
                if not re.fullmatch(r"[A-Z][A-Z0-9_]*", key) or "\n" in value or "\r" in value:
                    raise RunnerError("No se pudo crear el entorno privado del contenedor.")
                stream.write(f"{key}={value}\n")
        if stat.S_IMODE(path.stat().st_mode) != 0o600:
            raise RunnerError("El archivo temporal del contenedor no es privado.")
        yield path
    finally:
        path.unlink(missing_ok=True)


def created_container_id(stdout: str, cidfile: str) -> str:
    candidates = {item.strip() for item in (stdout, cidfile) if item.strip()}
    if len(candidates) != 1:
        raise RunnerError("Docker no confirmó una identidad única para el contenedor.")
    identifier = candidates.pop()
    if re.fullmatch(r"[0-9a-f]{64}", identifier) is None:
        raise RunnerError("Docker devolvió una identidad de contenedor inválida.")
    return identifier


def assert_reconciliation_output(output: str):
    lines = [line.strip() for line in output.splitlines() if line.strip()]
    try:
        begin = lines.index("identity_reconciliation_begin")
        end = lines.index("identity_reconciliation_end", begin + 1)
    except ValueError as error:
        raise RunnerError("La reconciliación PostgreSQL quedó incompleta.") from error
    if end != len(lines) - 1 or lines.count("identity_reconciliation_begin") != 1 or lines.count("identity_reconciliation_end") != 1:
        raise RunnerError("La reconciliación PostgreSQL quedó incompleta.")
    bounded = lines[begin + 1:end]
    if RECONCILIATION_MANIFEST_LINE not in bounded:
        raise RunnerError("La reconciliación PostgreSQL quedó incompleta.")
    if any(line.startswith("identity_problem:") for line in bounded):
        raise RunnerError("La reconciliación PostgreSQL detectó deriva.")


class DisposablePostgres:
    def __init__(self, docker: str | LocalDocker):
        nonce = secrets.token_hex(8)
        if isinstance(docker, LocalDocker):
            self.docker = docker.executable
            self.docker_environment = dict(docker.environment)
        else:
            self.docker = docker
            self.docker_environment = _safe_environment(os.environ)
        self.name = f"gds-identity-admin-{nonce}"
        self.run_id = f"identity-admin-{secrets.token_hex(16)}"
        self.container_id = ""
        self.cleanup_reference = ""
        self.started = False
        self.cleanup_blocked = False
        self.superuser = "postgres"
        self.password = "root_" + secrets.token_urlsafe(32)
        self.host_port = 0

    @property
    def cleanup_pending(self) -> bool:
        return self.started or bool(self.container_id) or bool(self.cleanup_reference)

    def _command(
        self, args: list[str], *, input_text: str | None = None, timeout: int = 120,
        expected_success: bool | None = True, step: str,
    ) -> subprocess.CompletedProcess[str]:
        try:
            result = subprocess.run(
                [self.docker, *args], cwd=ROOT, env=self.docker_environment,
                input=input_text, text=True, capture_output=True, timeout=timeout, check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RunnerError(f"Falló {step}; se suprimieron detalles del proceso.") from error
        if expected_success is True and result.returncode != 0:
            raise RunnerError(f"Falló {step}; se suprimieron detalles del proceso.")
        if expected_success is False and result.returncode == 0:
            raise RunnerError(f"La operación negativa '{step}' fue aceptada.")
        return result

    def start(self):
        with tempfile.TemporaryDirectory(prefix="gds-identity-container-") as directory_name:
            directory = Path(directory_name)
            env_file = directory / "postgres.env"
            cid_file = directory / "postgres.cid"
            result = None
            create_error = None
            with docker_secret_file(env_file, {
                "POSTGRES_USER": self.superuser,
                "POSTGRES_PASSWORD": self.password,
                "POSTGRES_DB": "postgres",
            }):
                self.cleanup_reference = self.name
                try:
                    result = subprocess.run(
                        [
                            self.docker, "create", "--name", self.name,
                            "--label", f"gds.identity-admin.run-id={self.run_id}",
                            "--cidfile", str(cid_file), "--env-file", str(env_file),
                            "--publish", "127.0.0.1::5432",
                            "--health-cmd", "pg_isready -U postgres -d postgres",
                            "--health-interval", "1s", "--health-timeout", "3s",
                            "--health-retries", "45", POSTGRES_IMAGE,
                        ],
                        cwd=ROOT, env=self.docker_environment, capture_output=True,
                        text=True, timeout=180, check=False,
                    )
                except (OSError, subprocess.TimeoutExpired) as error:
                    create_error = error
            cidfile_value = cid_file.read_text(encoding="utf-8").strip() if cid_file.exists() else ""
            stdout_value = result.stdout if result is not None else ""
            try:
                self.container_id = created_container_id(stdout_value, cidfile_value)
            except RunnerError:
                self.container_id = self._inspect_owned(self.name, "la recuperación por nombre")
            self.started = True
            self._validate_owned("la validación posterior a create")
            if create_error is not None or result is None or result.returncode != 0:
                raise RunnerError("Docker create falló; la identidad quedó disponible para limpieza.")
        self._command(["start", self.container_id], timeout=60, step="el inicio del contenedor")
        port_result = self._command(
            ["port", self.container_id, "5432/tcp"],
            step="la resolución del puerto PostgreSQL local",
        )
        port_match = re.fullmatch(r"127\.0\.0\.1:(\d+)", port_result.stdout.strip())
        if port_match is None:
            raise RunnerError("Docker no publicó PostgreSQL únicamente en loopback.")
        self.host_port = int(port_match.group(1))
        for _ in range(60):
            result = self._command(
                ["inspect", "--format={{.State.Health.Status}}", self.container_id],
                expected_success=None, step="la consulta de salud",
            )
            if result.returncode == 0 and result.stdout.strip() == "healthy":
                return
            time.sleep(1)
        raise RunnerError("PostgreSQL desechable no alcanzó estado saludable.")

    def _inspect_owned(self, reference: str, step: str) -> str:
        result = self._command(
            ["inspect", "--format={{.Id}} {{index .Config.Labels \"gds.identity-admin.run-id\"}}", reference],
            expected_success=None, timeout=30, step=step,
        )
        parts = result.stdout.strip().split()
        if result.returncode != 0 or len(parts) != 2 or re.fullmatch(r"[0-9a-f]{64}", parts[0]) is None or parts[1] != self.run_id:
            self.cleanup_blocked = True
            raise ContainerIdentityMismatch("La referencia de contenedor es incompatible.")
        return parts[0]

    def _validate_owned(self, step: str):
        observed = self._inspect_owned(self.container_id, step)
        if observed != self.container_id:
            self.cleanup_blocked = True
            raise ContainerIdentityMismatch("La referencia de contenedor es incompatible.")

    def cleanup(self):
        if not self.cleanup_pending:
            return
        if self.cleanup_blocked:
            raise RunnerError("La referencia de contenedor es incompatible; no se eliminará.")
        reference = self.container_id or self.cleanup_reference
        observed = self._inspect_owned(reference, "la inspección previa a cleanup")
        if self.container_id and observed != self.container_id:
            self.cleanup_blocked = True
            raise RunnerError("La referencia de contenedor es incompatible; no se eliminará.")
        self.container_id = observed
        result = self._command(
            ["rm", "--force", observed], expected_success=None, timeout=30,
            step="la eliminación del contenedor",
        )
        if result.returncode != 0:
            raise RunnerError("Falló la eliminación segura; la limpieza sigue pendiente.")
        self.started = False
        self.container_id = ""
        self.cleanup_reference = ""

    def psql(
        self, database: str, sql: str, *, expected_success: bool = True,
        step: str = "una operación SQL", timeout: int = 180,
    ) -> str:
        result = self._command(
            [
                "exec", "--interactive", self.container_id, "psql", "--no-psqlrc",
                "--set", "ON_ERROR_STOP=1", "--tuples-only", "--no-align", "--quiet",
                "--field-separator=|",
                "--username", self.superuser, "--dbname", database,
            ],
            input_text=sql, expected_success=expected_success, timeout=timeout, step=step,
        )
        return result.stdout.strip()

    def psql_result(
        self, database: str, sql: str, *, step: str, timeout: int = 180,
    ) -> subprocess.CompletedProcess[str]:
        """Execute a scenario statement while retaining its status for race assertions."""
        return self._command(
            [
                "exec", "--interactive", self.container_id, "psql", "--no-psqlrc",
                "--set", "ON_ERROR_STOP=1", "--set", "VERBOSITY=terse",
                "--tuples-only", "--no-align", "--quiet", "--field-separator=|",
                "--username", self.superuser, "--dbname", database,
            ],
            input_text=sql, expected_success=None, timeout=timeout, step=step,
        )

    def create_databases(self):
        for scenario in scenario_plan().values():
            self.psql("postgres", f'CREATE DATABASE "{scenario.database}";', step="la creación de una base desechable")

    def apply_file(self, database: str, path: Path, pass_name: str):
        output = self.psql(database, path.read_text(encoding="utf-8"), timeout=240, step=pass_name)
        if path in (APPLY, DELTA):
            assert_reconciliation_output(output)


class PostgresRaceSession:
    """One persistent psql transaction used by the last-administrator race."""

    def __init__(
        self, container: DisposablePostgres, database: str,
        actor_id: str, target_id: str,
    ):
        uuid_pattern = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
        if re.fullmatch(uuid_pattern, actor_id) is None or re.fullmatch(uuid_pattern, target_id) is None:
            raise RunnerError("La carrera recibió un identificador local inválido.")
        self.container = container
        self.database = database
        self.actor_id = actor_id
        self.target_id = target_id
        self.token = secrets.token_hex(16)
        self.ready_marker = f"GDS_RACE_READY_{self.token}"
        self.released_marker = f"GDS_RACE_RELEASED_{self.token}"
        self.process: subprocess.Popen[str] | None = None
        self.backend_pid: int | None = None
        self.stdout_queue: queue.Queue[str | None] = queue.Queue()
        self.stdout_lines: list[str] = []
        self.stderr_lines: list[str] = []
        self.reader_threads: list[threading.Thread] = []

    @staticmethod
    def _read_stdout(stream, output: queue.Queue[str | None], captured: list[str]):
        try:
            for line in iter(stream.readline, ""):
                value = line.rstrip("\r\n")
                captured.append(value)
                output.put(value)
        finally:
            output.put(None)

    @staticmethod
    def _read_stderr(stream, captured: list[str]):
        for line in iter(stream.readline, ""):
            captured.append(line.rstrip("\r\n"))

    def _send(self, sql: str):
        if self.process is None or self.process.stdin is None or self.process.poll() is not None:
            raise RunnerError("La sesión persistente PostgreSQL no está disponible.")
        try:
            self.process.stdin.write(sql)
            self.process.stdin.flush()
        except (BrokenPipeError, OSError) as error:
            raise RunnerError("La sesión persistente PostgreSQL terminó antes de tiempo.") from error

    def _wait_for_marker(self, marker: str, timeout: int) -> str | None:
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise RunnerError("La sesión persistente PostgreSQL excedió el timeout anti-deadlock.")
            try:
                line = self.stdout_queue.get(timeout=remaining)
            except queue.Empty as error:
                raise RunnerError("La sesión persistente PostgreSQL excedió el timeout anti-deadlock.") from error
            if line is None:
                return None
            if line.startswith(marker):
                return line

    def start(self):
        command = [
            self.container.docker, "exec", "--interactive", self.container.container_id,
            "psql", "--no-psqlrc", "--set", "ON_ERROR_STOP=1",
            "--set", "VERBOSITY=terse", "--tuples-only", "--no-align", "--quiet",
            "--username", self.container.superuser, "--dbname", self.database,
        ]
        try:
            self.process = subprocess.Popen(
                command, cwd=ROOT, env=self.container.docker_environment,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, bufsize=1,
            )
        except OSError as error:
            raise RunnerError("No se pudo abrir una sesión persistente PostgreSQL.") from error
        assert self.process.stdout is not None and self.process.stderr is not None
        self.reader_threads = [
            threading.Thread(
                target=self._read_stdout,
                args=(self.process.stdout, self.stdout_queue, self.stdout_lines), daemon=True,
            ),
            threading.Thread(
                target=self._read_stderr,
                args=(self.process.stderr, self.stderr_lines), daemon=True,
            ),
        ]
        for reader in self.reader_threads:
            reader.start()
        self._send(
            f"BEGIN ISOLATION LEVEL READ COMMITTED;\n"
            f"SET LOCAL app.actor_user_id='{self.actor_id}';\n"
            f"SELECT '{self.ready_marker}|' || pg_catalog.pg_backend_pid()::text;\n"
        )
        ready = self._wait_for_marker(self.ready_marker + "|", timeout=15)
        if ready is None:
            raise RunnerError("PostgreSQL cerró una sesión antes de confirmar readiness.")
        try:
            self.backend_pid = int(ready.split("|", 1)[1])
        except (IndexError, ValueError) as error:
            raise RunnerError("PostgreSQL devolvió un PID de sesión inválido.") from error
        if self.backend_pid <= 0:
            raise RunnerError("PostgreSQL devolvió un PID de sesión inválido.")

    def release_mutation(self):
        if self.backend_pid is None:
            raise RunnerError("No se puede liberar una sesión sin readiness PostgreSQL.")
        self._send(
            f'UPDATE public."AspNetUsers" SET "IsActive"=false '
            f'WHERE "Id"=\'{self.target_id}\';\n'
            "COMMIT;\n"
            f"SELECT '{self.released_marker}';\n"
            "\\quit\n"
        )

    def wait_result(self) -> subprocess.CompletedProcess[str]:
        if self.process is None:
            raise RunnerError("La sesión persistente PostgreSQL no fue iniciada.")
        self._wait_for_marker(self.released_marker, timeout=30)
        try:
            returncode = self.process.wait(timeout=5)
        except subprocess.TimeoutExpired as error:
            raise RunnerError("La sesión persistente no cerró tras la carrera.") from error
        for reader in self.reader_threads:
            reader.join(timeout=1)
        return subprocess.CompletedProcess(
            self.process.args, returncode,
            "\n".join(self.stdout_lines), "\n".join(self.stderr_lines),
        )

    def cleanup(self):
        process = self.process
        if process is None:
            return
        if process.poll() is None and self.backend_pid is not None:
            self.container.psql(
                self.database,
                f"SELECT pg_catalog.pg_terminate_backend({self.backend_pid})::int;",
                step="la terminación segura de una sesión de carrera",
                timeout=15,
            )
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if process.stdin is not None and not process.stdin.closed:
            try:
                process.stdin.close()
            except (BrokenPipeError, OSError):
                pass
        for reader in self.reader_threads:
            reader.join(timeout=1)


def _uuid(value: int) -> str:
    return f"00000000-0000-4000-8000-{value:012d}"


def _user_insert(user_id: str, name: str, *, upgraded: bool) -> str:
    administration = (
        ', "MustChangePassword", "PasswordChangedAt", "ManagementVersion"'
        if upgraded else ""
    )
    admin_values = ", false, now(), 1" if upgraded else ""
    return (
        'INSERT INTO public."AspNetUsers" '
        '("Id", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp", '
        '"ConcurrencyStamp", "DisplayName", "IsActive"' + administration + ") VALUES ("
        f"'{user_id}', '{name}', '{name.upper()}', 'hash', 'security', 'concurrency', '{name}', true"
        + admin_values + ")"
    )


def _assert_zero(container: DisposablePostgres, database: str, query: str, label: str):
    value = container.psql(database, query, step=label)
    if value != "0":
        raise RunnerError(f"La comprobación aislada '{label}' detectó deriva.")


def role_privilege_snapshot(
    container: DisposablePostgres, database: str, role_name: str,
) -> str:
    """Return a deterministic catalog snapshot of one role and its direct ACLs."""
    if re.fullmatch(r"[a-z][a-z0-9_]{0,62}", role_name) is None:
        raise RunnerError("El rol de snapshot no pertenece al manifiesto local.")
    snapshot = container.psql(
        database,
        f"""
        WITH target AS (
            SELECT * FROM pg_catalog.pg_roles WHERE rolname = '{role_name}'
        )
        SELECT pg_catalog.jsonb_build_object(
            'role', target.rolname,
            'attributes', pg_catalog.jsonb_build_array(
                target.rolcanlogin, target.rolinherit, target.rolsuper,
                target.rolcreatedb, target.rolcreaterole, target.rolreplication,
                target.rolbypassrls, target.rolconnlimit
            ),
            'member_of', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    parent.rolname, grantor.rolname, membership.admin_option,
                    membership.inherit_option, membership.set_option
                ) ORDER BY parent.rolname, grantor.rolname)
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
                JOIN pg_catalog.pg_roles grantor ON grantor.oid = membership.grantor
                WHERE membership.member = target.oid
            ), '[]'::pg_catalog.jsonb),
            'members', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    member.rolname, grantor.rolname, membership.admin_option,
                    membership.inherit_option, membership.set_option
                ) ORDER BY member.rolname, grantor.rolname)
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles member ON member.oid = membership.member
                JOIN pg_catalog.pg_roles grantor ON grantor.oid = membership.grantor
                WHERE membership.roleid = target.oid
            ), '[]'::pg_catalog.jsonb),
            'database_acl', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    database_object.datname, acl.privilege_type, acl.is_grantable
                ) ORDER BY database_object.datname, acl.privilege_type, acl.is_grantable)
                FROM pg_catalog.pg_database database_object
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                    database_object.datacl,
                    pg_catalog.acldefault('d', database_object.datdba)
                )) acl
                WHERE acl.grantee = target.oid
            ), '[]'::pg_catalog.jsonb),
            'schema_acl', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    namespace.nspname, acl.privilege_type, acl.is_grantable
                ) ORDER BY namespace.nspname, acl.privilege_type, acl.is_grantable)
                FROM pg_catalog.pg_namespace namespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                    namespace.nspacl,
                    pg_catalog.acldefault('n', namespace.nspowner)
                )) acl
                WHERE namespace.nspname IN ('public', 'app')
                  AND acl.grantee = target.oid
            ), '[]'::pg_catalog.jsonb),
            'relation_acl', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    namespace.nspname, relation.relname, relation.relkind,
                    acl.privilege_type, acl.is_grantable
                ) ORDER BY namespace.nspname, relation.relname, relation.relkind,
                           acl.privilege_type, acl.is_grantable)
                FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                    relation.relacl,
                    pg_catalog.acldefault(
                        CASE WHEN relation.relkind = 'S' THEN 'S'::"char" ELSE 'r'::"char" END,
                        relation.relowner
                    )
                )) acl
                WHERE namespace.nspname IN ('public', 'app')
                  AND acl.grantee = target.oid
            ), '[]'::pg_catalog.jsonb),
            'column_acl', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    namespace.nspname, relation.relname, attribute.attname,
                    acl.privilege_type, acl.is_grantable
                ) ORDER BY namespace.nspname, relation.relname, attribute.attname,
                           acl.privilege_type, acl.is_grantable)
                FROM pg_catalog.pg_attribute attribute
                JOIN pg_catalog.pg_class relation ON relation.oid = attribute.attrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                WHERE namespace.nspname IN ('public', 'app')
                  AND attribute.attnum > 0 AND NOT attribute.attisdropped
                  AND acl.grantee = target.oid
            ), '[]'::pg_catalog.jsonb),
            'routine_acl', COALESCE((
                SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_array(
                    routine.oid::pg_catalog.regprocedure::text,
                    acl.privilege_type, acl.is_grantable
                ) ORDER BY routine.oid::pg_catalog.regprocedure::text,
                           acl.privilege_type, acl.is_grantable)
                FROM pg_catalog.pg_proc routine
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = routine.pronamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                    routine.proacl,
                    pg_catalog.acldefault('f', routine.proowner)
                )) acl
                WHERE namespace.nspname IN ('public', 'app')
                  AND acl.grantee = target.oid
            ), '[]'::pg_catalog.jsonb)
        )::text
        FROM target;
        """,
        step=f"el snapshot de privilegios de {role_name}",
    )
    if not snapshot:
        raise RunnerError(f"El rol {role_name} no existe para el snapshot.")
    return snapshot


def _inject_upgrade_privilege_drift(container: DisposablePostgres, database: str):
    """Create managed identity drift and unrelated ACLs before applying the delta."""
    container.psql(
        database,
        """
        DO $roles$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='gds_identity_admin') THEN
                CREATE ROLE gds_identity_admin NOLOGIN NOINHERIT;
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='gds_identity_drift_parent') THEN
                CREATE ROLE gds_identity_drift_parent NOLOGIN NOINHERIT;
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='gds_identity_third_party') THEN
                CREATE ROLE gds_identity_third_party NOLOGIN NOINHERIT;
            END IF;
        END;
        $roles$;
        ALTER ROLE gds_identity_admin LOGIN INHERIT CREATEDB CREATEROLE REPLICATION BYPASSRLS;
        GRANT gds_identity_drift_parent TO gds_identity_admin;

        GRANT CREATE ON SCHEMA app TO gds_identity_admin;
        GRANT DELETE ON public."AspNetUsers" TO gds_identity_admin;
        GRANT USAGE, SELECT, UPDATE ON SEQUENCE public."AspNetUserClaims_Id_seq" TO gds_identity_admin;
        GRANT EXECUTE ON FUNCTION app.reject_immutable_change() TO gds_identity_admin;

        GRANT CREATE ON SCHEMA public TO gds_identity_drift_parent;
        GRANT UPDATE ("DisplayName") ON public."AspNetUsers" TO gds_identity_drift_parent;
        GRANT EXECUTE ON FUNCTION app.reject_immutable_change() TO gds_identity_drift_parent;

        GRANT USAGE ON SCHEMA public, app TO gds_identity_third_party;
        GRANT SELECT ("Id", "UserName") ON public."AspNetUsers" TO gds_identity_third_party;
        GRANT SELECT ON app.catalog_options TO gds_identity_third_party;
        GRANT EXECUTE ON FUNCTION app.reject_immutable_change() TO gds_identity_third_party;
        """,
        step="la inyección de deriva administrativa controlada",
    )


def _read_committed_last_admin_race(
    container: DisposablePostgres, database: str, first_admin: str, second_admin: str,
    *, session_factory=PostgresRaceSession,
):
    """Release mutations only after two PostgreSQL transactions report ready."""
    sessions = (
        session_factory(container, database, first_admin, second_admin),
        session_factory(container, database, second_admin, first_admin),
    )
    results: tuple[subprocess.CompletedProcess[str], ...]
    try:
        # Each start waits for a sentinel emitted from inside an already-open
        # transaction after BEGIN and SET LOCAL have both completed.
        for session in sessions:
            session.start()
        for session in sessions:
            session.release_mutation()
        with ThreadPoolExecutor(max_workers=2) as executor:
            futures = (
                executor.submit(sessions[0].wait_result),
                executor.submit(sessions[1].wait_result),
            )
            results = tuple(future.result(timeout=40) for future in futures)
    finally:
        for session in sessions:
            session.cleanup()
    successes = [result for result in results if result.returncode == 0]
    failures = [result for result in results if result.returncode != 0]
    if len(successes) != 1 or len(failures) != 1:
        raise RunnerError("La carrera del último administrador no produjo un único commit.")
    if not any(
        marker in failures[0].stderr
        for marker in ("identity_actor_required", "identity_last_active_admin")
    ):
        raise RunnerError("La carrera fue rechazada por una causa distinta a la guarda administrativa.")
    active = container.psql(
        database,
        """
        SELECT count(*) FROM public."AspNetUsers" u
        JOIN public."AspNetUserRoles" ur ON ur."UserId"=u."Id"
        JOIN public."AspNetRoles" r ON r."Id"=ur."RoleId"
        WHERE u."IsActive" AND r."Name"='administrador'
          AND r."NormalizedName"='ADMINISTRADOR';
        """,
        step="el estado final de la carrera del último administrador",
    )
    if active != "1":
        raise RunnerError("La carrera no conservó exactamente un administrador activo.")


def _assert_acl(container: DisposablePostgres, database: str):
    checks = """
    SELECT count(*) FROM (
        SELECT 1 FROM pg_catalog.pg_roles r
        WHERE r.rolname='gds_identity_admin'
          AND (r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolcreatedb
               OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_auth_members m
        JOIN pg_catalog.pg_roles r ON r.oid=m.member WHERE r.rolname='gds_identity_admin'
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        JOIN pg_catalog.pg_roles r ON r.oid=c.relowner
        WHERE n.nspname IN ('public','app') AND r.rolname='gds_identity_admin'
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname IN ('public','app')
          AND CASE
              WHEN c.relkind='S' THEN
                  pg_catalog.has_sequence_privilege(
                      'gds_identity_admin',c.oid,'USAGE,SELECT,UPDATE')
              ELSE FALSE
          END
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_depend d ON d.classid='pg_catalog.pg_proc'::regclass
          AND d.objid=p.oid AND d.deptype='e'
        WHERE p.pronamespace <> 'pg_catalog'::regnamespace
          AND p.oid <> 'public.gen_random_uuid()'::regprocedure
          AND EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
                      WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
    ) drift;
    """
    _assert_zero(container, database, checks, "la reconciliación final de privilegios")
    positive = container.psql(
        database,
        """
        SELECT (pg_catalog.has_schema_privilege('gds_identity_admin','public','USAGE')
            AND pg_catalog.has_schema_privilege('gds_identity_admin','app','USAGE')
            AND NOT pg_catalog.has_schema_privilege('gds_identity_admin','app','CREATE')
            AND pg_catalog.has_column_privilege('gds_identity_admin','public.\"AspNetUsers\"','Id','SELECT')
            AND pg_catalog.has_column_privilege('gds_identity_admin','public.\"AspNetUsers\"','PasswordHash','UPDATE')
            AND pg_catalog.has_table_privilege('gds_identity_admin','public.\"AspNetUserRoles\"','DELETE')
            AND pg_catalog.has_function_privilege('gds_identity_admin','public.gen_random_uuid()','EXECUTE'))::int;
        """,
        step="la allowlist positiva",
    )
    if positive != "1":
        raise RunnerError("La allowlist positiva no coincide con el contrato.")


def _run_identity_harness(
    container: DisposablePostgres, database: str, scenario: str, *, include_admin: bool,
):
    connection = (
        f"Host=127.0.0.1;Port={container.host_port};Database={database};"
        f"Username=gds_identity_runner_login;Password={container.password};SSL Mode=Disable"
    )
    environment = _safe_environment(os.environ)
    environment["GDS_TEST_PRIMARY_CONNECTION"] = connection
    if include_admin:
        environment["GDS_TEST_IDENTITY_ADMIN_CONNECTION"] = connection
    environment["GDS_TEST_CONTROL_CONNECTION"] = (
        f"Host=127.0.0.1;Port={container.host_port};Database={database};"
        f"Username={container.superuser};Password={container.password};SSL Mode=Disable"
    )
    try:
        result = subprocess.run(
            ["dotnet", "run", "--project",
             str(ROOT / "backend-tests/UserAdministrationHarness/UserAdministrationHarness.csproj"),
             "--", scenario],
            cwd=ROOT, env=environment, capture_output=True, text=True, timeout=90, check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RunnerError("Falló el harness IdentityAdmin; se suprimieron detalles.") from error
    if result.returncode != 0:
        raise RunnerError("Falló el harness IdentityAdmin; se suprimieron detalles.")


def test_idempotent_user_creation_receipts_and_tombstones(
    container: DisposablePostgres, database: str,
):
    """Exercise the real IdentityAdmin store against only runner-owned PostgreSQL."""
    _run_identity_harness(
        container, database, "identity-admin-real-create-receipts", include_admin=True,
    )


def test_role_status_csrf_returns_exact_code(container: DisposablePostgres, database: str):
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


def test_management_version_rejects_json_numbers_and_preserves_over_2pow53(
    container: DisposablePostgres, database: str,
):
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


def test_role_status_return_stable_errors_for_missing_stale_self_and_last_admin(
    container: DisposablePostgres, database: str,
):
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


def test_noop_runs_guards_without_version_or_success_audit(
    container: DisposablePostgres, database: str,
):
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


def test_admin_mutation_quota_survives_stale_conflict(
    container: DisposablePostgres, database: str,
):
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


def test_mutation_rejections_are_best_effort_audited_without_changing_errors(
    container: DisposablePostgres, database: str,
):
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


def test_same_version_has_single_writer_and_rotates_both_stamps(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "identity-admin-real-role-status", include_admin=True)


_TASK13_REAL_SCENARIOS: set[tuple[str, str]] = set()


def _prepare_task13_runtime_login(container: DisposablePostgres, database: str):
    container.psql(database, f"""
        DO $task13_runtime$
        DECLARE parent_name name;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='gds_task13_runtime_login') THEN
                CREATE ROLE gds_task13_runtime_login LOGIN INHERIT NOSUPERUSER
                    NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
                    CONNECTION LIMIT -1 PASSWORD '{container.password}' VALID UNTIL 'infinity';
            ELSE
                ALTER ROLE gds_task13_runtime_login LOGIN INHERIT NOSUPERUSER
                    NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
                    CONNECTION LIMIT -1 PASSWORD '{container.password}' VALID UNTIL 'infinity';
            END IF;
            FOR parent_name IN
                SELECT parent.rolname
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles parent ON parent.oid=membership.roleid
                JOIN pg_catalog.pg_roles member ON member.oid=membership.member
                WHERE member.rolname='gds_task13_runtime_login'
                  AND parent.rolname <> 'gds_app_runtime'
            LOOP
                EXECUTE pg_catalog.format('REVOKE %I FROM gds_task13_runtime_login CASCADE', parent_name);
            END LOOP;
        END;
        $task13_runtime$;
        ALTER ROLE gds_task13_runtime_login RESET ALL;
        GRANT gds_app_runtime TO gds_task13_runtime_login;
    """, step="la cuenta runtime desechable del escenario HTTP Task 13")


def _run_task13_real_http_scenario(container: DisposablePostgres, database: str):
    key = (container.container_id, database)
    if key in _TASK13_REAL_SCENARIOS:
        return
    _prepare_identity_harness_login(container, database)
    _prepare_task13_runtime_login(container, database)
    runtime = (
        f"Host=127.0.0.1;Port={container.host_port};Database={database};"
        f"Username=gds_task13_runtime_login;Password={container.password};SSL Mode=Disable;"
        "Application Name=gds-task13-runtime"
    )
    identity_admin = (
        f"Host=127.0.0.1;Port={container.host_port};Database={database};"
        f"Username=gds_identity_runner_login;Password={container.password};SSL Mode=Disable;"
        "Application Name=gds-task13-admin"
    )
    control = (
        f"Host=127.0.0.1;Port={container.host_port};Database={database};"
        f"Username={container.superuser};Password={container.password};SSL Mode=Disable;"
        "Application Name=gds-task13-control"
    )
    environment = _safe_environment(os.environ)
    environment["GDS_TEST_PRIMARY_CONNECTION"] = runtime
    environment["GDS_TEST_IDENTITY_ADMIN_CONNECTION"] = identity_admin
    environment["GDS_TEST_CONTROL_CONNECTION"] = control
    try:
        with tempfile.TemporaryDirectory(prefix="gds-task13-session-") as directory:
            result = subprocess.run(
                ["dotnet", "run", "--project",
                 str(ROOT / "backend-tests/SessionHarness/SessionHarness.csproj"),
                 "--", "change-password-real-postgres", directory],
                cwd=ROOT, env=environment, capture_output=True, text=True,
                timeout=180, check=False,
            )
        if result.returncode != 0 or "PASS change-password-real-postgres" not in result.stdout:
            raise RunnerError("Falló el escenario HTTP PostgreSQL Task 13; se suprimieron detalles.")
        _TASK13_REAL_SCENARIOS.add(key)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RunnerError("Falló el escenario HTTP PostgreSQL Task 13; se suprimieron detalles.") from error
    finally:
        container.psql(database, """
            SELECT pg_catalog.pg_terminate_backend(pid)::int
            FROM pg_catalog.pg_stat_activity
            WHERE usename='gds_task13_runtime_login' AND pid<>pg_backend_pid();
            REVOKE gds_app_runtime FROM gds_task13_runtime_login;
            DROP ROLE IF EXISTS gds_task13_runtime_login;
        """, step="la limpieza de la cuenta runtime desechable Task 13")


def test_concurrent_password_changes_and_disable_have_single_winner(
    container: DisposablePostgres, database: str,
):
    """Real registered HTTP/store path with observable PostgreSQL barriers."""
    _run_task13_real_http_scenario(container, database)


def test_change_password_rejections_are_best_effort_audited(
    container: DisposablePostgres, database: str,
):
    """Same real scenario owns quota, rollback, audit and sentinel inspection."""
    _run_task13_real_http_scenario(container, database)


def _prepare_identity_harness_login(container: DisposablePostgres, database: str):
    container.psql(
        database,
        f"""
        DO $runner_login$
        DECLARE
            parent_name name;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='gds_identity_runner_login') THEN
                CREATE ROLE gds_identity_runner_login LOGIN NOINHERIT NOSUPERUSER
                    NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
                    CONNECTION LIMIT -1 PASSWORD '{container.password}' VALID UNTIL 'infinity';
            ELSE
                ALTER ROLE gds_identity_runner_login LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1
                    PASSWORD '{container.password}' VALID UNTIL 'infinity';
            END IF;

            FOR parent_name IN
                SELECT parent.rolname
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles parent ON parent.oid=membership.roleid
                JOIN pg_catalog.pg_roles member ON member.oid=membership.member
                WHERE member.rolname='gds_identity_runner_login'
                  AND parent.rolname <> 'gds_identity_admin'
            LOOP
                EXECUTE pg_catalog.format(
                    'REVOKE %I FROM gds_identity_runner_login CASCADE', parent_name
                );
            END LOOP;
        END;
        $runner_login$;
        ALTER ROLE gds_identity_runner_login RESET ALL;
        REVOKE gds_identity_admin FROM gds_identity_runner_login CASCADE;
        GRANT gds_identity_admin TO gds_identity_runner_login WITH ADMIN FALSE, INHERIT FALSE, SET TRUE;
        """,
        step="la cuenta LOGIN desechable del harness IdentityAdmin",
    )


def test_identity_admin_sets_and_verifies_current_role_on_every_checkout(
    container: DisposablePostgres, database: str,
):
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "identity-admin-real-current-role", include_admin=True)


def test_admin_directory_uses_identity_admin_and_records_sanitized_audit(
    container: DisposablePostgres, database: str,
):
    admin_id = _uuid(1)
    percent_id = _uuid(11)
    underscore_id = _uuid(12)
    slash_id = _uuid(13)
    diacritic_id = _uuid(14)
    tie_first_id = _uuid(15)
    tie_second_id = _uuid(16)
    container.psql(
        database,
        f"""
        BEGIN;
        SET LOCAL app.actor_user_id='{admin_id}';
        {_user_insert(percent_id, 'alpha.percent%', upgraded=True)};
        UPDATE public."AspNetUsers"
        SET "ManagementVersion"=9007199254740993
        WHERE "Id"='{percent_id}';
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{percent_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='operador';
        {_user_insert(underscore_id, 'bravo_under_', upgraded=True)};
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{underscore_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='operador';
        {_user_insert(slash_id, r'charlie\slash', upgraded=True)};
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{slash_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='operador';
        {_user_insert(diacritic_id, 'Águila', upgraded=True)};
        UPDATE public."AspNetUsers" SET "NormalizedUserName"='DELTA.DIACRITIC'
        WHERE "Id"='{diacritic_id}';
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{diacritic_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='validador';
        {_user_insert(tie_second_id, 'tie.second', upgraded=True)};
        UPDATE public."AspNetUsers" SET "NormalizedUserName"=NULL WHERE "Id"='{tie_second_id}';
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{tie_second_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='operador';
        {_user_insert(tie_first_id, 'tie.first', upgraded=True)};
        UPDATE public."AspNetUsers" SET "NormalizedUserName"=NULL WHERE "Id"='{tie_first_id}';
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{tie_first_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='operador';
        COMMIT;
        """,
        step="los usuarios desechables del directorio administrativo",
    )
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "identity-admin-real-directory", include_admin=True)


def test_identity_admin_has_no_primary_fallback(container: DisposablePostgres, database: str):
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "identity-admin-real-no-primary-fallback", include_admin=False)


def test_real_readiness_fails_for_missing_retained_key(
    container: DisposablePostgres, database: str,
):
    _prepare_identity_harness_login(container, database)
    container.psql(
        database,
        "INSERT INTO app.identity_rate_limit_key_state (key_id, state, changed_at) VALUES ('rate-limit-v0', 'retained', now());",
        step="la clave retained sin secreto local",
    )
    try:
        _run_identity_harness(container, database, "identity-admin-real-missing-retained", include_admin=True)
    finally:
        container.psql(
            database,
            "DELETE FROM app.identity_rate_limit_key_state WHERE key_id='rate-limit-v0';",
            step="la limpieza de la clave retained del harness",
        )


def test_readiness_check_async_uses_injected_reader(
    container: DisposablePostgres, database: str,
):
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "identity-admin-real-injected-reader", include_admin=True)


def _reset_rate_limit_fixture(container: DisposablePostgres, database: str):
    container.psql(
        database,
        """
        BEGIN;
        DELETE FROM app.identity_rate_limits;
        INSERT INTO app.identity_rate_limit_key_state (key_id, state, changed_at)
        VALUES ('rate-limit-v1', 'accepted', now())
        ON CONFLICT (key_id) DO UPDATE SET state='accepted', changed_at=EXCLUDED.changed_at;
        DELETE FROM app.identity_rate_limit_key_state WHERE key_id <> 'rate-limit-v1';
        UPDATE app.identity_rate_limit_key_state
        SET state='active', changed_at=now() WHERE key_id='rate-limit-v1';
        COMMIT;
        """,
        step="la preparación aislada del rate limit distribuido",
    )


def test_rate_limit_lock_delta_reapplies_and_rejects_untrusted_grants(
    container: DisposablePostgres, database: str,
):
    delta = (ROOT / 'docs/runbooks/identity-rate-limit-lock-fix.sql').read_text()
    container.psql(database, delta, step="la primera aplicación del delta de bloqueo")
    container.psql(database, delta, step="la segunda aplicación idempotente del delta de bloqueo")
    preflight = delta[delta.index('DO $identity_lock_preflight$'):delta.index('$identity_lock_preflight$;', delta.index('DO $identity_lock_preflight$') + 26) + len('$identity_lock_preflight$;')]
    scenarios = (
        "GRANT EXECUTE ON FUNCTION app.lock_identity_rate_limit_key_state() TO PUBLIC;",
        "CREATE ROLE identity_lock_untrusted NOLOGIN; GRANT EXECUTE ON FUNCTION app.lock_identity_rate_limit_key_state() TO identity_lock_untrusted;",
        "CREATE ROLE identity_lock_untrusted NOLOGIN; GRANT CREATE ON SCHEMA app TO identity_lock_untrusted; ALTER FUNCTION app.lock_identity_rate_limit_key_state() OWNER TO identity_lock_untrusted;",
    )
    for mutation in scenarios:
        result = container.psql_result(database, 'BEGIN; ' + mutation + preflight,
                                       step="rechazar propietario o ACL no confiables")
        if result.returncode == 0 or 'identity_lock_existing_owner_or_acl_untrusted' not in result.stderr:
            raise RunnerError("La validación no rechazó el propietario o ACL no confiables.")


def test_rate_limit_key_lock_is_usable_without_write_privileges(
    container: DisposablePostgres, database: str,
):
    container.psql(
        database,
        """
        BEGIN;
        SET LOCAL ROLE gds_identity_admin;
        SELECT * FROM app.lock_identity_rate_limit_key_state();
        DO $test$
        BEGIN
            BEGIN
                UPDATE app.identity_rate_limit_key_state SET changed_at = now();
                RAISE EXCEPTION 'unexpected direct write access';
            EXCEPTION WHEN insufficient_privilege THEN NULL;
            END;
        END
        $test$;
        ROLLBACK;
        """,
        step="el bloqueo de claves sin permiso directo de escritura",
    )
    _assert_zero(container, database, """
        SELECT count(*) FROM pg_catalog.pg_proc p
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
            p.proacl, pg_catalog.acldefault('f', p.proowner))) a
        WHERE p.oid = 'app.lock_identity_rate_limit_key_state()'::pg_catalog.regprocedure
          AND a.grantee = 0 AND a.privilege_type = 'EXECUTE';
    """, "la función de bloqueo no es ejecutable por PUBLIC")


def test_rate_limit_attempt_commits_before_service_returns(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "rate-limit-attempt-commits-before-service-returns", include_admin=True)


def test_429_attempt_commits_before_service_returns(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "rate-limit-429-attempt-commits-before-service-returns", include_admin=True)


def test_rate_limit_attempt_survives_synthetic_later_rollback(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "rate-limit-attempt-survives-synthetic-later-rollback", include_admin=True)


def test_rate_limit_database_failure_returns_service_unavailable(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    _run_identity_harness(container, database, "rate-limit-database-failure-returns-service-unavailable", include_admin=False)


def test_expired_buckets_are_deleted_in_bounded_batches(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    container.psql(
        database,
        """
        INSERT INTO app.identity_rate_limits
            (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at)
        SELECT 'rate-limit-v1', pg_catalog.decode(pg_catalog.lpad(value::text, 64, '0'), 'hex'),
               now() - interval '2 days', now() - interval '1 day 1 hour', 1, now() - interval '1 hour'
        FROM pg_catalog.generate_series(1, 3) value;
        """,
        step="los buckets vencidos para limpieza acotada",
    )
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "expired-rate-limit-buckets-are-deleted-in-bounded-batches", include_admin=True)


def test_rate_limit_rotation_is_shared_ordered_and_fails_closed(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(container, database, "rate-limit-rotation-is-shared-ordered-and-fails-closed", include_admin=True)


def test_rate_limit_cleanup_state_transition_order_does_not_deadlock(
    container: DisposablePostgres, database: str,
):
    _reset_rate_limit_fixture(container, database)
    container.psql(
        database,
        """
        BEGIN;
        INSERT INTO app.identity_rate_limit_key_state (key_id, state, changed_at)
        VALUES ('rate-limit-v0', 'retired', now()), ('rate-limit-v2', 'accepted', now());
        INSERT INTO app.identity_rate_limits
            (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at)
        VALUES
            ('rate-limit-v0', pg_catalog.decode(pg_catalog.repeat('03', 32), 'hex'),
             now() - interval '2 days', now() - interval '1 day 1 hour', 1, now() - interval '1 hour'),
            ('rate-limit-v2', pg_catalog.decode(pg_catalog.repeat('04', 32), 'hex'),
             now() - interval '2 days', now() - interval '1 day 1 hour', 1, now() - interval '1 hour');
        COMMIT;
        """,
        step="los buckets vencidos accepted/retired para el orden de transición",
    )
    _prepare_identity_harness_login(container, database)
    _run_identity_harness(
        container, database,
        "rate-limit-cleanup-state-transition-order-does-not-deadlock",
        include_admin=True,
    )


def _negative_grants(container: DisposablePostgres, database: str):
    attempts = (
        'SET ROLE gds_identity_admin; CREATE TABLE app.forbidden_identity_table(id integer);',
        'SET ROLE gds_identity_admin; UPDATE public."AspNetUsers" SET "DisplayName"="DisplayName";',
        'SET ROLE gds_identity_admin; DELETE FROM public."AspNetUsers";',
        'SET ROLE gds_identity_admin; SELECT nextval(\'public."AspNetUserClaims_Id_seq"\');',
        'SET ROLE gds_identity_admin; SELECT app.guard_identity_admin_mutation();',
    )
    for number, sql in enumerate(attempts, 1):
        container.psql(
            database, sql, expected_success=False,
            step=f"la denegación administrativa {number}",
        )


def _fresh_scenario(container: DisposablePostgres, scenario: Scenario):
    container.apply_file(scenario.database, APPLY, "apply.sql en instalación limpia")
    _assert_zero(container, scenario.database, 'SELECT count(*) FROM public."AspNetUsers";', "fresh inicia sin usuarios")
    admin_id = _uuid(1)
    container.psql(
        scenario.database,
        f"""
        BEGIN;
        SET LOCAL app.bootstrap_mode='on';
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        SELECT '{admin_id}', "Id" FROM public."AspNetRoles" WHERE "Name"='administrador';
        {_user_insert(admin_id, 'fresh.admin', upgraded=True)};
        COMMIT;
        """,
        step="el bootstrap membership-before-user",
    )
    test_identity_admin_sets_and_verifies_current_role_on_every_checkout(container, scenario.database)
    test_admin_directory_uses_identity_admin_and_records_sanitized_audit(container, scenario.database)
    test_identity_admin_has_no_primary_fallback(container, scenario.database)
    test_real_readiness_fails_for_missing_retained_key(container, scenario.database)
    test_readiness_check_async_uses_injected_reader(container, scenario.database)
    test_rate_limit_lock_delta_reapplies_and_rejects_untrusted_grants(container, scenario.database)
    test_rate_limit_key_lock_is_usable_without_write_privileges(container, scenario.database)
    test_rate_limit_attempt_commits_before_service_returns(container, scenario.database)
    test_429_attempt_commits_before_service_returns(container, scenario.database)
    test_rate_limit_attempt_survives_synthetic_later_rollback(container, scenario.database)
    test_rate_limit_database_failure_returns_service_unavailable(container, scenario.database)
    test_expired_buckets_are_deleted_in_bounded_batches(container, scenario.database)
    test_rate_limit_rotation_is_shared_ordered_and_fails_closed(container, scenario.database)
    test_rate_limit_cleanup_state_transition_order_does_not_deadlock(container, scenario.database)
    test_idempotent_user_creation_receipts_and_tombstones(container, scenario.database)
    # One real harness scenario owns the complete Task 12 matrix. The seven
    # named runner entry points above remain independently discoverable while
    # this execution avoids seven destructive repetitions against the fixture.
    test_same_version_has_single_writer_and_rotates_both_stamps(container, scenario.database)
    test_concurrent_password_changes_and_disable_have_single_winner(container, scenario.database)
    test_change_password_rejections_are_best_effort_audited(container, scenario.database)
    _assert_acl(container, scenario.database)
    _negative_grants(container, scenario.database)


def _empty_upgrade_scenario(container: DisposablePostgres, scenario: Scenario):
    container.apply_file(scenario.database, FIXTURE, "el fixture vacío pre-administración")
    _assert_zero(
        container, scenario.database, 'SELECT count(*) FROM public."AspNetUsers";',
        "el upgrade vacío inicia sin usuarios",
    )
    for pass_number, path in enumerate(scenario.scripts[1:], 1):
        container.apply_file(
            scenario.database, path, f"delta sobre upgrade vacío, pasada {pass_number}",
        )
    _assert_zero(
        container, scenario.database, 'SELECT count(*) FROM public."AspNetUsers";',
        "el delta vacío no crea usuarios",
    )
    role_count = container.psql(
        scenario.database, 'SELECT count(*) FROM public."AspNetRoles";',
        step="los roles canónicos del upgrade vacío",
    )
    if role_count != "3":
        raise RunnerError("El upgrade vacío no instaló exactamente tres roles canónicos.")
    _assert_acl(container, scenario.database)


def _upgrade_scenario(container: DisposablePostgres, scenario: Scenario):
    container.apply_file(scenario.database, FIXTURE, "el fixture pre-administración")
    role_ids = {
        "operador": _uuid(101), "validador": _uuid(102), "administrador": _uuid(103),
    }
    for name, identifier in role_ids.items():
        container.psql(
            scenario.database,
            f'''UPDATE public."AspNetRoles" SET "Id"='{identifier}' WHERE "Name"='{name}';''',
            step="la preparación de UUIDs Identity arbitrarios",
        )
    admin_id = _uuid(2)
    container.psql(
        scenario.database,
        f"""
        {_user_insert(admin_id, 'upgrade.admin', upgraded=False)};
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId") VALUES ('{admin_id}','{role_ids['administrador']}');
        """,
        step="el administrador activo de precondición",
    )
    _inject_upgrade_privilege_drift(container, scenario.database)
    runtime_before = role_privilege_snapshot(
        container, scenario.database, "gds_app_runtime",
    )
    third_party_before = role_privilege_snapshot(
        container, scenario.database, "gds_identity_third_party",
    )
    for pass_number, path in enumerate(scenario.scripts[1:], 1):
        container.apply_file(scenario.database, path, f"delta de upgrade, pasada {pass_number}")
    runtime_after = role_privilege_snapshot(
        container, scenario.database, "gds_app_runtime",
    )
    third_party_after = role_privilege_snapshot(
        container, scenario.database, "gds_identity_third_party",
    )
    if runtime_after != runtime_before:
        raise RunnerError("El delta alteró privilegios o membresías de gds_app_runtime.")
    if third_party_after != third_party_before:
        raise RunnerError("El delta alteró privilegios o membresías del rol de tercero.")
    preserved = container.psql(
        scenario.database,
        '''SELECT "Id" FROM public."AspNetRoles" WHERE "Name"='administrador';''',
        step="la preservación del UUID de rol instalado",
    )
    if preserved != role_ids["administrador"]:
        raise RunnerError("El delta sustituyó un UUID Identity instalado.")

    # A semantic swap is rejected even if its final three names look canonical.
    container.psql(
        scenario.database,
        """
        BEGIN;
        UPDATE public."AspNetRoles" SET "Name"='temporal',"NormalizedName"='TEMPORAL' WHERE "Name"='administrador';
        UPDATE public."AspNetRoles" SET "Name"='administrador',"NormalizedName"='ADMINISTRADOR' WHERE "Name"='operador';
        UPDATE public."AspNetRoles" SET "Name"='operador',"NormalizedName"='OPERADOR' WHERE "Name"='temporal';
        COMMIT;
        """,
        expected_success=False, step="el rechazo del intercambio semántico de roles",
    )
    names = container.psql(
        scenario.database,
        """SELECT pg_catalog.string_agg("Name",',' ORDER BY "Name") FROM public."AspNetRoles";""",
        step="la restauración tras intercambio rechazado",
    )
    if names != "administrador,operador,validador":
        raise RunnerError("El intercambio de roles no se revirtió de forma atómica.")
    container.psql(
        scenario.database,
        """UPDATE public."AspNetRoles" SET "ConcurrencyStamp"='stamp-ok' WHERE "Name"='operador';""",
        step="la actualización permitida de ConcurrencyStamp",
    )

    target_id = _uuid(3)
    container.psql(
        scenario.database,
        f"""
        BEGIN;
        SET LOCAL app.actor_user_id='{admin_id}';
        {_user_insert(target_id, 'upgrade.operator', upgraded=True)};
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId") VALUES ('{target_id}','{role_ids['operador']}');
        COMMIT;
        """,
        step="los triggers diferidos user-before-membership",
    )
    # Guard references remain schema-qualified even with pg_temp decoys first.
    container.psql(
        scenario.database,
        f"""
        CREATE TEMP TABLE "AspNetUsers" (LIKE public."AspNetUsers");
        CREATE TEMP TABLE "AspNetUserRoles" (LIKE public."AspNetUserRoles");
        SET search_path=pg_temp,public,app,pg_catalog;
        BEGIN;
        SET LOCAL app.actor_user_id='{admin_id}';
        UPDATE public."AspNetUsers" SET "IsActive"=false WHERE "Id"='{target_id}';
        COMMIT;
        """,
        step="la resistencia a señuelos pg_temp",
    )
    # Both stronger isolation levels fail closed before an administrative write.
    for isolation in ("REPEATABLE READ", "SERIALIZABLE"):
        container.psql(
            scenario.database,
            f"""
            BEGIN ISOLATION LEVEL {isolation};
            SET LOCAL app.actor_user_id='{admin_id}';
            UPDATE public."AspNetUsers" SET "IsActive"=true WHERE "Id"='{target_id}';
            COMMIT;
            """,
            expected_success=False,
            step=f"el rechazo de ISOLATION LEVEL {isolation}",
        )
    # A failed pre-COMMIT reconciliation must roll back the drift.
    container.psql(
        scenario.database,
        """
        BEGIN;
        GRANT CREATE ON SCHEMA app TO gds_identity_admin;
        DO $rollback$ BEGIN RAISE EXCEPTION 'forced rollback'; END $rollback$;
        COMMIT;
        """,
        expected_success=False, step="el rollback pre-COMMIT",
    )
    _assert_zero(
        container, scenario.database,
        "SELECT pg_catalog.has_schema_privilege('gds_identity_admin','app','CREATE')::int;",
        "el rollback de privilegios",
    )

    race_admin_id = _uuid(4)
    container.psql(
        scenario.database,
        f"""
        BEGIN ISOLATION LEVEL READ COMMITTED;
        SET LOCAL app.actor_user_id='{admin_id}';
        {_user_insert(race_admin_id, 'upgrade.race.admin', upgraded=True)};
        INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
        VALUES ('{race_admin_id}','{role_ids['administrador']}');
        COMMIT;
        """,
        step="la preparación de dos administradores para la carrera",
    )
    _read_committed_last_admin_race(
        container, scenario.database, admin_id, race_admin_id,
    )
    _assert_acl(container, scenario.database)
    _negative_grants(container, scenario.database)


def run_scenarios(container: DisposablePostgres):
    container.create_databases()
    plan = scenario_plan()
    _fresh_scenario(container, plan["fresh"])
    _empty_upgrade_scenario(container, plan["empty_upgrade"])
    _upgrade_scenario(container, plan["upgrade"])


def with_disposable_container(container: DisposablePostgres, scenario: Callable[[DisposablePostgres], None]):
    try:
        container.start()
        scenario(container)
    finally:
        container.cleanup()


def main(arguments: list[str] | None = None) -> int:
    try:
        validate_cli_arguments(arguments or sys.argv)
        docker = preflight(os.environ)
        with_disposable_container(DisposablePostgres(docker), run_scenarios)
    except RunnerError as error:
        print(f"user-administration PostgreSQL: {error}", file=sys.stderr)
        return 1
    print("user-administration PostgreSQL: fresh, upgrade vacío y upgrade verificados; cleanup confirmado")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
