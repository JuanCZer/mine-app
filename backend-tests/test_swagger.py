"""Swagger/OpenAPI behavior over the real Notes API Kestrel boundary."""

from contextlib import contextmanager
import json
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


class SwaggerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(
            ["dotnet", "build", str(BACKEND / "Notes.Api.csproj"), "--nologo"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    @contextmanager
    def api(self, environment):
        with tempfile.TemporaryDirectory(prefix="gds-swagger-") as directory:
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]

            origin = f"http://127.0.0.1:{port}"
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
                        origin,
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
                            with urllib.request.urlopen(f"{origin}/api/health", timeout=0.2):
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
                    yield origin
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

    def get(self, url):
        try:
            with urllib.request.urlopen(url, timeout=5) as response:
                return response.status, response.headers.get_content_type(), response.read()
        except urllib.error.HTTPError as error:
            with error:
                return error.code, error.headers.get_content_type(), error.read()

    def test_development_serves_swagger_ui_and_openapi_json(self):
        with self.api("Development") as origin:
            ui_status, ui_type, ui_body = self.get(f"{origin}/swagger")
            json_status, json_type, json_body = self.get(
                f"{origin}/swagger/v1/swagger.json"
            )

        self.assertEqual(ui_status, 200)
        self.assertEqual(ui_type, "text/html")
        self.assertIn(b"Swagger UI", ui_body)
        self.assertEqual(json_status, 200)
        self.assertEqual(json_type, "application/json")
        self.assertEqual(json.loads(json_body)["openapi"], "3.0.4")

    def test_non_development_environment_does_not_serve_swagger(self):
        with self.api("Production") as origin:
            ui_status, _, _ = self.get(f"{origin}/swagger")
            json_status, _, _ = self.get(f"{origin}/swagger/v1/swagger.json")

        self.assertEqual(ui_status, 404)
        self.assertEqual(json_status, 404)

    def test_openapi_declares_session_cookie_only_on_protected_operations(self):
        with self.api("Development") as origin:
            status, _, body = self.get(f"{origin}/swagger/v1/swagger.json")

        self.assertEqual(status, 200)
        document = json.loads(body)
        self.assertEqual(
            document["components"]["securitySchemes"]["SessionCookie"],
            {
                "type": "apiKey",
                "description": "Cookie de sesión HttpOnly emitida por el inicio de sesión.",
                "name": "GDS.Session",
                "in": "cookie",
            },
        )

        anonymous = {
            ("/api/health", "get"),
            ("/api/auth/csrf", "get"),
            ("/api/auth/login", "post"),
        }
        documented = set()
        for path, path_item in document["paths"].items():
            for method, operation in path_item.items():
                if method not in {"get", "post", "put", "delete", "patch"}:
                    continue
                documented.add((path, method))
                expected = None if (path, method) in anonymous else [{"SessionCookie": []}]
                self.assertEqual(operation.get("security"), expected, (path, method))

        self.assertTrue(anonymous.issubset(documented))
        self.assertIn(("/api/auth/me", "get"), documented)
        self.assertIn(("/api/notes", "post"), documented)

    def test_swagger_interceptor_preserves_same_origin_cookies_and_scopes_csrf(self):
        with self.api("Development") as origin:
            status, _, initializer = self.get(
                f"{origin}/swagger/index.js"
            )

        self.assertEqual(status, 200)
        probe = r"""
const vm = require('node:vm');
const input = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
let captured;
function SwaggerUIBundle(config) { captured = config; return { initOAuth() {} }; }
SwaggerUIBundle.presets = { apis: {} };
SwaggerUIBundle.plugins = { DownloadUrl: {} };
const sandbox = {
  window: { location: new URL(input.origin) },
  location: new URL(input.origin),
  document: { cookie: 'other=value; XSRF-TOKEN=token%20with%20spaces' },
  SwaggerUIBundle,
  SwaggerUIStandalonePreset: {},
  URL,
  decodeURIComponent,
};
vm.createContext(sandbox);
vm.runInContext(input.initializer, sandbox);
sandbox.window.onload();
const requests = [
  { url: input.origin + '/api/notes/', method: 'POST', headers: {} },
  { url: input.origin + '/api/notes/', method: 'GET', headers: {} },
  { url: input.origin + '/api/notes/', method: 'TRACE', headers: {} },
  { url: input.origin + '/swagger/v1/swagger.json', method: 'POST', headers: {} },
  { url: 'https://example.test/api/notes/', method: 'POST', headers: {} },
];
Promise.all(requests.map(request => captured.requestInterceptor(request))).then(values => {
  process.stdout.write(JSON.stringify(values));
});
"""
        result = subprocess.run(
            ["node", "-e", probe],
            input=json.dumps(
                {"origin": origin, "initializer": initializer.decode("utf-8")}
            ),
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        mutable_api, safe_api, trace_api, other_path, other_origin = json.loads(result.stdout)
        self.assertEqual(mutable_api["credentials"], "same-origin")
        self.assertEqual(mutable_api["headers"]["X-XSRF-TOKEN"], "token with spaces")
        self.assertEqual(safe_api["credentials"], "same-origin")
        self.assertNotIn("X-XSRF-TOKEN", safe_api["headers"])
        self.assertNotIn("X-XSRF-TOKEN", trace_api["headers"])
        self.assertNotIn("X-XSRF-TOKEN", other_path["headers"])
        self.assertNotIn("X-XSRF-TOKEN", other_origin["headers"])

    def test_swagger_does_not_weaken_real_csrf_rejection(self):
        with self.api("Development") as origin:
            request = urllib.request.Request(
                f"{origin}/api/auth/login",
                data=b'{"username":"operator","password":"not-a-secret"}',
                method="POST",
                headers={"Content-Type": "application/json"},
            )
            try:
                urllib.request.urlopen(request, timeout=5)
                self.fail("login without an antiforgery token was accepted")
            except urllib.error.HTTPError as error:
                with error:
                    status = error.code
                    response = json.loads(error.read())

        self.assertEqual(status, 400)
        self.assertEqual(response, {"error": "Token CSRF inválido."})


if __name__ == "__main__":
    unittest.main(verbosity=2)
