"""Static contract checks for the self-contained PostgreSQL deliverable.

These tests deliberately do not claim that PostgreSQL executed the script. They
catch accidental file splitting, unsafe shortcuts, and drift in the required
domain and import contracts when a database service is not available locally.
"""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
DATABASE = ROOT / "backend" / "Database"
SCRIPT = DATABASE / "apply.sql"


class PostgreSqlSchemaContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.sql = SCRIPT.read_text()

    def test_database_has_two_sql_artifacts_and_apply_is_self_contained(self) -> None:
        sql_files = sorted(path.relative_to(DATABASE) for path in DATABASE.rglob("*.sql"))
        self.assertEqual([Path("apply.sql"), Path("user-administration.sql")], sql_files)
        self.assertNotRegex(self.sql, r"(?m)^\s*\\")
        self.assertEqual(1, len(re.findall(r"(?im)^\s*BEGIN;\s*$", self.sql)))
        self.assertEqual(1, len(re.findall(r"(?im)^\s*COMMIT;\s*$", self.sql)))
        self.assertLess(self.sql.find("BEGIN;"), self.sql.find("-- 1. Identity"))
        self.assertGreater(self.sql.rfind("COMMIT;"), self.sql.find("-- 10. Import reconciliation report"))

    def test_sections_are_embedded_in_required_order(self) -> None:
        expected = [
            "-- 1. Identity",
            "-- 2. Catalogs",
            "-- 3. Incidents and phases",
            "-- 4. Evidence, validations, and audit",
            "-- 5. Import support",
            "-- 6. Workflow guards",
            "-- 7. Catalog seeds",
            "-- 8. Database roles and grants",
            "-- 9. Schema integrity checks",
            "-- 10. Import reconciliation report",
        ]
        positions = [self.sql.find(section) for section in expected]
        self.assertTrue(all(position >= 0 for position in positions))
        self.assertEqual(positions, sorted(positions))

    def test_schema_covers_identity_incidents_four_phases_and_audit(self) -> None:
        required_tables = [
            'public."AspNetUsers"',
            'public."AspNetRoles"',
            "app.incidents",
            "app.incident_phases",
            "app.phase_reviews",
            "app.phase_indications",
            "app.phase_reconnaissance",
            "app.phase_surveillance",
            "app.phase_mitigation",
            "app.phase_indication_signatures",
            "app.phase_radio_technologies",
            "app.phase_surveillance_methods",
            "app.phase_evidence",
            "app.phase_validations",
            "app.incident_events",
            "app.audit_events",
            "app.import_batches",
            "app.legacy_records",
            "app.legacy_batch_records",
            "app.legacy_id_mappings",
        ]
        for table in required_tables:
            self.assertRegex(
                self.sql,
                rf"(?i)CREATE TABLE IF NOT EXISTS\s+{re.escape(table)}(?:\s|\()",
            )

        for stage in ["indications", "reconnaissance", "surveillance", "mitigation"]:
            self.assertIn(f"CHECK (stage_code = '{stage}')", self.sql)
        self.assertIn("UNIQUE (incident_id, stage_code)", self.sql)
        self.assertIn("UNIQUE (phase_id, client_id)", self.sql)

    def test_import_storage_preserves_original_and_enforces_retry_identity(self) -> None:
        self.assertIn("original_payload_json text NOT NULL", self.sql)
        self.assertIn("UNIQUE (source, source_record_id, source_hash)", self.sql)
        self.assertRegex(self.sql, r"source_sha256\s+char\(64\)\s+NOT NULL")
        self.assertRegex(self.sql, r"source_hash\s+char\(64\)\s+NOT NULL")
        self.assertIn("legacy_user_id text", self.sql)
        self.assertIn("reconciliation_required", self.sql)

    def test_import_batch_membership_is_occurrence_based_and_backfilled(self) -> None:
        self.assertRegex(
            self.sql,
            r"(?is)CREATE TABLE IF NOT EXISTS app\.legacy_batch_records\s*\(.*?"
            r"PRIMARY KEY\s*\(batch_id,\s*source_index\)",
        )
        bridge = self.sql[
            self.sql.index("CREATE TABLE IF NOT EXISTS app.legacy_batch_records"):
            self.sql.index("CREATE TABLE IF NOT EXISTS app.legacy_id_mappings")
        ]
        self.assertNotRegex(bridge, r"(?i)UNIQUE\s*\(\s*(?:batch_id\s*,\s*)?legacy_record_id")
        for column in ["disposition", "error_code", "error_detail", "processed_at"]:
            self.assertRegex(bridge, rf"(?i)\b{column}\b")
        self.assertRegex(
            bridge,
            r"(?is)disposition IN \('pending','imported'\) AND error_code IS NULL",
        )
        self.assertIn("row_number() OVER", self.sql)
        self.assertIn("ON CONFLICT (batch_id, source_index) DO NOTHING", self.sql)

    def test_legacy_membership_backfill_normalizes_errors_for_current_constraint(self) -> None:
        backfill = self.sql[
            self.sql.index("WITH ranked AS ("):
            self.sql.index("-- A previous importer could mark a batch complete")
        ]
        self.assertRegex(
            backfill,
            r"(?is)CASE\s+WHEN disposition IN \('pending','imported'\) THEN NULL\s+"
            r"WHEN disposition='reconciliation_required' THEN\s+"
            r"COALESCE\(NULLIF\(btrim\(error_code\), ''\), "
            r"'legacy-backfill-reconciliation-required'\)\s+"
            r"WHEN disposition='rejected' THEN\s+"
            r"COALESCE\(NULLIF\(btrim\(error_code\), ''\), "
            r"'legacy-backfill-rejected'\)\s+END AS error_code",
        )
        self.assertRegex(
            backfill,
            r"(?is)CASE\s+WHEN disposition IN \('pending','imported'\) THEN NULL\s+"
            r"ELSE error_detail\s+END AS error_detail",
        )

    def test_reconciliation_counts_batch_occurrences_through_membership(self) -> None:
        report = self.sql[self.sql.index("-- 10. Import reconciliation report"):]
        self.assertIn("app.legacy_batch_records", report)
        self.assertRegex(
            report,
            r"(?is)FROM app\.import_batches b\s+"
            r"LEFT JOIN app\.legacy_batch_records br ON br\.batch_id=b\.id\s+"
            r"LEFT JOIN app\.legacy_records r ON r\.id=br\.legacy_record_id",
        )
        self.assertIn("br.disposition='imported'", report)
        self.assertIn("br.disposition='pending'", report)

    def test_workflow_guards_are_database_enforced(self) -> None:
        for expected in [
            "FOR UPDATE OF i",
            "trg_incident_initialize_phases",
            "trg_incident_complete_phases",
            "trg_validation_guard",
            "previous workflow phases must be validated first",
            "validated phase is immutable",
            "physical deletion of incidents is forbidden",
            "trg_append_only",
        ]:
            self.assertIn(expected, self.sql)
        self.assertIn("app.assert_phase_mutable(OLD.phase_id)", self.sql)
        self.assertIn("app.assert_phase_mutable(NEW.phase_id)", self.sql)

    def test_final_checks_cover_integrity_and_import_reconciliation(self) -> None:
        for expected in [
            "phase_count",
            "workflowStages",
            "missing constraints",
            "linked_records",
            "pending_records",
            "reconciliation_required",
            "mapping points at no incident",
        ]:
            self.assertIn(expected, self.sql)

        self.assertRegex(
            self.sql,
            r"m\.legacy_record_id=r\.id\s+AND m\.entity_type='incident'\s+"
            r"AND m\.mapping_status='mapped'\s+"
            r"AND \(m\.target_id IS NOT NULL OR m\.target_public_id IS NOT NULL\)",
        )
        self.assertIn("m.mapping_status IN ('pending','rejected')", self.sql)

    def test_final_mapping_check_resolves_each_identifier_and_rejects_crossed_targets(self) -> None:
        mapping_check = self.sql[
            self.sql.index("-- Must be empty: mapping points at no incident."):
            self.sql.index("-- Must be empty: incident mappings still awaiting resolution")
        ]
        self.assertIn("LEFT JOIN app.incidents i_by_id ON i_by_id.id=m.target_id", mapping_check)
        self.assertIn(
            "LEFT JOIN app.incidents i_by_public ON i_by_public.public_id=m.target_public_id",
            mapping_check,
        )
        for predicate in [
            "m.target_id IS NULL AND m.target_public_id IS NULL",
            "m.target_id IS NOT NULL AND i_by_id.id IS NULL",
            "m.target_public_id IS NOT NULL AND i_by_public.id IS NULL",
            "i_by_id.id IS DISTINCT FROM i_by_public.id",
        ]:
            self.assertIn(predicate, mapping_check)
        self.assertNotRegex(mapping_check, r"(?i)JOIN app\.incidents\s+i\s+ON\s+i\.id=.*\s+OR\s+i\.public_id")

    def test_runtime_can_read_every_catalog_partition(self) -> None:
        catalog_tables = [
            "cat_mexico_states", "cat_soils", "cat_seasons", "cat_risk_levels",
            "cat_sensors", "cat_platforms", "cat_indication_sources",
            "cat_indication_signatures", "cat_radio_technologies",
            "cat_surveillance_methods", "cat_surveillance_deployments",
            "cat_human_activities", "cat_environment_decisions",
            "cat_protection_levels", "cat_neutralization_results",
            "cat_route_results", "cat_findings", "cat_environment_reviews",
            "cat_workflow_statuses", "workflow_stages",
        ]
        grant_start = self.sql.index("GRANT SELECT ON\n    app.catalog_options")
        grant_end = self.sql.index("TO gds_app_runtime;", grant_start)
        grant = self.sql[grant_start:grant_end]
        for table in catalog_tables:
            self.assertIn(f"app.{table}", grant)

    def test_reapplying_unchanged_seeds_does_not_touch_catalog_versions(self) -> None:
        seed_section = self.sql[
            self.sql.index("-- 7. Catalog seeds"):
            self.sql.index("-- 8. Database roles and grants")
        ]
        self.assertEqual(3, seed_section.count("ON CONFLICT (catalog_key, code) DO UPDATE"))
        self.assertEqual(3, seed_section.count("IS DISTINCT FROM"))

    def test_catalog_changes_are_audited_with_actor_context_and_before_after_values(self) -> None:
        for expected in [
            "app.audit_catalog_option_change",
            "trg_catalog_options_audit",
            "catalog.option.insert",
            "catalog.option.update",
            "databaseRole",
            "'before'",
            "'after'",
        ]:
            self.assertIn(expected, self.sql)

    def test_documented_psql_execution_stops_on_first_error(self) -> None:
        documents = [
            DATABASE / "README.md",
            ROOT / "docs" / "data" / "postgresql-schema.md",
            ROOT / "docs" / "runbooks" / "postgresql-cutover.md",
        ]
        commands = [
            line
            for document in documents
            for line in document.read_text().splitlines()
            if "psql " in line and "apply.sql" in line
        ]
        self.assertGreaterEqual(len(commands), 1)
        for command in commands:
            self.assertIn("--set ON_ERROR_STOP=on", command, command)

    def test_direct_sql_is_the_only_schema_change_mechanism(self) -> None:
        backend_sources = "\n".join(
            path.read_text()
            for pattern in ("*.cs", "*.csproj")
            for path in (ROOT / "backend").rglob(pattern)
            if not {"bin", "obj"} & set(path.parts)
        )
        self.assertNotIn('__EF' + 'MigrationsHistory', self.sql)
        self.assertNotIn("gds_app_" + "migrator", self.sql)
        # EF is permitted as Identity storage, but SQL remains the sole schema
        # owner: prohibit schema-creation/migration calls in application code.
        self.assertNotRegex(backend_sources, r"\.\s*(?:EnsureCreated|EnsureDeleted|Migrate)(?:Async)?\s*\(")
        self.assertNotRegex(backend_sources, r"\bMigrationsAssembly\s*\(")
        self.assertFalse(any((ROOT / "backend").rglob("*ModelSnapshot.cs")))
        self.assertFalse((ROOT / "backend" / "Migrations").exists())
        self.assertIn("gds_db_deployer", self.sql)

    def test_sql_does_not_create_credentials_or_destructive_rollback(self) -> None:
        self.assertNotRegex(self.sql, r"(?i)\bCREATE\s+USER\b")
        self.assertNotRegex(self.sql, r"(?i)\bCREATE\s+ROLE\b[^;]*\bLOGIN\b")
        self.assertNotRegex(self.sql, r"(?i)\bPASSWORD\s+['\"]")
        self.assertNotRegex(self.sql, r"(?i)\bDROP\s+(TABLE|SCHEMA|DATABASE)\b")


if __name__ == "__main__":
    unittest.main(verbosity=2)
