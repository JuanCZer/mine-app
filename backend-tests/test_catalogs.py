"""API integration tests for the explicit Testing catalog provider."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
from auth_http import authenticated_request, TEST_USER

from test_note_phases import BASE, complete_data

ROOT = Path(__file__).resolve().parents[1]
IDENTITY_TEST_CONNECTION = 'Host=localhost;Database=unused;Username=identity;Password=unused;Ssl Mode=VerifyFull'
IDENTITY_TEST_SECRET = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='


class CatalogApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='gds-catalogs-')
        cls.data_path = Path(cls.directory.name) / 'notes.json'
        cls.seed_path = Path(cls.directory.name) / 'catalog-overrides.json'
        cls.seed_path.write_text('[]')
        cls.log = open(Path(cls.directory.name) / 'api.log', 'w+')
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        cls.url = f'http://127.0.0.1:{port}'
        env = {
            **os.environ,
            'Notes__DataPath': str(cls.data_path),
            'Notes__Provider': 'Json',
            'ASPNETCORE_ENVIRONMENT': 'Testing',
            'Authentication__EnableTestHeaders': 'true',
            'Catalogs__Provider': 'InMemory',
            'Catalogs__SeedFile': str(cls.seed_path),
            'ConnectionStrings__Postgres': IDENTITY_TEST_CONNECTION,
            'ConnectionStrings__IdentityAdmin': IDENTITY_TEST_CONNECTION,
            'IdentitySecurity__RateLimitHmacKeys__0__KeyId': 'rate-limit-v1',
            'IdentitySecurity__RateLimitHmacKeys__0__SecretBase64': IDENTITY_TEST_SECRET,
        }
        cls.process = subprocess.Popen(
            ['dotnet', 'run', '--no-build', '--project', str(ROOT / 'backend'), '--', '--urls', cls.url],
            env=env, stdout=cls.log, stderr=subprocess.STDOUT)
        for _ in range(100):
            try:
                urllib.request.urlopen(cls.url + '/api/health', timeout=0.2).close()
                return
            except urllib.error.HTTPError as error:
                if error.code == 503:
                    error.close()
                    return
                error.close()
            except (OSError, urllib.error.URLError):
                if cls.process.poll() is not None:
                    break
                time.sleep(0.1)
        cls.log.seek(0)
        message = cls.log.read()
        cls.tearDownClass()
        raise RuntimeError(message)

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        try:
            cls.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.process.kill()
            cls.process.wait()
        cls.log.close()
        cls.directory.cleanup()

    def request(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.url + path, data=data, method=method, headers={'Content-Type': 'application/json'})
        try:
            with authenticated_request(self.url, method, path, body) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            raw = error.read()
            status = error.code
            error.close()
            return status, json.loads(raw) if raw else None

    def setUp(self):
        self.seed_path.write_text('[]')
        status, self.note = self.request('POST', '/api/notes', BASE)
        self.assertEqual(status, 200)

    def save_indications(self, data):
        return self.request(
            'PUT',
            f'/api/notes/{self.note["id"]}/phases/indications',
            {'expectedRevision': self.note['revision'], 'data': data},
        )

    def write_overrides(self, values):
        temporary = self.seed_path.with_suffix('.tmp')
        temporary.write_text(json.dumps(values))
        temporary.replace(self.seed_path)

    def test_endpoint_returns_requested_catalogs_in_stable_order(self):
        status, body = self.request('GET', '/api/catalogs?keys=soils,seasons')
        self.assertEqual(status, 200)
        self.assertEqual(list(body['catalogs']), ['soils', 'seasons'])
        self.assertEqual([option['code'] for option in body['catalogs']['soils']], ['unknown', 'vertisol', 'andosol', 'leptosol'])
        self.assertTrue(all(option['active'] for option in body['catalogs']['soils']))
        self.assertGreaterEqual(body['version'], 1)

    def test_stage_filters_options_and_rejects_invalid_inputs(self):
        status, body = self.request('GET', '/api/catalogs?keys=platforms,sensors&stage=indications')
        self.assertEqual(status, 200)
        self.assertEqual([option['code'] for option in body['catalogs']['platforms']], ['if1200a'])
        self.assertNotIn('EOD', [option['code'] for option in body['catalogs']['sensors']])
        self.assertEqual(self.request('GET', '/api/catalogs?keys=doesNotExist')[0], 400)
        self.assertEqual(self.request('GET', '/api/catalogs?keys=soils&stage=unknown')[0], 400)

    def test_case_form_catalogs_include_global_risk_levels_during_indications(self):
        keys = 'mexicoStates,soils,seasons,riskLevels'
        status, body = self.request('GET', f'/api/catalogs?keys={keys}&stage=indications')
        self.assertEqual(status, 200)
        self.assertEqual(list(body['catalogs']), keys.split(','))
        self.assertEqual(
            [option['code'] for option in body['catalogs']['riskLevels']],
            ['low', 'medium', 'high'],
        )

    def test_catalog_membership_and_stage_are_enforced_on_save(self):
        data = complete_data('indications')
        data['soil'] = 'not-a-soil'
        status, body = self.save_indications(data)
        self.assertEqual(status, 400)
        self.assertIn('soil', body['issues'])

        data = complete_data('indications')
        data['evidence'][0]['sensor'] = 'EOD'
        status, body = self.save_indications(data)
        self.assertEqual(status, 400)
        self.assertIn('evidence.sensor', body['issues'])

    def test_new_active_database_option_is_read_and_saved_without_code_change(self):
        self.write_overrides([
            {'key': 'soils', 'code': 'volcanic-custom', 'label': 'Suelo volcánico local', 'sortOrder': 5, 'active': True, 'stages': []}
        ])
        status, body = self.request('GET', '/api/catalogs?keys=soils')
        self.assertEqual(status, 200)
        self.assertIn('volcanic-custom', [option['code'] for option in body['catalogs']['soils']])
        data = complete_data('indications')
        data['soil'] = 'volcanic-custom'
        status, body = self.save_indications(data)
        self.assertEqual(status, 200, body)

    def test_inactive_option_is_historical_only(self):
        data = complete_data('indications')
        status, saved = self.save_indications(data)
        self.assertEqual(status, 200, saved)
        self.note = saved

        self.write_overrides([
            {'key': 'soils', 'code': 'andosol', 'label': 'Andosol retirado', 'sortOrder': 3, 'active': False, 'stages': []}
        ])
        status, visible = self.request('GET', '/api/catalogs?keys=soils')
        self.assertEqual(status, 200)
        self.assertNotIn('andosol', [option['code'] for option in visible['catalogs']['soils']])
        status, historical = self.request('GET', '/api/catalogs?keys=soils&includeInactive=true')
        self.assertEqual(status, 200)
        self.assertFalse(next(option for option in historical['catalogs']['soils'] if option['code'] == 'andosol')['active'])

        unchanged = complete_data('indications')
        status, saved = self.save_indications(unchanged)
        self.assertEqual(status, 200, saved)
        self.note = saved
        status, validated = self.request(
            'POST',
            f'/api/notes/{self.note["id"]}/phases/indications/validate',
            {'expectedRevision': self.note['revision']},
        )
        self.assertEqual(status, 200, validated)

        status, fresh = self.request('POST', '/api/notes', {**BASE, 'title': 'Nueva incidencia'})
        self.assertEqual(status, 200)
        self.note = fresh
        status, conflict = self.save_indications(complete_data('indications'))
        self.assertEqual(status, 409)
        self.assertIn('soil', conflict['issues'])


class PostgreSqlFailureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='gds-catalog-unavailable-')
        cls.log = open(Path(cls.directory.name) / 'api.log', 'w+')
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            unavailable_port = sock.getsockname()[1]
        cls.url = f'http://127.0.0.1:{port}'
        env = {
            **os.environ,
            'ASPNETCORE_ENVIRONMENT': 'Testing',
            'Authentication__EnableTestHeaders': 'true',
            'Catalogs__Provider': 'PostgreSql',
            'ConnectionStrings__Catalogs': (
                f'Host=127.0.0.1;Port={unavailable_port};Database=missing;'
                'Username=missing;Password=missing;Timeout=1;Command Timeout=1'
            ),
            'ConnectionStrings__IdentityAdmin': IDENTITY_TEST_CONNECTION,
            'IdentitySecurity__RateLimitHmacKeys__0__KeyId': 'rate-limit-v1',
            'IdentitySecurity__RateLimitHmacKeys__0__SecretBase64': IDENTITY_TEST_SECRET,
        }
        cls.process = subprocess.Popen(
            ['dotnet', 'run', '--no-build', '--project', str(ROOT / 'backend'), '--', '--urls', cls.url],
            env=env, stdout=cls.log, stderr=subprocess.STDOUT)
        for _ in range(100):
            try:
                urllib.request.urlopen(cls.url + '/api/health', timeout=0.2).close()
                return
            except urllib.error.HTTPError as error:
                if error.code == 503:
                    error.close()
                    return
                error.close()
            except (OSError, urllib.error.URLError):
                if cls.process.poll() is not None:
                    break
                time.sleep(0.1)
        cls.log.seek(0)
        message = cls.log.read()
        cls.tearDownClass()
        raise RuntimeError(message)

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        try:
            cls.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.process.kill()
            cls.process.wait()
        cls.log.close()
        cls.directory.cleanup()

    def test_database_failure_returns_generic_503(self):
        request = urllib.request.Request(self.url + '/api/catalogs?keys=soils')
        with self.assertRaises(urllib.error.HTTPError) as caught:
            authenticated_request(self.url, 'GET', '/api/catalogs?keys=soils')
        self.assertEqual(caught.exception.code, 503)
        body = json.loads(caught.exception.read())
        caught.exception.close()
        self.assertEqual(body, {'error': 'El servicio de catálogos no está disponible temporalmente.'})


if __name__ == '__main__':
    unittest.main(verbosity=2)
