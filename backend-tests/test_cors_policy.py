"""Integration tests for the environment-aware credentialed CORS policy."""

import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
BACKEND = ROOT / "backend"
IDENTITY_TEST_SECRET = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="


class CorsPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            ["dotnet", "build", str(BACKEND), "--nologo"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    def cors_origin(self, environment, origin, allowed_origins=()):
        with tempfile.TemporaryDirectory(prefix="gds-cors-") as directory:
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]

            url = f"http://127.0.0.1:{port}"
            env = {
                **os.environ,
                "ASPNETCORE_ENVIRONMENT": environment,
                "ConnectionStrings__Postgres": (
                    "Host=localhost;Database=unused;Username=unused;Password=unused;"
                    "SSL Mode=VerifyFull"
                ),
                "ConnectionStrings__IdentityAdmin": (
                    "Host=localhost;Database=unused;Username=identity;Password=unused;"
                    "SSL Mode=VerifyFull"
                ),
                "IdentitySecurity__RateLimitHmacKeys__0__KeyId": "rate-limit-v1",
                "IdentitySecurity__RateLimitHmacKeys__0__SecretBase64": IDENTITY_TEST_SECRET,
                "DataProtection__KeyRingPath": directory,
            }
            for index, value in enumerate(allowed_origins):
                env[f"Cors__AllowedOrigins__{index}"] = value

            log_path = Path(directory) / "api.log"
            with log_path.open("w+") as log:
                process = subprocess.Popen(
                    [
                        "dotnet",
                        "run",
                        "--no-build",
                        "--project",
                        str(BACKEND),
                        "--",
                        "--urls",
                        url,
                    ],
                    cwd=ROOT,
                    env=env,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                )
                try:
                    for _ in range(100):
                        if process.poll() is not None:
                            log.seek(0)
                            self.fail(f"API exited during startup:\n{log.read()}")
                        try:
                            with urllib.request.urlopen(f"{url}/api/health", timeout=0.2):
                                break
                        except urllib.error.HTTPError as error:
                            if error.code == 503:
                                error.close()
                                break
                            error.close()
                            time.sleep(0.05)
                        except (urllib.error.URLError, TimeoutError):
                            time.sleep(0.05)
                    else:
                        log.seek(0)
                        self.fail(f"API did not start:\n{log.read()}")

                    request = urllib.request.Request(
                        f"{url}/api/health",
                        method="OPTIONS",
                        headers={
                            "Origin": origin,
                            "Access-Control-Request-Method": "GET",
                        },
                    )
                    try:
                        with urllib.request.urlopen(request, timeout=2) as response:
                            return response.headers.get("Access-Control-Allow-Origin")
                    except urllib.error.HTTPError as error:
                        return error.headers.get("Access-Control-Allow-Origin")
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

    def test_production_does_not_authorize_localhost_by_default(self):
        self.assertIsNone(
            self.cors_origin("Production", "http://localhost:4200")
        )

    def test_development_authorizes_local_angular_origin(self):
        self.assertEqual(
            self.cors_origin("Development", "http://localhost:4200"),
            "http://localhost:4200",
        )

    def test_explicit_allowlist_authorizes_configured_production_origin(self):
        origin = "https://operaciones.example.test"
        self.assertEqual(
            self.cors_origin("Production", origin, [origin]),
            origin,
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
