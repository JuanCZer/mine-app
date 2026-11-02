"""Backend user-administration scenarios owned by UserAdministrationHarness."""

from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "backend-tests/UserAdministrationHarness/UserAdministrationHarness.csproj"


class UserAdministrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(
            ["dotnet", "build", str(PROJECT), "--nologo"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)

    def scenario(self, name):
        with tempfile.TemporaryDirectory(prefix="gds-user-administration-") as directory:
            result = subprocess.run(
                ["dotnet", "run", "--no-build", "--project", str(PROJECT), "--", name, str(Path(directory) / "identity.db")],
                cwd=ROOT,
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_admin_password_reset_http_requires_authorization_stepup_and_idempotency(self):
        self.scenario("admin-reset-http-contract")

    def test_harness_rejects_unknown_scenario_without_running_product(self):
        result = subprocess.run(
            ["dotnet", "run", "--no-build", "--project", str(PROJECT), "--", "unknown"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unknown user-administration scenario: unknown", result.stdout + result.stderr)
        self.assertNotIn("PASS", result.stdout + result.stderr)

    def test_identity_model_maps_management_fields(self):
        self.scenario("identity-model-management-fields")

    def test_rate_limit_key_array_binds_indexed_environment_entries(self):
        self.scenario("rate-limit-key-array-binds-indexed-environment-entries")

    def test_rate_limit_key_list_rejects_null_incomplete_duplicate_and_invalid_entries(self):
        self.scenario("rate-limit-key-list-rejects-null-incomplete-duplicate-and-invalid-entries")

    def test_configuration_rejects_local_current_or_active_selector(self):
        self.scenario("configuration-rejects-local-current-or-active-selector")

    def test_readiness_requires_exactly_one_active_and_every_retained_secret(self):
        self.scenario("readiness-requires-exactly-one-active-and-every-retained-secret")

    def test_startup_requires_identity_admin_pool_and_complete_hmac_keys(self):
        self.scenario("startup-requires-identity-admin-pool-and-complete-hmac-keys")

    def test_rejection_record_requires_stable_code_allowlisted_nonempty_entity_rejected_outcome_and_bounded_metadata(self):
        self.scenario("rejection-record-requires-stable-code-allowlisted-nonempty-entity-rejected-outcome-and-bounded-metadata")

    def test_rejection_audit_persists_exact_top_level_columns_empty_changes_and_security_context_json(self):
        self.scenario("rejection-audit-persists-exact-top-level-columns-empty-changes-and-security-context-json")

    def test_rejection_audit_writes_in_its_own_transaction(self):
        self.scenario("rejection-audit-writes-in-its-own-transaction")

    def test_rejection_audit_failure_preserves_original_rejection(self):
        self.scenario("rejection-audit-failure-preserves-original-rejection")

    def test_rejection_audit_never_accepts_body_password_cookie_csrf_or_connection_data(self):
        self.scenario("rejection-audit-never-accepts-body-password-cookie-csrf-or-connection-data")

    def test_distributed_rate_limit_direct_service_scenarios_are_registered_without_http(self):
        runner = (ROOT / "backend-tests/user_administration_postgres_runner.py").read_text(encoding="utf-8")
        harness = (ROOT / "backend-tests/UserAdministrationHarness/Program.cs").read_text(encoding="utf-8")
        names = (
            "test_rate_limit_attempt_commits_before_service_returns",
            "test_429_attempt_commits_before_service_returns",
            "test_rate_limit_attempt_survives_synthetic_later_rollback",
            "test_rate_limit_database_failure_returns_service_unavailable",
            "test_expired_buckets_are_deleted_in_bounded_batches",
            "test_rate_limit_rotation_is_shared_ordered_and_fails_closed",
            "test_rate_limit_cleanup_state_transition_order_does_not_deadlock",
        )
        for name in names:
            self.assertRegex(runner, rf"def {re.escape(name)}\(")
        rate_limit_harness = harness.split('case "rate-limit-attempt-commits-before-service-returns"', 1)[1]
        rate_limit_harness = rate_limit_harness.split('case "admin-directory-authorization"', 1)[0]
        self.assertNotIn("HttpClient", rate_limit_harness)
        self.assertNotIn("/api/", rate_limit_harness)

    def test_rate_limiter_contract_and_hosted_cleanup_are_registered_in_identity_di(self):
        identity = (ROOT / "backend/IdentityAdministration/IdentityRateLimiter.cs").read_text(encoding="utf-8")
        cleanup = (ROOT / "backend/IdentityAdministration/IdentityRateLimitCleanupService.cs").read_text(encoding="utf-8")
        registration = (ROOT / "backend/Auth/IdentityServiceCollectionExtensions.cs").read_text(encoding="utf-8")
        self.assertIn("Task<IdentityRateLimitDecision> ConsumeAsync", identity)
        self.assertIn("Task<int> DeleteExpiredBatchAsync", identity)
        self.assertIn("BackgroundService", cleanup)
        self.assertIn("AddSingleton<IIdentityRateLimiter, IdentityRateLimiter>()", registration)
        self.assertIn("AddHostedService<IdentityRateLimitCleanupService>()", registration)

    def test_opportunistic_cleanup_locks_all_state_before_bucket_access(self):
        identity = (ROOT / "backend/IdentityAdministration/IdentityRateLimiter.cs").read_text(encoding="utf-8")
        consume = identity[
            identity.index("public async Task<IdentityRateLimitDecision> ConsumeAsync"):
            identity.index("public async Task<int> DeleteExpiredBatchAsync")
        ]
        self.assertIn("LockAllStateAsync", consume)
        self.assertLess(consume.index("LockAllStateAsync"), consume.index("DeleteExpiredRowsAsync"))
        self.assertLess(consume.index("LockAllStateAsync"), consume.index("MaterializeCandidateAsync"))

    def test_admin_directory_returns_authentication_required_and_permission_denied(self):
        self.scenario("admin-directory-authorization")

    def test_admin_directory_registered_http_pipeline_enforces_auth_methods_and_no_csrf(self):
        program = (ROOT / "backend/Program.cs").read_text(encoding="utf-8")
        registration = (ROOT / "backend/Auth/IdentityServiceCollectionExtensions.cs").read_text(encoding="utf-8")
        self.assertIn("app.MapIdentityAdministrationEndpoints();", program)
        self.assertIn(
            "services.AddSingleton<IIdentityAdministrationStore, IdentityAdministrationStore>();",
            registration,
        )
        self.assertNotIn(
            "TryAddSingleton<IIdentityAdministrationStore, IdentityAdministrationStore>",
            registration,
        )
        self.scenario("admin-directory-registered-http-pipeline")

    def test_admin_directory_defaults_to_page_1_and_page_size_25(self):
        self.scenario("admin-directory-default-pagination")

    def test_admin_directory_validates_filters_and_bounded_pagination(self):
        self.scenario("admin-directory-validation")

    def test_admin_directory_success_and_errors_use_no_store_and_stable_envelope(self):
        self.scenario("admin-directory-no-store-envelope")

    def test_admin_directory_escapes_wildcards_and_orders_by_normalized_name_then_id(self):
        self.scenario("admin-directory-search-and-order")

    def test_admin_directory_success_audits_stable_entity_without_results(self):
        self.scenario("admin-directory-success-audit")

    def test_admin_directory_validation_rejection_is_audited_once_with_stable_entity(self):
        self.scenario("admin-directory-validation-audit")

    def test_admin_directory_real_postgres_path_is_runner_owned(self):
        runner = (ROOT / "backend-tests/user_administration_postgres_runner.py").read_text(encoding="utf-8")
        harness = (ROOT / "backend-tests/UserAdministrationHarness/Program.cs").read_text(encoding="utf-8")
        self.assertIn("def test_admin_directory_uses_identity_admin_and_records_sanitized_audit(", runner)
        self.assertIn('"identity-admin-real-directory"', runner)
        self.assertIn('case "identity-admin-real-directory"', harness)
        for evidence in (
            "alpha.percent%", "bravo_under_", r"charlie\slash", "Águila",
            '"NormalizedUserName"=NULL', "tie.first", "tie.second",
        ):
            self.assertIn(evidence, runner)
        self.assertIn("page.Total == 7", harness)
        self.assertIn("real directory order must be exact NormalizedUserName then UUID", harness)

    def test_admin_stepup_filter_audits_missing_or_expired_stepup_once(self):
        self.scenario("admin-stepup-filter-missing-expired")

    def test_admin_stepup_filter_does_not_duplicate_authorization_audit(self):
        self.scenario("admin-stepup-filter-audit-ownership")

    def test_create_csrf_returns_exact_code(self):
        self.scenario("create-csrf-returns-exact-code")

    def test_create_validates_fields_uuid_v4_and_temporary_password(self):
        self.scenario("create-validates-fields-uuid-v4-and-temporary-password")

    def test_idempotency_hash_matches_shared_vector(self):
        self.scenario("idempotency-hash-matches-shared-vector")

    def test_create_headers_are_exact_for_original_and_replay(self):
        self.scenario("create-headers-are-exact-for-original-and-replay")

    def test_create_two_stores_in_one_process_replay_identical_protected_response(self):
        self.scenario("create-two-stores-in-one-process-replay-identical-protected-response")

    def test_same_idempotency_uuid_is_isolated_by_actor(self):
        self.scenario("same-idempotency-uuid-is-isolated-by-actor")

    def test_same_actor_key_with_different_canonical_payload_returns_idempotency_key_reused(self):
        self.scenario("same-actor-key-with-different-canonical-payload-returns-idempotency-key-reused")

    def test_distinct_creation_intents_generate_different_temporary_passwords(self):
        self.scenario("distinct-creation-intents-generate-different-temporary-passwords")

    def test_expired_receipt_becomes_tombstone_without_duplicate(self):
        self.scenario("expired-receipt-becomes-tombstone-without-duplicate")

    def test_receipt_cleanup_nulls_ciphertext_and_preserves_row(self):
        self.scenario("receipt-cleanup-nulls-ciphertext-and-preserves-row")

    def test_create_rejections_are_best_effort_audited_without_secrets(self):
        self.scenario("create-rejections-are-best-effort-audited-without-secrets")

    def test_receipt_plaintext_never_reaches_db_log_or_audit(self):
        self.scenario("receipt-plaintext-never-reaches-db-log-or-audit")

    def test_role_status_csrf_returns_exact_code(self):
        self.scenario("role-status-csrf-returns-exact-code")

    def test_management_version_rejects_json_numbers_and_preserves_over_2pow53(self):
        self.scenario("management-version-rejects-json-numbers-and-preserves-over-2pow53")

    def test_role_status_return_stable_errors_for_missing_stale_self_and_last_admin(self):
        self.scenario("role-status-return-stable-errors-for-missing-stale-self-and-last-admin")

    def test_noop_runs_guards_without_version_or_success_audit(self):
        self.scenario("noop-runs-guards-without-version-or-success-audit")

    def test_admin_mutation_quota_survives_stale_conflict(self):
        self.scenario("admin-mutation-quota-survives-stale-conflict")

    def test_mutation_rejections_are_best_effort_audited_without_changing_errors(self):
        self.scenario("mutation-rejections-are-best-effort-audited-without-changing-errors")

    def test_same_version_has_single_writer_and_rotates_both_stamps(self):
        self.scenario("same-version-has-single-writer-and-rotates-both-stamps")


if __name__ == "__main__":
    unittest.main()
