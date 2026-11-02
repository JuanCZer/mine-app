"""Integration tests for mandatory note-phase validation; uses an isolated data file."""
import concurrent.futures
import json
import math
import random
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
from auth_http import authenticated_request, TEST_USER

ROOT = Path(__file__).resolve().parents[1]
IDENTITY_TEST_CONNECTION = 'Host=localhost;Database=unused;Username=identity;Password=unused;Ssl Mode=VerifyFull'
IDENTITY_TEST_SECRET = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='
STAGES = ['indications', 'reconnaissance', 'surveillance', 'mitigation']
BASE = dict(title='Nota de prueba', content='Observación ficticia.', state='Michoacán', altitude=19.7, longitude=-101.2)


def complete_data(stage):
    sensors = {
        'indications': ['LiDAR'],
        'reconnaissance': ['GPR', 'Magnetometría', 'RGB', 'IR'],
        'surveillance': ['EO/IR', 'IMSI/IMEI/TMSI'],
        'mitigation': ['GPR', 'EOD', 'TECHINT'],
    }[stage]
    return dict(
        review=dict(baseline='BASE-01', comparison='COMPARACIÓN-01', finding='confirmed', environment='reviewed', report='EOD-01', techint='TECHINT-01', reviewer='Revisor de prueba', notes='Revisión documentada.'),
        soil='andosol', season='rainy', terrainNotes='Contexto documentado de prueba.',
        evidence=[dict(id=f'ev-{index}', sensor=sensor, reference=f'REF-{index}', summary='Referencia de prueba.') for index, sensor in enumerate(sensors)],
        indications=dict(
            platform='if1200a',
            blueUasNdaaCompliant=True,
            flightDurationMin=40,
            lidarPointRate=640000,
            payloadLb=18.5,
            missionSpeedMph=52,
            windKnots=18,
            terrainModelReference='3D-IF1200A-01',
            differentialAnalysisCompleted=True,
            surfaceVariationMm=6.4,
            actionableAlertGenerated=True,
            personnelNotExposed=True,
            coordinateTransferredToVeles=True,
            observedAt='2026-09-20T10:30',
            collectionSource='if1200a-lidar-rgb-ir',
            signatures=['surface-removal', 'vegetation-change'],
            alternativeCauses='Se consideró erosión y actividad agrícola; la comparación diferencial requiere reconocimiento.',
            depressionDepthCm=8.2,
        ),
        reconnaissance=dict(
            magneticReadingNt=12.4,
            gprDepthCm=24,
            rtkAccuracyCm=2,
            localizationAccuracyCm=4.5,
            probability='high',
            priority='high',
            georeferencedPointReference='GIS-VELES-01',
            riskMapReference='RISK-VELES-01',
            aiClassificationReference='IA-VELES-01',
            fusionCompleted=True,
        ),
        surveillance=dict(
            observationStartedAt='2026-09-20T11:00',
            observationEndedAt='2026-09-20T11:45',
            radioTechnologies=['4G', '5G'],
            methods=['imsi-imei-tmsi-capture', 'precision-mapping', 'uav-camera-feed'],
            deployment='combined',
            coveredChannels=4,
            duplicatedChannelConfiguration=True,
            activeDeviceCount=0,
            humanActivity='not-detected',
            environmentDecision='clear-for-mitigation',
        ),
        mitigation=dict(
            coordinateReceived=True,
            platform='husky-route-clearance',
            protectionLevel='stanag-4',
            personnelProtectionConfirmed=True,
            equipmentProtectionConfirmed=True,
            detectionAssets=2,
            interrogationAssets=1,
            gprCoverageOverlap=True,
            safetyMarginM=8,
            remoteInterrogationCompleted=True,
            artifactType='Objeto caracterizado por EOD',
            orientation='Orientación documentada',
            activationMechanism='Mecanismo documentado',
            eodHandoffReference='EOD-01',
            neutralizationResult='neutralized',
            routeResult='route-opened',
            techintComposition='Composición documentada.',
            techintManufacture='Manufactura documentada.',
            techintSignature='Firma documentada.',
            catalogFeedbackReference='TECHINT-VELES-01',
        ),
    )


class PhaseApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='gds-note-phases-')
        cls.data_path = Path(cls.directory.name) / 'notes.json'
        cls.log = open(Path(cls.directory.name) / 'api.log', 'w+')
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        cls.url = f'http://127.0.0.1:{port}'
        env = {
            **os.environ,
            'Notes__DataPath': str(cls.data_path),
            'Notes__Provider': 'Json',
            'Notes__MaximumMapLocations': '15000',
            'ASPNETCORE_ENVIRONMENT': 'Testing',
            'Authentication__EnableTestHeaders': 'true',
            'Catalogs__Provider': 'InMemory',
            'ConnectionStrings__Postgres': IDENTITY_TEST_CONNECTION,
            'ConnectionStrings__IdentityAdmin': IDENTITY_TEST_CONNECTION,
            'IdentitySecurity__RateLimitHmacKeys__0__KeyId': 'rate-limit-v1',
            'IdentitySecurity__RateLimitHmacKeys__0__SecretBase64': IDENTITY_TEST_SECRET,
        }
        cls.process = subprocess.Popen(['dotnet', 'run', '--no-build', '--project', str(ROOT / 'backend'), '--', '--urls', cls.url], env=env, stdout=cls.log, stderr=subprocess.STDOUT)
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
        status, self.note = self.request('POST', '/api/notes', BASE)
        self.assertEqual(status, 200)

    def test_shared_map_counts_all_owners_and_preserves_write_ownership(self):
        other = '22222222-2222-4222-8222-222222222222'
        with authenticated_request(self.url, 'POST', '/api/notes', dict(BASE, title='Otro', longitude=-101.8), user=other, role='operador') as response:
            foreign = json.load(response)
        status, sectors = self.request('GET', '/api/map/sectors')
        self.assertEqual(status, 200)
        self.assertGreaterEqual(sectors['total'], 2)
        self.assertTrue(any(item['count'] >= 1 for item in sectors['sectors']))
        status, points = self.request('GET', '/api/map/incidents?west=-103&south=19&east=-101&north=20')
        self.assertEqual(status, 200)
        self.assertIn(foreign['id'], [point['id'] for point in points['items']])
        status, detail = self.request('GET', '/api/map/incidents/' + foreign['id'])
        self.assertEqual(status, 200)
        self.assertEqual(detail['title'], 'Otro')
        status, _ = self.request('GET', '/api/notes/' + foreign['id'])
        self.assertEqual(status, 404)
        status, _ = self.request('PUT', '/api/notes/' + foreign['id'], dict(BASE, expectedRevision=0))
        self.assertEqual(status, 404)
        status, _ = self.request('GET', '/api/map/incidents?west=0&south=0&east=181&north=1')
        self.assertEqual(status, 400)
        with self.assertRaises(urllib.error.HTTPError) as error:
            urllib.request.urlopen(self.url + '/api/map/sectors')
        self.assertEqual(error.exception.code, 401)
        error.exception.close()

    def test_map_detail_rejects_missing_and_invalid_coordinates_without_changing_owner_access(self):
        data = json.loads(self.data_path.read_text())
        base = next(note for note in data if note['id'] == self.note['id'])
        for index, (latitude, longitude) in enumerate(((None, -101.5), (19.5, None), (91, -101.5), (19.5, -181))):
            data.append(dict(base, id=f'map-bad-{index}', altitude=latitude, longitude=longitude))
        self.data_path.write_text(json.dumps(data))
        for index in range(4):
            with self.subTest(index=index):
                status, _ = self.request('GET', f'/api/map/incidents/map-bad-{index}')
                self.assertEqual(status, 404)
                status, _ = self.request('GET', f'/api/notes/map-bad-{index}')
                self.assertEqual(status, 200)

    def test_map_is_available_to_each_authenticated_role(self):
        for role in ('operador', 'validador', 'administrador'):
            with self.subTest(role=role), authenticated_request(self.url, 'GET', '/api/map/sectors', role=role) as response:
                self.assertEqual(response.status, 200)
                self.assertIn('sectors', json.load(response))

    def test_map_counts_distinct_incidents_within_one_kilometer(self):
        data = json.loads(self.data_path.read_text())
        base = next(note for note in data if note['id'] == self.note['id'])
        # The reference incident plus 105 distinct incidents, including duplicates.
        for index in range(106):
            data.append(dict(base, id=f'proximity-example-{index}', altitude=20.09798,
                             longitude=-98.76842 if index < 3 else -98.76342))
        data.append(dict(base, id='proximity-outside', altitude=20.1071, longitude=-98.76842))
        self.data_path.write_text(json.dumps(data))
        status, result = self.request('GET', '/api/map/sectors')
        self.assertEqual(status, 200)
        centers = [sector for sector in result['sectors']
                   if sector['latitude'] == 20.09798 and sector['longitude'] == -98.76842]
        self.assertEqual(len(centers), 3)
        self.assertTrue(all(center['count'] == 106 for center in centers))

    def test_map_counts_across_antimeridian_and_near_poles(self):
        data = json.loads(self.data_path.read_text())
        base = next(note for note in data if note['id'] == self.note['id'])
        for index, (lat, lon) in enumerate(((0, 179.999), (0, -179.999),
                                             (89.999, -120), (89.999, 120))):
            data.append(dict(base, id=f'proximity-extreme-{index}', altitude=lat, longitude=lon))
        self.data_path.write_text(json.dumps(data))
        status, result = self.request('GET', '/api/map/sectors')
        self.assertEqual(status, 200)
        for lat, lon in ((0, 179.999), (0, -179.999), (89.999, -120), (89.999, 120)):
            center = next(s for s in result['sectors'] if s['latitude'] == lat and s['longitude'] == lon)
            self.assertEqual(center['count'], 2)

    def test_map_spatial_index_matches_reference_distance(self):
        data = json.loads(self.data_path.read_text())
        base = next(note for note in data if note['id'] == self.note['id'])
        rng = random.Random(42)
        locations = [(40 + rng.uniform(-0.02, 0.02), -70 + rng.uniform(-0.02, 0.02)) for _ in range(150)]
        for index, (lat, lon) in enumerate(locations):
            data.append(dict(base, id=f'proximity-random-{index}', altitude=lat, longitude=lon))
        self.data_path.write_text(json.dumps(data))
        status, result = self.request('GET', '/api/map/sectors')
        self.assertEqual(status, 200)
        for lat, lon in locations:
            sector = next(s for s in result['sectors'] if s['latitude'] == lat and s['longitude'] == lon)
            expected = 0
            for other_lat, other_lon in locations:
                lat_diff = math.radians(other_lat - lat)
                lon_diff = math.radians(other_lon - lon)
                a = math.sin(lat_diff / 2) ** 2 + math.cos(math.radians(lat)) * math.cos(math.radians(other_lat)) * math.sin(lon_diff / 2) ** 2
                expected += 2 * 6371000 * math.asin(min(1, math.sqrt(a))) <= 1000
            self.assertEqual(sector['count'], expected)

    def test_map_radius_includes_boundary_and_excludes_outside(self):
        data = json.loads(self.data_path.read_text())
        base = next(note for note in data if note['id'] == self.note['id'])
        for index, distance in enumerate((0, 999.999, 1000, 1000.001)):
            data.append(dict(base, id=f'proximity-boundary-{index}', altitude=math.degrees(distance / 6371000), longitude=0))
        self.data_path.write_text(json.dumps(data))
        status, result = self.request('GET', '/api/map/sectors')
        self.assertEqual(status, 200)
        center = next(s for s in result['sectors'] if s['latitude'] == 0 and s['longitude'] == 0)
        self.assertEqual(center['count'], 3)

    def test_map_large_coincident_group_counts_each_incident(self):
        original = self.data_path.read_text()
        data = json.loads(original)
        base = next(note for note in data if note['id'] == self.note['id'])
        data.extend(dict(base, id=f'proximity-dense-{index}', altitude=-60, longitude=20) for index in range(10000))
        data.append(dict(base, id='proximity-dense-neighbor', altitude=-60.001, longitude=20))
        try:
            self.data_path.write_text(json.dumps(data))
            status, result = self.request('GET', '/api/map/sectors')
        finally:
            self.data_path.write_text(original)
        self.assertEqual(status, 200)
        group = [s for s in result['sectors'] if s['latitude'] == -60 and s['longitude'] == 20]
        self.assertEqual(len(group), 10000)
        self.assertTrue(all(s['count'] == 10001 for s in group))
        neighbor = next(s for s in result['sectors'] if s['latitude'] == -60.001 and s['longitude'] == 20)
        self.assertEqual(neighbor['count'], 10001)

    def test_map_location_capacity_returns_error_not_partial_total(self):
        original = self.data_path.read_text()
        data = json.loads(original)
        base = next(note for note in data if note['id'] == self.note['id'])
        data.extend(dict(base, id=f'proximity-capacity-{index}', altitude=-60, longitude=20) for index in range(15001))
        try:
            self.data_path.write_text(json.dumps(data))
            status, result = self.request('GET', '/api/map/sectors')
        finally:
            self.data_path.write_text(original)
        self.assertEqual(status, 503)
        self.assertNotIn('total', result)
        self.assertNotIn('sectors', result)

    def test_map_pagination_is_complete_and_excludes_invalid_coordinates(self):
        data = json.loads(self.data_path.read_text())
        base = data[-1].copy()
        for index in range(503):
            copy = dict(base, id=f'map-test-{index:04}', altitude=19.5, longitude=-101.5)
            data.append(copy)
        data.append(dict(base, id='map-invalid', altitude=91, longitude=-101.5))
        self.data_path.write_text(json.dumps(data))
        status, sectors = self.request('GET', '/api/map/sectors')
        self.assertEqual(status, 200)
        self.assertGreaterEqual(sectors['total'], 503)
        self.assertTrue(any(item['count'] >= 1 for item in sectors['sectors']))
        status, first = self.request('GET', '/api/map/incidents?west=-102&south=19&east=-101&north=20')
        self.assertEqual(status, 200)
        self.assertEqual(len(first['items']), 500)
        self.assertIsNotNone(first['nextCursor'])
        status, second = self.request('GET', '/api/map/incidents?west=-102&south=19&east=-101&north=20&after=' + first['nextCursor'])
        self.assertEqual(status, 200)
        ids = [item['id'] for item in first['items'] + second['items']]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual(sum(id.startswith('map-test-') for id in ids), 503)
        self.assertNotIn('map-invalid', ids)

    def path(self, stage):
        return f'/api/notes/{self.note["id"]}/phases/{stage}'

    def save(self, stage, data=None):
        status, response = self.request('PUT', self.path(stage), dict(expectedRevision=self.note['revision'], data=data or complete_data(stage)))
        self.assertEqual(status, 200, response)
        self.note = response

    def validate(self, stage):
        status, response = self.request('POST', self.path(stage) + '/validate', dict(expectedRevision=self.note['revision']))
        if status == 200:
            self.note = response
        return status, response

    def advance_to(self, stage):
        for previous in STAGES[:STAGES.index(stage)]:
            self.save(previous)
            self.assertEqual(self.validate(previous)[0], 200)

    def test_four_phases_are_created_in_order(self):
        self.assertEqual([p['stage'] for p in self.note['workflow']['phases']], STAGES)
        self.assertEqual(self.note['workflow']['currentStage'], 'indications')
        self.assertTrue(all(p['validatedAt'] is None for p in self.note['workflow']['phases']))
        self.assertTrue(all(
            phase['data']['indications']['signatures'] == []
            for phase in self.note['workflow']['phases']
        ))

    def test_direct_api_requests_cannot_start_or_validate_future_phases(self):
        for stage in STAGES[1:]:
            status, _ = self.request('PUT', self.path(stage), dict(expectedRevision=0, data=complete_data(stage)))
            self.assertEqual(status, 409)
            self.assertEqual(self.validate(stage)[0], 409)

    def test_creating_note_cannot_inject_completed_workflow(self):
        status, note = self.request('POST', '/api/notes', {**BASE, 'workflow': {'status': 'closed', 'currentStage': 'mitigation', 'phases': []}, 'revision': 500})
        self.assertEqual(status, 200)
        self.assertEqual(note['workflow']['currentStage'], 'indications')
        self.assertEqual(note['revision'], 0)

    def test_drafts_do_not_unlock_next_phase(self):
        self.save('indications')
        self.assertEqual(self.note['workflow']['currentStage'], 'indications')
        self.assertIsNone(self.note['workflow']['phases'][0]['validatedAt'])
        self.assertEqual(self.validate('reconnaissance')[0], 409)

    def test_incomplete_validation_returns_requirements(self):
        status, response = self.validate('indications')
        self.assertEqual(status, 422)
        self.assertGreaterEqual(len(response['issues']), 6)

    def test_phase_one_requires_pdf_iw_fields_and_lidar_reference(self):
        data = complete_data('indications')
        data['indications'] = dict(observedAt='', collectionSource='', signatures=[], alternativeCauses='')
        data['evidence'][0]['sensor'] = 'RGB'
        self.save('indications', data)
        status, response = self.validate('indications')
        self.assertEqual(status, 422)
        joined = ' '.join(response['issues'])
        self.assertIn('levantamiento comparativo', joined)
        self.assertIn('fuente LiDAR', joined)
        self.assertIn('firma de alteración', joined)
        self.assertIn('causas naturales', joined)
        self.assertIn('nube de puntos', joined)

    def test_surface_removal_requires_the_documented_depth_range(self):
        data = complete_data('indications')
        data['indications']['depressionDepthCm'] = 3
        self.save('indications', data)
        status, response = self.validate('indications')
        self.assertEqual(status, 422)
        self.assertIn('entre 5 y 15 cm', ' '.join(response['issues']))

    def test_indications_requires_the_if1200a_operating_profile_and_iw_outputs(self):
        data = complete_data('indications')
        data['indications'].update(
            platform='',
            blueUasNdaaCompliant=False,
            flightDurationMin=0,
            lidarPointRate=639999,
            terrainModelReference='',
            differentialAnalysisCompleted=False,
            actionableAlertGenerated=False,
            personnelNotExposed=False,
            coordinateTransferredToVeles=False,
        )
        self.save('indications', data)
        status, response = self.validate('indications')
        self.assertEqual(status, 422)
        joined = ' '.join(response['issues'])
        self.assertIn('IF1200A', joined)
        self.assertIn('43 minutos', joined)
        self.assertIn('640,000', joined)
        self.assertIn('modelo tridimensional', joined)
        self.assertIn('algoritmo diferencial', joined)
        self.assertIn('alerta procesable', joined)
        self.assertIn('transferencia de la coordenada', joined)

    def test_complete_flow_requires_each_validation_and_persists_it(self):
        for index, stage in enumerate(STAGES):
            self.save(stage)
            self.assertEqual(self.note['workflow']['currentStage'], stage)
            self.assertEqual(self.validate(stage)[0], 200)
            phase = self.note['workflow']['phases'][index]
            self.assertIsNotNone(phase['validatedAt'])
            self.assertEqual(phase['validatedBy'], TEST_USER)
            self.assertTrue(all(event['actor'] == TEST_USER for event in self.note['workflow']['history']))
            self.assertEqual(self.note['workflow']['currentStage'], STAGES[index + 1] if index < 3 else None)
        self.assertEqual(self.note['workflow']['status'], 'closed')
        saved = next(n for n in json.loads(self.data_path.read_text()) if n['id'] == self.note['id'])
        self.assertEqual(saved['workflow']['status'], 'closed')
        self.assertEqual(len(saved['workflow']['history']), 8)
        self.assertEqual(self.validate('mitigation')[0], 409)

    def test_validated_phases_and_general_information_are_readonly(self):
        self.advance_to('reconnaissance')
        status, _ = self.request('PUT', self.path('indications'), dict(expectedRevision=self.note['revision'], data=complete_data('indications')))
        self.assertEqual(status, 409)
        status, _ = self.request('PUT', f'/api/notes/{self.note["id"]}', {**BASE, 'title': 'Cambio', 'expectedRevision': self.note['revision']})
        self.assertEqual(status, 409)

    def test_stale_revision_cannot_overwrite_or_validate(self):
        self.save('indications')
        for method, path, body in [('PUT', self.path('indications'), dict(expectedRevision=0, data=complete_data('indications'))), ('POST', self.path('indications') + '/validate', dict(expectedRevision=0))]:
            self.assertEqual(self.request(method, path, body)[0], 409)

    def test_delete_requires_the_current_revision(self):
        original_revision = self.note['revision']
        status, updated = self.request('PUT', f'/api/notes/{self.note["id"]}', {
            **BASE,
            'content': 'Actualización concurrente',
            'expectedRevision': original_revision,
        })
        self.assertEqual(status, 200)

        stale_path = f'/api/notes/{self.note["id"]}?expectedRevision={original_revision}'
        self.assertEqual(self.request('DELETE', stale_path)[0], 409)

        current_path = f'/api/notes/{self.note["id"]}?expectedRevision={updated["revision"]}'
        self.assertEqual(self.request('DELETE', current_path)[0], 204)

    def test_area_length_is_rejected_before_persistence(self):
        status, response = self.request('POST', '/api/notes', {**BASE, 'state': 'x' * 101})
        self.assertEqual(status, 400)
        self.assertIn('100 caracteres', response['error'])

    def test_inconclusive_and_repeated_sensor_do_not_validate(self):
        self.advance_to('reconnaissance')
        data = complete_data('reconnaissance')
        data['review']['finding'] = 'inconclusive'
        self.save('reconnaissance', data)
        self.assertEqual(self.validate('reconnaissance')[0], 422)
        data['review']['finding'] = 'confirmed'
        data['evidence'][1]['sensor'] = 'GPR'
        self.save('reconnaissance', data)
        self.assertEqual(self.validate('reconnaissance')[0], 422)

    def test_reconnaissance_requires_veles_sensor_fusion_and_precision_values(self):
        self.advance_to('reconnaissance')
        data = complete_data('reconnaissance')
        data['evidence'] = [entry for entry in data['evidence'] if entry['sensor'] != 'Magnetometría']
        data['reconnaissance']['magneticReadingNt'] = 8
        data['reconnaissance']['fusionCompleted'] = False
        self.save('reconnaissance', data)
        status, response = self.validate('reconnaissance')
        self.assertEqual(status, 422)
        joined = ' '.join(response['issues'])
        self.assertIn('Magnetometría', joined)
        self.assertIn('10 nT', joined)
        self.assertIn('fusión', joined)

    def test_false_positive_closes_without_enabling_later_phases(self):
        self.advance_to('reconnaissance')
        data = complete_data('reconnaissance')
        data['review']['finding'] = 'false-positive'
        self.save('reconnaissance', data)
        self.assertEqual(self.validate('reconnaissance')[0], 200)
        self.assertEqual(self.note['workflow']['status'], 'discarded')
        self.assertIsNone(self.note['workflow']['currentStage'])
        self.assertEqual(self.validate('surveillance')[0], 409)

    def test_surveillance_certificate_and_techint_feedback_prevent_advancement(self):
        self.advance_to('surveillance')
        data = complete_data('surveillance')
        data['surveillance']['environmentDecision'] = 'hold-for-reassessment'
        self.save('surveillance', data)
        self.assertEqual(self.validate('surveillance')[0], 422)
        self.save('surveillance')
        self.assertEqual(self.validate('surveillance')[0], 200)
        data = complete_data('mitigation')
        data['mitigation']['catalogFeedbackReference'] = '  '
        self.save('mitigation', data)
        self.assertEqual(self.validate('mitigation')[0], 422)

    def test_surveillance_requires_cellular_and_eoir_outputs(self):
        self.advance_to('surveillance')
        data = complete_data('surveillance')
        data['evidence'] = []
        data['surveillance']['methods'] = ['uav-camera-feed']
        self.save('surveillance', data)
        status, response = self.validate('surveillance')
        self.assertEqual(status, 422)
        joined = ' '.join(response['issues'])
        self.assertIn('IMSI, IMEI y TMSI', joined)
        self.assertIn('EO/IR', joined)
        self.assertIn('IMSI, IMEI o TMSI', joined)

    def test_invalid_data_returns_400_without_changing_note(self):
        for data in [None, {**complete_data('indications'), 'review': None}, {**complete_data('indications'), 'soil': 'invalid'}]:
            status, _ = self.request('PUT', self.path('indications'), dict(expectedRevision=0, data=data))
            self.assertEqual(status, 400)
        _, notes = self.request('GET', '/api/notes')
        self.assertEqual(next(n for n in notes if n['id'] == self.note['id'])['revision'], 0)

    def test_concurrent_validations_only_advance_once(self):
        self.save('indications')
        path = self.path('indications') + '/validate'
        body = dict(expectedRevision=self.note['revision'])
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            responses = list(pool.map(lambda _: self.request('POST', path, body)[0], range(2)))
        self.assertEqual(sorted(responses), [200, 409])

    def test_legacy_notes_gain_empty_phases_without_losing_content(self):
        notes = json.loads(self.data_path.read_text())
        legacy = {k: v for k, v in self.note.items() if k not in ['workflow', 'revision']}
        legacy['id'] = 'legacy-note'
        notes.append(legacy)
        self.data_path.write_text(json.dumps(notes))
        status, notes = self.request('GET', '/api/notes')
        self.assertEqual(status, 200)
        old = next(n for n in notes if n['id'] == 'legacy-note')
        self.assertEqual(old['content'], BASE['content'])
        self.assertEqual(old['altitude'], BASE['altitude'])
        self.assertEqual(old['workflow']['currentStage'], 'indications')
        self.assertEqual(old['revision'], 0)


class PostgreSqlValidationBoundaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='gds-postgres-validation-boundary-')
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
            'Notes__Provider': 'PostgreSql',
            'Catalogs__Provider': 'InMemory',
            'ConnectionStrings__Postgres': (
                f'Host=127.0.0.1;Port={unavailable_port};Database=missing;'
                'Username=missing;Password=missing;Timeout=1;Command Timeout=1'
            ),
            'ConnectionStrings__IdentityAdmin': (
                f'Host=127.0.0.1;Port={unavailable_port};Database=missing;'
                'Username=identity;Password=missing;Timeout=1;Command Timeout=1'
            ),
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

    def test_validation_requires_authenticated_user_before_database_access(self):
        request = urllib.request.Request(
            self.url + '/api/notes/missing/phases/indications/validate',
            data=json.dumps({'expectedRevision': 0}).encode(),
            method='POST',
            headers={'Content-Type': 'application/json'},
        )
        with self.assertRaises(urllib.error.HTTPError) as caught:
            urllib.request.urlopen(request, timeout=10)
        status = caught.exception.code
        body = caught.exception.read()
        caught.exception.close()
        self.assertEqual(status, 401)
        error = json.loads(body)
        self.assertEqual(error['code'], 'authentication_required')
        self.assertTrue(error['error'])
        self.assertEqual(set(error), {'error', 'code'})


if __name__ == '__main__':
    unittest.main(verbosity=2)
