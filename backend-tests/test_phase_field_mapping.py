"""Contract checks for the PDF → UI → DTO → SQL traceability matrix."""
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
MAPPING = (ROOT / "docs" / "data" / "phase-field-mapping.md").read_text(encoding="utf-8")
SQL = (ROOT / "backend" / "Database" / "apply.sql").read_text(encoding="utf-8")


PHASE_FIELDS = {
    "indications": {
        "platform": "platform_code",
        "blueUasNdaaCompliant": "blue_uas_ndaa_compliant",
        "flightDurationMin": "flight_duration_min",
        "lidarPointRate": "lidar_point_rate",
        "payloadLb": "payload_lb",
        "missionSpeedMph": "mission_speed_mph",
        "windKnots": "wind_knots",
        "terrainModelReference": "terrain_model_reference",
        "differentialAnalysisCompleted": "differential_analysis_completed",
        "surfaceVariationMm": "surface_variation_mm",
        "actionableAlertGenerated": "actionable_alert_generated",
        "personnelNotExposed": "personnel_not_exposed",
        "coordinateTransferredToVeles": "coordinate_transferred_to_veles",
        "observedAt": "observed_at",
        "collectionSource": "collection_source_code",
        "signatures": "phase_indication_signatures",
        "alternativeCauses": "alternative_causes",
        "depressionDepthCm": "depression_depth_cm",
    },
    "reconnaissance": {
        "magneticReadingNt": "magnetic_reading_nt",
        "gprDepthCm": "gpr_depth_cm",
        "rtkAccuracyCm": "rtk_accuracy_cm",
        "localizationAccuracyCm": "localization_accuracy_cm",
        "probability": "probability_code",
        "priority": "priority_code",
        "georeferencedPointReference": "georeferenced_point_reference",
        "riskMapReference": "risk_map_reference",
        "aiClassificationReference": "ai_classification_reference",
        "fusionCompleted": "fusion_completed",
    },
    "surveillance": {
        "observationStartedAt": "observation_started_at",
        "observationEndedAt": "observation_ended_at",
        "radioTechnologies": "phase_radio_technologies",
        "methods": "phase_surveillance_methods",
        "deployment": "deployment_code",
        "coveredChannels": "covered_channels",
        "duplicatedChannelConfiguration": "duplicated_channel_configuration",
        "activeDeviceCount": "active_device_count",
        "humanActivity": "human_activity_code",
        "environmentDecision": "environment_decision_code",
    },
    "mitigation": {
        "coordinateReceived": "coordinate_received",
        "platform": "platform_code",
        "protectionLevel": "protection_level_code",
        "personnelProtectionConfirmed": "personnel_protection_confirmed",
        "equipmentProtectionConfirmed": "equipment_protection_confirmed",
        "detectionAssets": "detection_assets",
        "interrogationAssets": "interrogation_assets",
        "gprCoverageOverlap": "gpr_coverage_overlap",
        "safetyMarginM": "safety_margin_m",
        "remoteInterrogationCompleted": "remote_interrogation_completed",
        "artifactType": "artifact_type",
        "orientation": "orientation",
        "activationMechanism": "activation_mechanism",
        "eodHandoffReference": "eod_handoff_reference",
        "neutralizationResult": "neutralization_result_code",
        "routeResult": "route_result_code",
        "techintComposition": "techint_composition",
        "techintManufacture": "techint_manufacture",
        "techintSignature": "techint_signature",
        "catalogFeedbackReference": "catalog_feedback_reference",
    },
}


class PhaseFieldMappingTests(unittest.TestCase):
    def test_every_phase_dto_field_has_a_documented_sql_destination(self) -> None:
        for stage, fields in PHASE_FIELDS.items():
            for dto_field, sql_destination in fields.items():
                with self.subTest(stage=stage, field=dto_field):
                    self.assertIn(dto_field, MAPPING)
                    self.assertIn(sql_destination, MAPPING)
                    self.assertIn(sql_destination, SQL)

    def test_mapping_records_pdf_page_provenance_and_non_input_capabilities(self) -> None:
        for page_range in ["7–10", "11–14", "25–26", "18–24", "32–35"]:
            self.assertIn(page_range, MAPPING)
        for capability in [
            "RGB 0.2 cm/px",
            "IR 0.5 cm/px",
            "100,000 m²/día",
            "461 objetos",
            "móvil, vehicular, UAV y especiales",
        ]:
            self.assertIn(capability, MAPPING)


if __name__ == "__main__":
    unittest.main(verbosity=2)
