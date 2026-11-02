#!/usr/bin/env python3
"""Objective 4 integration runner over a runner-owned disposable PostgreSQL.

The command deliberately accepts no DSN and never loads an env file. It creates
and removes its own Docker container, applies the repository DDL twice, and then
tests the real API with PostgreSQL-backed Identity, catalogs, and incidents.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from copy import copy
from pathlib import Path
from typing import Callable, Mapping
import http.cookiejar
import json
import os
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
DDL = ROOT / "backend" / "Database" / "apply.sql"
BACKEND = ROOT / "backend" / "Notes.Api.csproj"
POSTGRES_IMAGE = (
    "postgres:17.6-alpine@sha256:"
    "ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94"
)
FORBIDDEN_CONNECTION_KEYS = frozenset({
    "POSTGRES_DEPLOY_URL",
    "ConnectionStrings__Postgres",
    "ConnectionStrings__Catalogs",
})
SAFE_INHERITED_ENV = frozenset({
    "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "TEMP", "TMP",
    "DOTNET_ROOT", "NUGET_PACKAGES", "LANG", "LC_ALL",
})


class RunnerError(RuntimeError):
    """Expected, already-sanitized runner failure."""


class ContainerIdentityMismatch(RunnerError):
    """A name or ID resolved to a container outside this runner's ownership proof."""


def validate_cli_arguments(arguments: list[str]):
    if len(arguments) != 1:
        raise RunnerError("Este runner no acepta argumentos ni conexiones externas.")


@contextmanager
def docker_secret_file(path: Path, values: Mapping[str, str]):
    for key, value in values.items():
        if not key.replace("_", "").isalnum() or "\n" in value or "\r" in value:
            raise RunnerError("No se pudo preparar el entorno privado del contenedor.")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            for key, value in values.items():
                stream.write(f"{key}={value}\n")
        yield path
    finally:
        path.unlink(missing_ok=True)


def container_identity_matches(expected_id: str, run_id: str, inspect_output: str) -> bool:
    parts = inspect_output.strip().split()
    return len(parts) == 2 and parts[0] == expected_id and parts[1] == run_id


def created_container_id(stdout: str, cidfile: str) -> str:
    candidates = {value.strip() for value in (stdout, cidfile) if value.strip()}
    if len(candidates) != 1:
        raise RunnerError("Docker no confirmó una identidad única para el contenedor desechable.")
    identifier = candidates.pop()
    if re.fullmatch(r"[0-9a-f]{64}", identifier) is None:
        raise RunnerError("Docker devolvió una identidad de contenedor inválida.")
    return identifier


def response_evidence(body: bytes, headers: Mapping[str, str]) -> bytes:
    serialized_headers = "\n".join(f"{name}: {value}" for name, value in headers.items())
    return body + b"\n" + serialized_headers.encode("utf-8", errors="replace")


def ensure_secrets_absent(evidence: bytes, secret_values: list[bytes]):
    if any(value and value in evidence for value in secret_values):
        raise RunnerError("Las respuestas, cabeceras o logs contienen material secreto.")


def preflight(environment: Mapping[str, str], find_executable: Callable[[str], str | None] = shutil.which) -> str:
    forbidden = {key.casefold() for key in FORBIDDEN_CONNECTION_KEYS}
    if any(key.casefold() in forbidden for key in environment):
        raise RunnerError(
            "El runner rechazó conexiones externas: elimine variables PostgreSQL/DSN del entorno de esta ejecución."
        )
    docker = find_executable("docker")
    if not docker:
        raise RunnerError(
            "Docker no está disponible; no se creó ni modificó ninguna base de datos. "
            "Instale/inicie Docker y vuelva a ejecutar npm run test:postgres:objective4."
        )
    if not DDL.is_file():
        raise RunnerError("No se encontró backend/Database/apply.sql; no se inició la prueba.")
    return docker


