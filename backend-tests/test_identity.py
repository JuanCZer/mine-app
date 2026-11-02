"""Identity behavior with a disk-backed SQLite test harness; no PostgreSQL claim.

Mutation targets: lost persistence, missing active/lockout checks, distinct public
failure results, seeded credentials, and password/role bypass in provisioning.
"""
from pathlib import Path
import os
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'backend-tests/IdentityHarness/IdentityHarness.csproj'


class IdentityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(['dotnet', 'build', str(PROJECT), '--nologo'], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)

    def scenario(self, name):
        with tempfile.TemporaryDirectory(prefix='gds-identity-') as directory:
            env = {k: v for k, v in os.environ.items() if k != 'GDS_PROVISION_PASSWORD'}
            result = subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT), '--', name, str(Path(directory)/'identity.db')], env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_user_survives_recreated_provider(self): self.scenario('persistence')
    def test_inactive_account_rejected(self): self.scenario('inactive')
    def test_unknown_and_wrong_password_share_public_result(self): self.scenario('uniform')
    def test_lockout_persists_and_blocks_correct_password(self): self.scenario('lockout')
    def test_administrative_provisioning_uses_identity_and_explicit_role(self): self.scenario('provision')

    def test_provision_rejects_14_characters(self): self.scenario('password-short')
    def test_provision_accepts_phrase_without_composition(self): self.scenario('password-phrase')
    def test_provision_preserves_spaces_and_unicode(self): self.scenario('password-unicode')
    def test_provision_uses_the_shared_128_unicode_scalar_maximum(self): self.scenario('provision-password-maximum')
    def test_runtime_updates_only_authentication_columns(self): self.scenario('runtime-boundary')
    def test_ineligible_accounts_pay_password_verification_cost(self): self.scenario('credential-cost')
    def test_password_length_is_bounded_before_hashing(self): self.scenario('password-maximum')
    def test_concurrent_failure_snapshots_cannot_bypass_lockout(self): self.scenario('concurrent-lockout')
    def test_session_rejects_zero_multiple_or_noncanonical_roles(self): self.scenario('session-role-cardinality')
    def test_change_password_policy_preserves_unicode_scalar_values(self): self.scenario('change-password-policy-runes')
    def test_bootstrap_factory_requires_deploy_url_with_verify_full_tls(self): self.scenario('bootstrap-config')

    def test_runtime_grants_remove_table_update_before_limited_column_grant(self):
        sql = (ROOT/'backend/Database/apply.sql').read_text()
        revoke = re.search(r'REVOKE UPDATE ON public\."AspNetUsers" FROM gds_app_runtime;', sql)
        self.assertIsNotNone(revoke, 'upgrades must revoke existing table-wide UPDATE')
        self.assertNotRegex(sql, r'GRANT UPDATE ON public\."AspNetUsers" TO gds_app_runtime;')
        grant = re.search(r'GRANT UPDATE\s*\(([^)]+)\) ON public\."AspNetUsers" TO gds_app_runtime;', sql)
        self.assertIsNotNone(grant, 'runtime requires an explicit column grant')
        self.assertLess(revoke.start(), grant.start())
        self.assertEqual(set(re.findall(r'"([^"]+)"', grant.group(1))), {
            'PasswordHash', 'SecurityStamp', 'ConcurrencyStamp', 'AccessFailedCount', 'LockoutEnd', 'UpdatedAt'})

    def test_no_demo_account_or_fixed_password_in_runtime_code_or_sql(self):
        # Explicit security regression guard required by the task brief.
        for path in (ROOT/'backend').rglob('*'):
            if path.suffix not in ('.cs', '.sql') or {'bin', 'obj'} & set(path.parts):
                continue
            text = path.read_text()
            self.assertNotIn('demo-user', text, str(path))
            self.assertNotIn('Demo123!', text, str(path))
            self.assertNotRegex(text, r'(?i)PasswordHash\s*=\s*"[^\"]+"', str(path))


if __name__ == '__main__': unittest.main()
