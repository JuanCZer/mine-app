"""Verify SPA hosting and API isolation through a local Kestrel instance."""
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'server/MineApp.Api.csproj').is_file())

class SpaHostingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(['dotnet', 'build', str(ROOT / 'server/MineApp.Api.csproj'), '--nologo'],
                       cwd=ROOT, check=True, capture_output=True, text=True)

    def test_spa_assets_and_api_boundaries(self):
        with tempfile.TemporaryDirectory(prefix='mineapp-spa-') as directory:
            temp = Path(directory)
            webroot = temp / 'public'
            webroot.mkdir()
            (webroot / 'index.html').write_text('<html>spa-test</html>')
            (webroot / 'app.js').write_text('window.spa = true;')
            (webroot / 'api').mkdir()
            (webroot / 'api' / 'private.js').write_text('private-asset')
            with socket.socket() as listener:
                listener.bind(('127.0.0.1', 0))
                port = listener.getsockname()[1]
            origin = f'http://127.0.0.1:{port}'
            env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'Spa__Enabled': 'true',
                   'ConnectionStrings__Postgres': 'Host=localhost;Database=unused;Username=unused;Password=unused;SSL Mode=VerifyFull',
                   'ConnectionStrings__IdentityAdmin': 'Host=localhost;Database=unused;Username=identity;Password=unused;SSL Mode=VerifyFull',
                   'IdentitySecurity__RateLimitHmacKeys__0__KeyId': 'rate-limit-v1',
                   'IdentitySecurity__RateLimitHmacKeys__0__SecretBase64': 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
                   'DataProtection__KeyRingPath': str(temp / 'keys'), 'ASPNETCORE_WEBROOT': str(webroot)}
            process = subprocess.Popen(['dotnet', 'run', '--no-build', '--project', str(ROOT / 'server/MineApp.Api.csproj'),
                                        '--', '--urls', origin], cwd=ROOT, env=env,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            def get(path, method='GET'):
                try:
                    request = urllib.request.Request(origin + path, method=method)
                    with urllib.request.urlopen(request, timeout=1) as response:
                        return response.status, response.headers.get_content_type(), response.read()
                except urllib.error.HTTPError as error:
                    with error: return error.code, error.headers.get_content_type(), error.read()
            try:
                for _ in range(100):
                    try:
                        get('/api/health')
                        break
                    except (OSError, TimeoutError):
                        if process.poll() is not None: self.fail('La API terminó antes de iniciar')
                        time.sleep(.05)
                else: self.fail('La API no inició')
                self.assertEqual(get('/')[0], 200)
                self.assertIn(b'spa-test', get('/incidencias')[2])
                self.assertEqual(get('/incidencias', 'HEAD')[0], 200)
                self.assertIn(b'spa-test', get('/cuenta/perfil')[2])
                self.assertIn(b'window.spa', get('/app.js')[2])
                for path in ['/missing.js', '/api', '/api/nonexistent', '/api/private.js', '/api/notes', '/swagger/nonexistent']:
                    status, content_type, body = get(path)
                    self.assertNotEqual(status, 200, path)
                    self.assertNotIn(b'spa-test', body, path)
                    self.assertNotIn(b'private-asset', body, path)
                self.assertEqual(get('/api/notes')[0], 401)
                status, _, body = get('/incidencias', 'POST')
                self.assertNotEqual(status, 200)
                self.assertNotIn(b'spa-test', body)
                self.assertEqual(get('/swagger/v1/swagger.json')[1], 'application/json')
            finally:
                process.terminate()
                try: process.wait(timeout=5)
                except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
