"""Integration tests for mandatory note-phase validation; uses an isolated data file."""
import concurrent.futures
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
        env = {**os.environ, 'Notes__DataPath': str(cls.data_path), 'ASPNETCORE_ENVIRONMENT': 'Development'}
        cls.process = subprocess.Popen(['dotnet', 'run', '--no-build', '--project', str(ROOT / 'backend'), '--', '--urls', cls.url], env=env, stdout=cls.log, stderr=subprocess.STDOUT)
        for _ in range(100):
            try:
                urllib.request.urlopen(cls.url + '/api/health', timeout=0.2).close()
                return
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
            with urllib.request.urlopen(request, timeout=10) as response:
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
            self.assertEqual(phase['validatedBy'], 'Revisor de prueba')
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


if __name__ == '__main__':
    unittest.main(verbosity=2)