def _base_environment(inherited: Mapping[str, str]) -> dict[str, str]:
    result = {key: value for key, value in inherited.items() if key in SAFE_INHERITED_ENV}
    result.update({
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
    })
    return result


def runtime_environment(
    inherited: Mapping[str, str], connection_string: str, key_ring: Path
) -> dict[str, str]:
    result = _base_environment(inherited)
    result.update({
        "ASPNETCORE_ENVIRONMENT": "Testing",
        "Authentication__EnableTestHeaders": "false",
        "Catalogs__Provider": "PostgreSql",
        "Notes__Provider": "PostgreSql",
        "ConnectionStrings__Postgres": connection_string,
        "ConnectionStrings__Catalogs": connection_string,
        "DataProtection__KeyRingPath": str(key_ring),
    })
    return result


def with_disposable_container(container, scenario: Callable):
    try:
        container.start()
        return scenario(container)
    finally:
        container.cleanup()


def _run(
    args: list[str], *, env: Mapping[str, str] | None = None, input_text: str | None = None,
    timeout: int = 120, step: str
) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(
            args, cwd=ROOT, env=dict(env) if env is not None else _base_environment(os.environ),
            input=input_text, text=True, capture_output=True, timeout=timeout, check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RunnerError(f"Falló {step}; se suprimieron detalles potencialmente sensibles.") from error
    if result.returncode:
        raise RunnerError(f"Falló {step}; se suprimieron detalles potencialmente sensibles.")
    return result


class DisposablePostgres:
    def __init__(self, docker: str):
        suffix = secrets.token_hex(6)
        self.docker = docker
        self.name = f"gds-objective4-{suffix}"
        self.run_id = f"objective4-{secrets.token_hex(12)}"
        self.container_id = ""
        self.superuser = "postgres"
        self.database = "gds_objective4"
        self.superuser_password = "root_" + secrets.token_urlsafe(30)
        self.deployer = "gds_objective4_deployer"
        self.deployer_password = "deploy_" + secrets.token_urlsafe(30)
        self.runtime = "gds_objective4_runtime"
        self.runtime_password = "runtime_" + secrets.token_urlsafe(30)
        self.deployer_connection_string = ""
        self.runtime_connection_string = ""
        self.started = False
        self.cleanup_reference = ""
        self.cleanup_blocked = False

    @property
    def cleanup_pending(self) -> bool:
        return self.started or bool(self.container_id) or bool(self.cleanup_reference)

    def docker_run(self, arguments: list[str], **kwargs) -> subprocess.CompletedProcess[str]:
        return _run([self.docker, *arguments], **kwargs)

    def start(self):
        # Create first so a failed/slow start cannot lose the owned container ID.
        with tempfile.TemporaryDirectory(prefix="gds-objective4-container-") as directory_name:
            directory = Path(directory_name)
            env_path = directory / "postgres.env"
            cid_path = directory / "postgres.cid"
            result = None
            create_error = None
            with docker_secret_file(env_path, {
                "POSTGRES_USER": self.superuser,
                "POSTGRES_PASSWORD": self.superuser_password,
                "POSTGRES_DB": self.database,
            }):
                # Preserve the random name before invoking Docker. If create times
                # out before returning an ID, cleanup can safely retry inspect by
                # this name plus the unguessable run-id label.
                self.cleanup_reference = self.name
                try:
                    result = subprocess.run(
                        [
                            self.docker, "create", "--name", self.name,
                            "--label", f"gds.objective4.run-id={self.run_id}",
                            "--cidfile", str(cid_path), "--env-file", str(env_path),
                            "--publish", "127.0.0.1::5432",
                            "--health-cmd", f"pg_isready -U {self.superuser} -d {self.database}",
                            "--health-interval", "1s", "--health-timeout", "3s", "--health-retries", "30",
                            POSTGRES_IMAGE,
                        ], cwd=ROOT, env=_base_environment(os.environ), capture_output=True,
                        text=True, timeout=120, check=False,
                    )
                except (OSError, subprocess.TimeoutExpired) as error:
                    create_error = error
            cidfile_id = cid_path.read_text(encoding="utf-8").strip() if cid_path.exists() else ""
            stdout_id = result.stdout if result is not None else ""
            creation_failed = create_error is not None or result is None or result.returncode != 0
            recovered_by_name = False
            try:
                self.container_id = created_container_id(stdout_id, cidfile_id)
            except RunnerError:
                try:
                    self.container_id = self._inspect_identity(
                        self.name, "la recuperación segura posterior a docker create"
                    )
                    recovered_by_name = True
                except ContainerIdentityMismatch as recovery_error:
                    self.container_id = ""
                    self.started = False
                    raise RunnerError(
                        "Docker create resolvió una identidad incompatible; se bloqueó cualquier eliminación."
                    ) from recovery_error
                except RunnerError as recovery_error:
                    self.container_id = ""
                    self.started = False
                    raise RunnerError(
                        "Docker create no confirmó aún un contenedor propio; la limpieza por referencia queda pendiente."
                    ) from recovery_error
            self.started = True
            if not recovered_by_name:
                self._validate_owned_identity("la validación posterior a docker create")
            if creation_failed:
                raise RunnerError(
                    "Falló la creación del PostgreSQL desechable; su identidad se conservó para limpieza."
                )
        self.docker_run(
            ["start", self.container_id], timeout=60, step="el inicio del PostgreSQL desechable"
        )
        self._wait_healthy()
        port_result = self.docker_run(
            ["port", self.container_id, "5432/tcp"], timeout=10, step="la lectura del puerto desechable"
        )
        port = port_result.stdout.strip().rsplit(":", 1)[-1]
        if not port.isdigit():
            raise RunnerError("Docker no devolvió un puerto local válido para PostgreSQL.")
        self._bootstrap_logins()
        self.deployer_connection_string = self._connection_string(
            port, self.deployer, self.deployer_password
        )
        self.runtime_connection_string = self._connection_string(
            port, self.runtime, self.runtime_password
        )

    def _connection_string(self, port: str, username: str, password: str) -> str:
        return (
            f"Host=127.0.0.1;Port={port};Database={self.database};Username={username};"
            f"Password={password};SSL Mode=Disable;Timeout=5;Command Timeout=30"
        )

    @staticmethod
    def _literal(value: str) -> str:
        return "'" + value.replace("'", "''") + "'"

    def _bootstrap_logins(self):
        statement = f"""
        CREATE ROLE {self.deployer} LOGIN PASSWORD {self._literal(self.deployer_password)} CREATEROLE;
        CREATE ROLE {self.runtime} LOGIN PASSWORD {self._literal(self.runtime_password)};
        ALTER DATABASE {self.database} OWNER TO {self.deployer};
        """
        self.psql(statement, self.superuser, step="el bootstrap mínimo de roles aislados")

    def _wait_healthy(self):
        for _ in range(60):
            result = subprocess.run(
                [self.docker, "inspect", "--format={{.State.Health.Status}}", self.container_id],
                cwd=ROOT, env=_base_environment(os.environ), capture_output=True, text=True, check=False,
            )
            if result.returncode == 0 and result.stdout.strip() == "healthy":
                return
            time.sleep(1)
        raise RunnerError("PostgreSQL desechable no alcanzó estado saludable.")

    def apply_schema_twice(self):
        sql = DDL.read_text(encoding="utf-8")
        for pass_number in (1, 2):
            self.psql(sql, self.deployer, timeout=180, step=f"apply.sql (pasada {pass_number})")
        self.psql(
            f"""
            GRANT gds_db_deployer TO {self.deployer};
            GRANT gds_app_runtime TO {self.runtime};
            ALTER ROLE {self.deployer} NOCREATEROLE;
            """,
            self.superuser,
            step="la asignación de roles de base aislados",
        )
        self._assert_runtime_grants()

    def psql(
        self, statement: str, role: str, *, expected_success=True, timeout=30,
        step="una operación SQL aislada",
    ) -> str:
        args = [
            self.docker, "exec", "--interactive", self.container_id, "psql", "--no-psqlrc",
            "--set", "ON_ERROR_STOP=1", "--tuples-only", "--no-align",
            "--username", role, "--dbname", self.database,
        ]
        try:
            result = subprocess.run(
                args, cwd=ROOT, env=_base_environment(os.environ), input=statement,
                text=True, capture_output=True, timeout=timeout, check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RunnerError(f"Falló {step}; se suprimieron detalles potencialmente sensibles.") from error
        if expected_success and result.returncode:
            raise RunnerError(f"Falló {step}; se suprimieron detalles potencialmente sensibles.")
        if not expected_success and result.returncode == 0:
            raise RunnerError(f"Falló {step}: el rol runtime recibió privilegios administrativos.")
        return result.stdout.strip()

    def _assert_runtime_grants(self):
        self.psql(
            'SELECT count(*) FROM public."AspNetUsers";', self.runtime,
            step="la lectura runtime autorizada",
        )
        self.psql(
            'UPDATE public."AspNetUsers" SET "IsActive"=false WHERE false;', self.runtime,
            expected_success=False, step="la denegación runtime de IsActive",
        )
        self.psql(
            'INSERT INTO public."AspNetRoles" ("Id","Name","NormalizedName","ConcurrencyStamp") '
            "VALUES (gen_random_uuid(),'forbidden','FORBIDDEN','forbidden');",
            self.runtime, expected_success=False, step="la denegación runtime de roles",
        )

    def scalar(self, statement: str, role: str | None = None) -> int:
        value = self.psql(statement, role or self.deployer, step="una aserción SQL aislada")
        try:
            return int(value)
        except ValueError as error:
            raise RunnerError("Una aserción SQL aislada devolvió un resultado inválido.") from error

    def restart(self):
        self.docker_run(
            ["restart", self.container_id], timeout=60, step="el reinicio del PostgreSQL desechable"
        )
        self._wait_healthy()

    def _validate_owned_identity(self, step: str):
        observed_id = self._inspect_identity(self.container_id, step)
        if observed_id != self.container_id:
            self.cleanup_blocked = True
            raise ContainerIdentityMismatch(
                "La identidad del contenedor no coincide; se rechazó una eliminación insegura."
            )

    def _inspect_identity(self, reference: str, step: str) -> str:
        try:
            inspected = subprocess.run(
                [self.docker, "inspect", "--format={{.Id}} {{index .Config.Labels \"gds.objective4.run-id\"}}", reference],
                cwd=ROOT, env=_base_environment(os.environ), capture_output=True, text=True,
                timeout=30, check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RunnerError(f"Falló {step}; la limpieza seguirá pendiente y reintentable.") from error
        if inspected.returncode != 0:
            raise RunnerError(f"Falló {step}; la limpieza seguirá pendiente y reintentable.")
        parts = inspected.stdout.strip().split()
        if (
            len(parts) != 2
            or re.fullmatch(r"[0-9a-f]{64}", parts[0]) is None
            or parts[1] != self.run_id
        ):
            self.cleanup_blocked = True
            raise ContainerIdentityMismatch(
                "La identidad del contenedor no coincide; se rechazó una eliminación insegura."
            )
        return parts[0]

    def cleanup(self):
        if not self.cleanup_pending:
            return
        if self.cleanup_blocked:
            raise RunnerError(
                "La referencia de limpieza es incompatible; no se intentará eliminar ningún contenedor."
            )
        reference = self.container_id or self.cleanup_reference
        observed_id = self._inspect_identity(reference, "la inspección de limpieza")
        if self.container_id and observed_id != self.container_id:
            self.cleanup_blocked = True
            raise RunnerError(
                "La referencia de limpieza es incompatible; no se intentará eliminar ningún contenedor."
            )
        self.container_id = observed_id
        self.started = True
        try:
            removed = subprocess.run(
                [self.docker, "rm", "--force", self.container_id], cwd=ROOT,
                env=_base_environment(os.environ), capture_output=True, text=True,
                timeout=30, check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RunnerError("Falló la eliminación segura; la limpieza permanece reintentable.") from error
        if removed.returncode != 0:
            raise RunnerError("Falló la eliminación segura; la limpieza permanece reintentable.")
        self.started = False
        self.container_id = ""
        self.cleanup_reference = ""


def _unused_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class ApiProcess:
    def __init__(self, environment: Mapping[str, str], directory: Path):
        self.environment = dict(environment)
        self.directory = directory
        self.port = _unused_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.log_path = directory / "objective4-api.log"
        self.log_file = None
        self.process = None

    def start(self):
        self.log_file = self.log_path.open("a", encoding="utf-8")
        self.process = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", str(BACKEND), "--", "--urls", self.url],
            cwd=ROOT, env=self.environment, stdout=self.log_file, stderr=subprocess.STDOUT, text=True,
        )
        for _ in range(120):
            try:
                with urllib.request.urlopen(self.url + "/api/health", timeout=0.25) as response:
                    if response.status == 200:
                        return
            except (OSError, urllib.error.URLError):
                if self.process.poll() is not None:
                    break
            time.sleep(0.1)
        self.stop()
        raise RunnerError("La API real no inició sobre la base desechable; el log quedó sanitizado del reporte.")

    def stop(self):
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        if self.log_file is not None:
            self.log_file.close()
        self.process = None
        self.log_file = None

    def restart(self):
        self.stop()
        self.port = _unused_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.start()


class HttpSession:
    def __init__(self, base_url: str):
        self.base_url = base_url
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.observed: list[bytes] = []

    def move(self, base_url: str):
        self.base_url = base_url

    def clone(self) -> "HttpSession":
        result = HttpSession(self.base_url)
        for cookie in self.jar:
            result.jar.set_cookie(copy(cookie))
        return result

    def request(self, method: str, path: str, body=None, *, csrf=False, headers=None):
        request_headers = {"Accept": "application/json", **(headers or {})}
        if csrf:
            status, _, _ = self.request("GET", "/api/auth/csrf")
            if status != 204:
                raise RunnerError("No se pudo emitir CSRF en la prueba aislada.")
            token = next((cookie.value for cookie in self.jar if cookie.name == "XSRF-TOKEN"), None)
            if not token:
                raise RunnerError("La API no emitió la cookie XSRF-TOKEN esperada.")
            request_headers["X-XSRF-TOKEN"] = token
        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            request_headers["Content-Type"] = "application/json"
        request = urllib.request.Request(
            self.base_url + path, data=data, method=method, headers=request_headers
        )
        try:
            response = self.opener.open(request, timeout=20)
            raw = response.read()
            status = response.status
            response_headers = response.headers
            response.close()
        except urllib.error.HTTPError as error:
            raw = error.read()
            status = error.code
            response_headers = error.headers
            error.close()
        self.observed.append(response_evidence(raw, response_headers))
        parsed = json.loads(raw) if raw else None
        return status, parsed, raw

    def login(self, username: str, password: str):
        return self.request(
            "POST", "/api/auth/login", {"username": username, "password": password}, csrf=True
        )


def _expect(actual, expected, label: str):
    if actual != expected:
        raise RunnerError(f"Falló el escenario aislado '{label}': resultado HTTP inesperado.")


def _provision(environment: Mapping[str, str], username: str, display_name: str, role: str, password: str):
    provision_env = dict(environment)
    provision_env.pop("GDS_PROVISION_PASSWORD", None)
    _run(
        ["dotnet", "run", "--no-build", "--project", str(BACKEND), "--", "provision-user",
         "--username", username, "--display-name", display_name, "--role", role],
        env=provision_env, input_text=password + "\n", timeout=120,
        step="el aprovisionamiento aislado",
    )


def _load_phase_factory():
    sys.path.insert(0, str(ROOT / "backend-tests"))
    try:
        from test_note_phases import BASE, complete_data
        return dict(BASE), complete_data
    finally:
        sys.path.pop(0)


def _integration_scenarios(container: DisposablePostgres):
    container.apply_schema_twice()
    _run(["dotnet", "build", str(BACKEND), "--nologo"], timeout=180, step="el build del backend")

    suffix = secrets.token_hex(4)
    owner_name = f"objective4.validator.{suffix}"
    outsider_name = f"objective4.operator.{suffix}"
    inactive_name = f"objective4.inactive.{suffix}"
    owner_password = "Ow9!Objective4-" + secrets.token_urlsafe(18)
    outsider_password = "Op9!Objective4-" + secrets.token_urlsafe(18)
    inactive_password = "In9!Objective4-" + secrets.token_urlsafe(18)
    invalid_password = "Wrong9!Objective4-" + secrets.token_urlsafe(18)

    with tempfile.TemporaryDirectory(prefix="gds-objective4-postgres-") as directory_name:
        directory = Path(directory_name)
        key_ring = directory / "keys"
        key_ring.mkdir(mode=0o700)
        environment = runtime_environment(os.environ, container.runtime_connection_string, key_ring)
        deployer_environment = runtime_environment(
            os.environ, container.deployer_connection_string, key_ring
        )
        _provision(deployer_environment, owner_name, "Validador Objetivo 4", "validador", owner_password)
        _provision(deployer_environment, outsider_name, "Operador Objetivo 4", "operador", outsider_password)
        _provision(deployer_environment, inactive_name, "Cuenta Inactiva Objetivo 4", "operador", inactive_password)
        normalized_owner = owner_name.upper().replace("'", "''")
        owner_password_hash = container.psql(
            f'''SELECT COALESCE("PasswordHash",'') FROM public."AspNetUsers" WHERE "NormalizedUserName"='{normalized_owner}';''',
            container.deployer,
            step="la lectura controlada del hash sintético",
        )
        if not owner_password_hash:
            raise RunnerError("La cuenta sintética no contiene un hash Identity verificable.")

        api = ApiProcess(environment, directory)
        sessions: list[HttpSession] = []
        try:
            api.start()

            anonymous = HttpSession(api.url)
            sessions.append(anonymous)
            status, _, _ = anonymous.request(
                "GET", "/api/notes", headers={
                    "X-Test-User": "11111111-1111-4111-8111-111111111111",
                    "X-Test-Roles": "administrador",
                },
            )
            _expect(status, 401, "cabeceras Testing ignoradas")

            unknown = HttpSession(api.url)
            wrong = HttpSession(api.url)
            inactive = HttpSession(api.url)
            sessions.extend([unknown, wrong, inactive])
            unknown_response = unknown.login("cuenta.inexistente", invalid_password)
            wrong_response = wrong.login(owner_name, invalid_password)
            _expect(unknown_response[0], 401, "usuario desconocido")
            _expect(wrong_response[0], 401, "contraseña incorrecta")
            if unknown_response[2] != wrong_response[2]:
                raise RunnerError("Las respuestas públicas de credenciales inválidas no son equivalentes.")

            normalized_inactive = inactive_name.upper().replace("'", "''")
            container.psql(
                f'''UPDATE public."AspNetUsers" SET "IsActive"=false WHERE "NormalizedUserName"='{normalized_inactive}';''',
                container.deployer,
                step="la desactivación administrativa de la cuenta sintética",
            )
            inactive_response = inactive.login(inactive_name, inactive_password)
            _expect(inactive_response[0], 401, "usuario inactivo")
            if inactive_response[2] != unknown_response[2]:
                raise RunnerError("La cuenta inactiva revela una respuesta pública distinta.")

            owner = HttpSession(api.url)
            sessions.append(owner)
            _expect(owner.login(owner_name, owner_password)[0], 200, "login persistido")
            _expect(owner.request("GET", "/api/auth/me")[0], 200, "sesión autenticada")
            copied = owner.clone()
            sessions.append(copied)
            _expect(owner.request("POST", "/api/auth/logout", csrf=True)[0], 204, "logout")
            _expect(copied.request("GET", "/api/auth/me")[0], 401, "revocación de cookie copiada")

            _expect(owner.login(owner_name, owner_password)[0], 200, "nuevo login")
            api.restart()
            for session in sessions:
                session.move(api.url)
            _expect(owner.request("GET", "/api/auth/me")[0], 200, "sesión y usuario sobreviven reinicio")

            base, complete_data = _load_phase_factory()
            _expect(owner.request("POST", "/api/notes", base, csrf=False)[0], 400, "CSRF obligatorio")
            status, note, _ = owner.request("POST", "/api/notes", base, csrf=True)
            _expect(status, 200, "crear incidencia PostgreSQL")
            note_id = note["id"]

            outsider = HttpSession(api.url)
            sessions.append(outsider)
            _expect(outsider.login(outsider_name, outsider_password)[0], 200, "login segundo usuario")
            _expect(outsider.request("GET", f"/api/notes/{note_id}")[0], 404, "ownership lectura")
            outsider_status, outsider_note, _ = outsider.request("POST", "/api/notes", base, csrf=True)
            _expect(outsider_status, 200, "incidencia de operador")
            save_body = {"expectedRevision": outsider_note["revision"], "data": complete_data("indications")}
            outsider_status, outsider_note, _ = outsider.request(
                "PUT", f'/api/notes/{outsider_note["id"]}/phases/indications', save_body, csrf=True
            )
            _expect(outsider_status, 200, "borrador de operador")
            _expect(
                outsider.request(
                    "POST", f'/api/notes/{outsider_note["id"]}/phases/indications/validate',
                    {"expectedRevision": outsider_note["revision"]}, csrf=True,
                )[0],
                403, "rol operador sin validación",
            )

            status, note, _ = owner.request(
                "PUT", f"/api/notes/{note_id}/phases/indications",
                {"expectedRevision": note["revision"], "data": complete_data("indications")}, csrf=True,
            )
            _expect(status, 200, "guardar fase 1")

            concurrent_a = owner.clone()
            concurrent_b = owner.clone()
            sessions.extend([concurrent_a, concurrent_b])
            validation_path = f"/api/notes/{note_id}/phases/indications/validate"
            validation_body = {"expectedRevision": note["revision"]}
            with ThreadPoolExecutor(max_workers=2) as pool:
                results = list(pool.map(
                    lambda session: session.request("POST", validation_path, validation_body, csrf=True),
                    (concurrent_a, concurrent_b),
                ))
            statuses = sorted(result[0] for result in results)
            if statuses != [200, 409]:
                raise RunnerError("La validación concurrente no produjo exactamente 200 y 409.")
            note = next(result[1] for result in results if result[0] == 200)

            api.stop()
            container.restart()
            api.start()
            for session in sessions:
                session.move(api.url)
            _expect(owner.request("GET", f"/api/notes/{note_id}")[0], 200, "fases sobreviven reinicio intermedio")

            for stage in ("reconnaissance", "surveillance", "mitigation"):
                status, note, _ = owner.request(
                    "PUT", f"/api/notes/{note_id}/phases/{stage}",
                    {"expectedRevision": note["revision"], "data": complete_data(stage)}, csrf=True,
                )
                _expect(status, 200, f"guardar {stage}")
                status, note, _ = owner.request(
                    "POST", f"/api/notes/{note_id}/phases/{stage}/validate",
                    {"expectedRevision": note["revision"]}, csrf=True,
                )
                _expect(status, 200, f"validar {stage}")
            if note["workflow"]["status"] != "closed":
                raise RunnerError("El flujo PostgreSQL no cerró después de las cuatro fases.")

            quoted_note_id = note_id.replace("'", "''")
            validations = container.scalar(f"""
                SELECT count(*)
                FROM app.phase_validations validation
                JOIN app.incident_phases phase ON phase.id=validation.phase_id
                JOIN app.incidents incident ON incident.id=phase.incident_id
                WHERE incident.public_id='{quoted_note_id}';
            """)
            phase_events = container.scalar(f"""
                SELECT count(*)
                FROM app.incident_events event
                JOIN app.incidents incident ON incident.id=event.incident_id
                WHERE incident.public_id='{quoted_note_id}'
                  AND event.event_text <> 'Incidencia registrada. Fase 1 habilitada.';
            """)
            if validations != 4 or phase_events != 8:
                raise RunnerError("La persistencia SQL no contiene cuatro validaciones y ocho eventos de fase.")

            status, false_positive, _ = owner.request("POST", "/api/notes", base, csrf=True)
            _expect(status, 200, "crear incidencia falso positivo")
            for stage in ("indications", "reconnaissance"):
                data = complete_data(stage)
                if stage == "reconnaissance":
                    data["review"]["finding"] = "false-positive"
                status, false_positive, _ = owner.request(
                    "PUT", f'/api/notes/{false_positive["id"]}/phases/{stage}',
                    {"expectedRevision": false_positive["revision"], "data": data}, csrf=True,
                )
                _expect(status, 200, f"guardar {stage} del falso positivo")
                status, false_positive, _ = owner.request(
                    "POST", f'/api/notes/{false_positive["id"]}/phases/{stage}/validate',
                    {"expectedRevision": false_positive["revision"]}, csrf=True,
                )
                _expect(status, 200, f"validar {stage} del falso positivo")
            if false_positive["workflow"]["status"] != "discarded":
                raise RunnerError("El reconocimiento falso positivo no cerró el flujo como descartado.")
        finally:
            api.stop()

        evidence = b"\n".join(body for session in sessions for body in session.observed)
        if api.log_path.exists():
            evidence += b"\n" + api.log_path.read_bytes()
        forbidden = [
            container.superuser_password.encode(), container.deployer_password.encode(),
            container.runtime_password.encode(), owner_password.encode(), outsider_password.encode(),
            inactive_password.encode(), invalid_password.encode(), owner_password_hash.encode(),
        ]
        ensure_secrets_absent(evidence, forbidden)


def main() -> int:
    container = None
    previous_handlers = {}
    exit_code = 0
    try:
        validate_cli_arguments(sys.argv)
        docker = preflight(os.environ)
        # Probe the daemon without propagating stderr, paths, or daemon metadata.
        _run([docker, "info", "--format", "{{.ServerVersion}}"], timeout=20, step="el preflight de Docker")
        container = DisposablePostgres(docker)

        def interrupt(_signal, _frame):
            raise KeyboardInterrupt

        for signal_name in (signal.SIGINT, signal.SIGTERM):
            previous_handlers[signal_name] = signal.signal(signal_name, interrupt)
        with_disposable_container(container, _integration_scenarios)
        print("Objetivo 4 PostgreSQL: escenarios aislados completados.")
    except KeyboardInterrupt:
        print("Objetivo 4 PostgreSQL: ejecución interrumpida; se reintentará la limpieza.", file=sys.stderr)
        exit_code = 130
    except RunnerError as error:
        print(f"Objetivo 4 PostgreSQL: {error}", file=sys.stderr)
        exit_code = 1
    finally:
        if container is not None and container.cleanup_pending:
            try:
                container.cleanup()
            except RunnerError as error:
                print(f"Objetivo 4 PostgreSQL: {error}", file=sys.stderr)
                exit_code = 1
        for signal_name, handler in previous_handlers.items():
            signal.signal(signal_name, handler)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
