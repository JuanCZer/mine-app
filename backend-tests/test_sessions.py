"""Cookie/CSRF contracts over real Kestrel HTTP; SQLite is harness-only, persisted on disk."""
from pathlib import Path
import subprocess
import tempfile
import json
import os
from test_note_phases import complete_data
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'backend-tests/SessionHarness/SessionHarness.csproj'

class SessionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(['dotnet', 'build', str(PROJECT), '--nologo'], cwd=ROOT, capture_output=True, text=True)
        if result.returncode: raise RuntimeError(result.stdout + result.stderr)

    def test_production_requires_persistent_key_path(self):
        env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Production',
               'DataProtection__KeyRingPath': '', 'Notes__Provider': 'PostgreSql', 'Catalogs__Provider': 'PostgreSql'}
        result = subprocess.run(['dotnet', str(ROOT/'backend/bin/Debug/net10.0/Notes.Api.dll')],
                                cwd=ROOT, env=env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('DataProtection:KeyRingPath is required', result.stdout + result.stderr)

    def scenario(self, name):
        with tempfile.TemporaryDirectory(prefix='gds-sessions-') as directory:
            (Path(directory)/'phase.json').write_text(json.dumps(complete_data('indications')))
            result = subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT), '--', name, directory], cwd=ROOT, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

for scenario in ['anonymous', 'login', 'invalid', 'login-size', 'global-body-size', 'csrf', 'revocation', 'disabled', 'deleted', 'stamp', 'cookie', 'ownership', 'roles', 'rate', 'claims', 'secure', 'development', 'headers-production-https', 'headers-production-http', 'headers-development-https', 'headers-testing-https', 'forwarded-untrusted', 'forwarded-trusted', 'restart', 'role-change', 'limited-read', 'exception-logs', 'auth-audit', 'auth-audit-failure', 'auth-signin-failure']:
    setattr(SessionTests, 'test_' + scenario, lambda self, name=scenario: self.scenario(name))

SessionTests.test_mandatory_password_session_only_reaches_four_allowed_routes = lambda self: self.scenario('mandatory-password-routes')
SessionTests.test_profile_contract_uses_persisted_own_user_and_refreshes_role = lambda self: self.scenario('profile-contract')
SessionTests.test_auth_responses_use_no_store_and_exact_status_codes = lambda self: self.scenario('auth-response-contract')
SessionTests.test_authorization_result_handler_audits_only_authentication_required_and_generic_permission_denied = lambda self: self.scenario('authorization-result-handler-audit-ownership')
SessionTests.test_unknown_auth_target_uses_fixed_authentication_entity = lambda self: self.scenario('authorization-unknown-fixed-entity')
SessionTests.test_required_password_filter_audits_before_password_change_required = lambda self: self.scenario('mandatory-password-audit-before-response')
SessionTests.test_authorization_and_mandatory_rejections_write_exactly_one_audit_row = lambda self: self.scenario('rejection-audit-exactly-once')
SessionTests.test_health_invokes_injected_readiness_returns_503_without_key_ids = lambda self: self.scenario('health-readiness-unavailable')
SessionTests.test_stepup_is_reusable_without_renewal_and_invalidates_with_actor_security_state = lambda self: self.scenario('stepup-lifecycle')
SessionTests.test_oversized_reauth_body_never_hash = lambda self: self.scenario('reauth-body-size')
SessionTests.test_129_rune_reauth_never_hash = lambda self: self.scenario('reauth-rune-limit')
SessionTests.test_reauth_csrf_returns_exact_code = lambda self: self.scenario('reauth-csrf')
SessionTests.test_reauth_commits_quota_before_hasher_and_rejection_audit = lambda self: self.scenario('reauth-quota-order')
SessionTests.test_reauth_uses_no_store_envelope_and_preserves_normal_session = lambda self: self.scenario('reauth-session-preservation')
SessionTests.test_change_password_csrf_returns_exact_code = lambda self: self.scenario('change-password-csrf')
SessionTests.test_oversized_change_password_body_never_hash = lambda self: self.scenario('change-password-body-size')
SessionTests.test_129_rune_current_password_never_hash = lambda self: self.scenario('change-password-current-rune-limit')
SessionTests.test_new_password_enforces_14_15_128_129_rune_boundaries = lambda self: self.scenario('change-password-new-rune-boundaries')
SessionTests.test_change_password_returns_exact_current_confirmation_and_reuse_codes = lambda self: self.scenario('change-password-exact-codes')
SessionTests.test_change_password_policy_errors_use_envelope_and_no_store = lambda self: self.scenario('change-password-policy-errors')
SessionTests.test_change_password_clears_mandatory_flag_rotates_stamps_and_cookies = lambda self: self.scenario('change-password-success-state')

if __name__ == '__main__': unittest.main()
