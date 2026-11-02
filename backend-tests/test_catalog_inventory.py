"""Checks that record fixtures only use codes seeded by the real PostgreSQL script."""
from pathlib import Path
import re
import unittest

from test_legacy_import import NOTES_FIXTURE
from test_note_phases import STAGES, complete_data


ROOT = Path(__file__).resolve().parents[1]
SQL = (ROOT / "backend" / "Database" / "apply.sql").read_text(encoding="utf-8")


def seed_codes() -> set[tuple[str, str]]:
    pairs = {
        (match.group("key"), match.group("code").replace("''", "'"))
        for match in re.finditer(
            r"\('(?P<key>[A-Za-z]+)',\s*md5\('[^']+'\)::uuid,\s*'(?P<code>(?:''|[^'])+)'",
            SQL,
        )
    }
    for key, start_marker, end_marker in [
        ("sensors", "FROM (VALUES\n    ('LiDAR'", ") AS seed(code, label, sort_order, stages)"),
        (
            "surveillanceMethods",
            "FROM (VALUES\n    ('imsi-imei-tmsi-capture'",
            ") AS seed(code, label, sort_order)",
        ),
    ]:
        start = SQL.index(start_marker)
        end = SQL.index(end_marker, start)
        for code in re.findall(r"(?m)^\s*\('((?:''|[^'])+)'", SQL[start:end]):
            pairs.add((key, code.replace("''", "'")))
    return pairs


def phase_selections(stage: str, data: dict) -> set[tuple[str, str]]:
    selections = {
        ("soils", data.get("soil", "")),
        ("seasons", data.get("season", "")),
        ("findings", data.get("review", {}).get("finding", "")),
        ("environmentReviews", data.get("review", {}).get("environment", "")),
    }
    selections.update(("sensors", item.get("sensor", "")) for item in data.get("evidence", []))
    if stage == "indications":
        intake = data.get("indications", {})
        selections.update(
            {
                ("platforms", intake.get("platform", "")),
                ("indicationSources", intake.get("collectionSource", "")),
            }
        )
        selections.update(("indicationSignatures", code) for code in intake.get("signatures", []))
    elif stage == "reconnaissance":
        intake = data.get("reconnaissance", {})
        selections.update(
            {
                ("riskLevels", intake.get("probability", "")),
                ("riskLevels", intake.get("priority", "")),
            }
        )
    elif stage == "surveillance":
        intake = data.get("surveillance", {})
        selections.update(("radioTechnologies", code) for code in intake.get("radioTechnologies", []))
        selections.update(("surveillanceMethods", code) for code in intake.get("methods", []))
        selections.update(
            {
                ("surveillanceDeployments", intake.get("deployment", "")),
                ("humanActivities", intake.get("humanActivity", "")),
                ("environmentDecisions", intake.get("environmentDecision", "")),
            }
        )
    elif stage == "mitigation":
        intake = data.get("mitigation", {})
        selections.update(
            {
                ("platforms", intake.get("platform", "")),
                ("protectionLevels", intake.get("protectionLevel", "")),
                ("neutralizationResults", intake.get("neutralizationResult", "")),
                ("routeResults", intake.get("routeResult", "")),
            }
        )
    return {(key, code) for key, code in selections if code}


class CatalogInventoryTests(unittest.TestCase):
    def test_surveillance_methods_cover_every_process_listed_by_the_pdf(self) -> None:
        expected = {
            "imsi-imei-tmsi-capture",
            "target-import-export",
            "precision-mapping",
            "silent-call-df",
            "mass-sms-transmission",
            "imsi-imei-alert",
            "operations-center-access",
            "selective-signal-block",
            "online-offline-mapping",
            "uav-camera-feed",
            "gsma-tac",
            "target-api",
            "ground-refinement-realtime",
            "radio-stack-configuration",
        }
        actual = {code for key, code in seed_codes() if key == "surveillanceMethods"}
        self.assertEqual(expected, actual)

    def test_current_workflow_fixtures_are_covered_by_apply_sql_seeds(self) -> None:
        available = seed_codes()
        used = set().union(*(phase_selections(stage, complete_data(stage)) for stage in STAGES))
        self.assertEqual(set(), used - available)

    def test_existing_legacy_note_fixture_codes_are_mapped_before_cutover(self) -> None:
        available = seed_codes()
        used: set[tuple[str, str]] = set()
        for note in NOTES_FIXTURE:
            for phase in note.get("workflow", {}).get("phases", []):
                used.update(phase_selections(phase.get("stage", ""), phase.get("data", {})))
        # This fixture is intentionally rejected by the legacy analysis. Keeping the
        # explicit allowlist here makes every additional unmapped code fail the cutover gate.
        self.assertEqual({("soils", "soil-not-in-database")}, used - available)


if __name__ == "__main__":
    unittest.main(verbosity=2)
