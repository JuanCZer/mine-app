using Notes.Api.Catalogs;

namespace Notes.Api.Models;

public sealed record PhaseReview(
    string Baseline = "", string Comparison = "", string Finding = "pending",
    string Environment = "pending", string Report = "", string Techint = "",
    string Reviewer = "", string Notes = "");

public sealed record PhaseEvidence(string Id, string Sensor, string Reference, string Summary, string? RecordedAt = null);
public sealed record PhaseOneIntake(
    string Platform = "", bool BlueUasNdaaCompliant = false, double? FlightDurationMin = null,
    double? LidarPointRate = null, double? PayloadLb = null, double? MissionSpeedMph = null,
    double? WindKnots = null, string TerrainModelReference = "", bool DifferentialAnalysisCompleted = false,
    double? SurfaceVariationMm = null, bool ActionableAlertGenerated = false,
    bool PersonnelNotExposed = false, bool CoordinateTransferredToVeles = false,
    string ObservedAt = "", string CollectionSource = "", List<string>? Signatures = null,
    string AlternativeCauses = "", double? DepressionDepthCm = null)
{
    public static PhaseOneIntake Empty() => new(Signatures: []);
}

public sealed record ReconnaissanceIntake(
    double? MagneticReadingNt = null, double? GprDepthCm = null, double? RtkAccuracyCm = null,
    double? LocalizationAccuracyCm = null, string Probability = "", string Priority = "",
    string GeoreferencedPointReference = "", string RiskMapReference = "", string AiClassificationReference = "",
    bool FusionCompleted = false)
{
    public static ReconnaissanceIntake Empty() => new();
}

public sealed record SurveillanceIntake(
    string ObservationStartedAt = "", string ObservationEndedAt = "", List<string>? RadioTechnologies = null,
    List<string>? Methods = null, string Deployment = "", int? CoveredChannels = null,
    bool DuplicatedChannelConfiguration = false, int? ActiveDeviceCount = null,
    string HumanActivity = "", string EnvironmentDecision = "")
{
    public static SurveillanceIntake Empty() => new("", "", [], [], "", null, false, null, "", "");
}

public sealed record MitigationIntake(
    bool CoordinateReceived = false, string Platform = "", string ProtectionLevel = "",
    bool PersonnelProtectionConfirmed = false, bool EquipmentProtectionConfirmed = false,
    int? DetectionAssets = null, int? InterrogationAssets = null, bool GprCoverageOverlap = false,
    double? SafetyMarginM = null, bool RemoteInterrogationCompleted = false, string ArtifactType = "",
    string Orientation = "", string ActivationMechanism = "", string EodHandoffReference = "",
    string NeutralizationResult = "", string RouteResult = "", string TechintComposition = "",
    string TechintManufacture = "", string TechintSignature = "", string CatalogFeedbackReference = "")
{
    public static MitigationIntake Empty() => new();
}

public sealed record PhaseData(
    PhaseReview Review, string Soil, string Season, string TerrainNotes, List<PhaseEvidence> Evidence,
    PhaseOneIntake? Indications = null, ReconnaissanceIntake? Reconnaissance = null,
    SurveillanceIntake? Surveillance = null, MitigationIntake? Mitigation = null)
{
    public static PhaseData Empty() => new(
        new(), "unknown", "unknown", "", [], PhaseOneIntake.Empty(), ReconnaissanceIntake.Empty(),
        SurveillanceIntake.Empty(), MitigationIntake.Empty());
}
public sealed record NotePhase(
    string Stage,
    PhaseData Data,
    DateTimeOffset? ValidatedAt = null,
    string? ValidatedBy = null,
    CatalogValidationSnapshot? CatalogSnapshot = null);
public sealed record MessageReference(string Code, IReadOnlyDictionary<string, object?>? Parameters = null);
public sealed record ApiMessage(string Code, string Text, IReadOnlyDictionary<string, object?>? Parameters = null)
{
    public MessageReference Reference => new(Code, Parameters);
}
public sealed record WorkflowEvent(string Id, DateTimeOffset At, string Actor, string Text,
    string? Code = null, IReadOnlyDictionary<string, object?>? Parameters = null);

public static class WorkflowEventMessages
{
    public static ApiMessage Created() => new("incident_created", "Incidencia registrada. Las cuatro fases están disponibles para captura.");

    public static ApiMessage Saved(int stage) => new("phase_saved",
        $"Borrador guardado: fase {stage}. Pendiente de validación.", new Dictionary<string, object?> { ["stage"] = stage });

    public static ApiMessage Validated(int stage, string status) => status switch
    {
        "discarded" => new("workflow_discarded", "Fase 2 validada. Proceso cerrado como falso positivo."),
        "closed" => new("workflow_closed", "Fase 4 validada. Proceso completo con informe de cierre y TECHINT."),
        _ => new("phase_validated", $"Fase {stage} validada. Siguiente validación: fase {stage + 1}.",
            new Dictionary<string, object?> { ["stage"] = stage, ["nextStage"] = stage + 1 })
    };
}

public sealed record NoteWorkflow(string Status, List<NotePhase> Phases, List<WorkflowEvent> History)
{
    public string? CurrentStage => Status == "open" ? Phases.FirstOrDefault(phase => phase.ValidatedAt is null)?.Stage : null;
    public static NoteWorkflow Empty() => new("open", WorkflowRules.Stages.Select(stage => new NotePhase(stage, PhaseData.Empty())).ToList(), []);
}
public sealed record SavePhaseRequest(int ExpectedRevision, PhaseData Data);
public sealed record ValidatePhaseRequest(int ExpectedRevision);

public sealed class WorkflowException(string message, string code, int statusCode = 409,
    IReadOnlyList<string>? issues = null, IReadOnlyDictionary<string, object?>? parameters = null,
    IReadOnlyList<ApiMessage>? issueDetails = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?>? Parameters { get; } = parameters;
    public IReadOnlyList<string> Issues { get; } = issues ?? issueDetails?.Select(issue => issue.Text).ToArray() ?? [];
    public IReadOnlyList<MessageReference> IssueDetails { get; } = issueDetails?.Select(issue => issue.Reference).ToArray() ?? [];
}

public static class WorkflowRules
{
    public static readonly string[] Stages = ["indications", "reconnaissance", "surveillance", "mitigation"];
    // These codes change workflow semantics. Their labels and membership come from PostgreSQL,
    // while changes to their behavior require an explicit code change and migration.
    private static readonly string[] IndicationSources = ["if1200a-lidar", "if1200a-lidar-rgb-ir"];
    private static readonly string[] RiskLevels = ["low", "medium", "high"];
    private static readonly string[] Deployments = ["uav", "ground-backpack", "combined"];
    private static readonly string[] HumanActivities = ["not-detected", "movement-detected", "irregular-pattern", "custody-or-activation-risk"];
    private static readonly string[] ProtectionLevels = ["stanag-4", "stanag-5"];
    private static readonly string[] RouteResults = ["route-opened", "not-applicable"];

    public static void CheckRevision(Note note, int? expected)
    {
        if (expected != note.Revision) throw new WorkflowException("La incidencia cambió. Recarga la versión actual antes de guardar o validar.", "incident_revision_conflict");
    }

    public static NotePhase EditablePhase(Note note, string stage)
    {
        if (!Stages.Contains(stage)) throw new WorkflowException("La fase indicada no existe.", "phase_not_found", 400);
        var workflow = note.Workflow ?? NoteWorkflow.Empty();
        if (workflow.Status != "open") throw new WorkflowException("El proceso de esta incidencia ya está cerrado.", "workflow_closed");
        var phase = workflow.Phases.Single(phase => phase.Stage == stage);
        if (phase.ValidatedAt is not null) throw new WorkflowException("Las fases validadas son de solo lectura.", "phase_read_only");
        return phase;
    }

    public static NotePhase ActivePhase(Note note, string stage)
    {
        var phase = EditablePhase(note, stage);
        if ((note.Workflow ?? NoteWorkflow.Empty()).CurrentStage != stage)
            throw new WorkflowException("Debes validar las fases anteriores antes de validar esta fase. Puedes guardar su borrador sin validarlas.", "previous_phases_required");
        return phase;
    }

    public static void CheckDraft(PhaseData? data)
    {
        if (data?.Review is null || data.Evidence is null) throw new WorkflowException("La información de la fase no es válida.", "phase_data_invalid", 400);
        var review = data.Review;
        if (string.IsNullOrEmpty(data.Soil) || string.IsNullOrEmpty(data.Season)
            || string.IsNullOrEmpty(review.Finding) || string.IsNullOrEmpty(review.Environment))
            throw new WorkflowException("Completa las opciones obligatorias de revisión.", "review_options_required", 400);
        if (new[] { review.Baseline, review.Comparison, review.Report, review.Techint }.Any(value => value is null || value.Length > 500)
            || review.Reviewer is null || review.Reviewer.Length > 100 || review.Notes is null || review.Notes.Length > 5000
            || data.TerrainNotes is null || data.TerrainNotes.Length > 2000 || data.Evidence.Count > 100)
            throw new WorkflowException("Revisa los campos y los límites de longitud de la fase.", "phase_fields_invalid", 400);
        var indications = data.Indications;
        if (indications is not null && (
            !FiniteWithin(indications.FlightDurationMin, 0, 43)
            || !FiniteWithin(indications.LidarPointRate, 0, 5000000)
            || !FiniteWithin(indications.PayloadLb, 0, 19.1)
            || !FiniteWithin(indications.MissionSpeedMph, 0, 56)
            || !FiniteWithin(indications.WindKnots, 0, 23)
            || !StringWithin(indications.TerrainModelReference, 500)
            || !FiniteWithin(indications.SurfaceVariationMm, 0, 1000)
            || indications.ObservedAt is null || indications.ObservedAt.Length > 50
            || indications.CollectionSource is null
            || indications.AlternativeCauses is null || indications.AlternativeCauses.Length > 2000
            || indications.DepressionDepthCm is { } depth && (!double.IsFinite(depth) || depth < 0 || depth > 100)
            || indications.Signatures is not null && (indications.Signatures.Count > 100 || indications.Signatures.Distinct().Count() != indications.Signatures.Count)))
            throw new WorkflowException("La información de indicios no corresponde al formato de la fase 1.", "indications_data_invalid", 400);
        var reconnaissance = data.Reconnaissance;
        if (reconnaissance is not null && (
            !FiniteWithin(reconnaissance.MagneticReadingNt, 0, 100000)
            || !FiniteWithin(reconnaissance.GprDepthCm, 0, 1000)
            || !FiniteWithin(reconnaissance.RtkAccuracyCm, 0, 100)
            || !FiniteWithin(reconnaissance.LocalizationAccuracyCm, 0, 100)
            || !StringWithin(reconnaissance.GeoreferencedPointReference, 500)
            || !StringWithin(reconnaissance.RiskMapReference, 500)
            || !StringWithin(reconnaissance.AiClassificationReference, 500)))
            throw new WorkflowException("La información de reconocimiento no corresponde al formato de la fase 2.", "reconnaissance_data_invalid", 400);
        var surveillance = data.Surveillance;
        if (surveillance is not null && (
            !StringWithin(surveillance.ObservationStartedAt, 50)
            || !StringWithin(surveillance.ObservationEndedAt, 50)
            || !ListHasUniqueValues(surveillance.RadioTechnologies)
            || !ListHasUniqueValues(surveillance.Methods)
            || surveillance.CoveredChannels is { } channels && (channels < 0 || channels > 100)
            || surveillance.ActiveDeviceCount is { } devices && (devices < 0 || devices > 100000)
            ))
            throw new WorkflowException("La información de vigilancia no corresponde al formato de la fase 3.", "surveillance_data_invalid", 400);
        var mitigation = data.Mitigation;
        if (mitigation is not null && (
            mitigation.DetectionAssets is { } detectionAssets && (detectionAssets < 0 || detectionAssets > 100)
            || mitigation.InterrogationAssets is { } interrogationAssets && (interrogationAssets < 0 || interrogationAssets > 100)
            || !FiniteWithin(mitigation.SafetyMarginM, 0, 100000)
            || !StringWithin(mitigation.ArtifactType, 500)
            || !StringWithin(mitigation.Orientation, 500)
            || !StringWithin(mitigation.ActivationMechanism, 500)
            || !StringWithin(mitigation.EodHandoffReference, 500)
            || !StringWithin(mitigation.TechintComposition, 2000)
            || !StringWithin(mitigation.TechintManufacture, 2000)
            || !StringWithin(mitigation.TechintSignature, 2000)
            || !StringWithin(mitigation.CatalogFeedbackReference, 500)))
            throw new WorkflowException("La información de mitigación no corresponde al formato de la fase 4.", "mitigation_data_invalid", 400);
        if (data.Evidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > 100 || string.IsNullOrWhiteSpace(e.Sensor)
            || string.IsNullOrWhiteSpace(e.Reference) || e.Reference.Length > 300 || string.IsNullOrWhiteSpace(e.Summary) || e.Summary.Length > 2000)
            || data.Evidence.Select(e => e.Id).Distinct().Count() != data.Evidence.Count)
            throw new WorkflowException("Cada evidencia necesita fuente, referencia y descripción válidas.", "evidence_invalid", 400);
    }

    // Retain the string-only contract for callers that have not adopted localization metadata.
    public static List<string> ValidationIssues(Note note, string stage, PhaseData data) =>
        ValidationIssueDetails(note, stage, data).Select(issue => issue.Text).ToList();

    public static List<ApiMessage> ValidationIssueDetails(Note note, string stage, PhaseData data)
    {
        var issues = new List<ApiMessage>();
        var review = data.Review;
        if (string.IsNullOrWhiteSpace(note.Title) || string.IsNullOrWhiteSpace(note.Content) || string.IsNullOrWhiteSpace(note.State)
            || note.Altitude is null || !double.IsFinite(note.Altitude.Value) || Math.Abs(note.Altitude.Value) > 90
            || note.Longitude is null || !double.IsFinite(note.Longitude.Value) || Math.Abs(note.Longitude.Value) > 180)
            issues.Add(new("initial_record_required", "Completa y guarda el registro inicial de indicios y sus coordenadas."));
        switch (stage)
        {
            case "indications":
                var indications = data.Indications;
                if (string.IsNullOrWhiteSpace(review.Baseline)) issues.Add(new("baseline_required", "Falta la referencia del levantamiento base."));
                if (string.IsNullOrWhiteSpace(review.Comparison)) issues.Add(new("comparison_required", "Falta la referencia del levantamiento de comparación."));
                if (indications is null || indications.Platform != "if1200a") issues.Add(new("indications_platform_required", "Indica la plataforma Inspired Flight IF1200A."));
                if (indications is null || !indications.BlueUasNdaaCompliant) issues.Add(new("platform_compliance_required", "Confirma el cumplimiento Blue UAS / NDAA de la plataforma."));
                if (indications is null || indications.FlightDurationMin is not { } flightDuration || flightDuration <= 0 || flightDuration > 43) issues.Add(new("flight_duration_required", "Registra una duración de vuelo entre 0 y 43 minutos."));
                if (indications is null || indications.LidarPointRate is not { } pointRate || pointRate < 640000) issues.Add(new("lidar_point_rate_required", "Registra una captura LiDAR de al menos 640,000 puntos por segundo."));
                if (indications is null || indications.PayloadLb is not { } payload || payload <= 0 || payload > 19.1) issues.Add(new("payload_required", "Registra una carga útil entre 0 y 19.1 lb."));
                if (indications is null || indications.MissionSpeedMph is not { } missionSpeed || missionSpeed <= 0 || missionSpeed > 56) issues.Add(new("mission_speed_required", "Registra una velocidad de misión entre 0 y 56 mph."));
                if (indications is null || indications.WindKnots is not { } wind || wind < 0 || wind > 23) issues.Add(new("wind_speed_required", "Registra una resistencia de viento de hasta 23 nudos."));
                if (indications is null || string.IsNullOrWhiteSpace(indications.TerrainModelReference)) issues.Add(new("terrain_model_required", "Agrega la referencia del modelo tridimensional denso del corredor."));
                if (indications is null || !indications.DifferentialAnalysisCompleted) issues.Add(new("differential_analysis_required", "Confirma la ejecución del algoritmo diferencial contra la línea base."));
                if (indications is null || indications.SurfaceVariationMm is not { } surfaceVariation || surfaceVariation <= 0) issues.Add(new("surface_variation_required", "Registra la variación milimétrica de superficie observada."));
                if (indications is null || !indications.ActionableAlertGenerated) issues.Add(new("actionable_alert_required", "Confirma la generación de una alerta procesable de I&W."));
                if (indications is null || !indications.PersonnelNotExposed) issues.Add(new("personnel_not_exposed_required", "Confirma que el levantamiento no expuso personal al área de riesgo."));
                if (indications is null || !indications.CoordinateTransferredToVeles) issues.Add(new("coordinate_transfer_required", "Confirma la transferencia de la coordenada de anomalía a VELES."));
                if (indications is null || string.IsNullOrWhiteSpace(indications.ObservedAt) || !DateTime.TryParse(indications.ObservedAt, out _)) issues.Add(new("comparison_time_required", "Registra la fecha y hora del levantamiento comparativo."));
                if (indications is null || !IndicationSources.Contains(indications.CollectionSource)) issues.Add(new("indications_source_required", "Indica la fuente LiDAR del levantamiento de Fase 1."));
                if (indications?.Signatures is null || indications.Signatures.Count == 0) issues.Add(new("indication_signature_required", "Selecciona al menos una firma de alteración del terreno observada."));
                if (indications?.Signatures?.Contains("surface-removal") == true && (indications.DepressionDepthCm is not { } depth || depth < 5 || depth > 15)) issues.Add(new("depression_depth_required", "Para una depresión o remoción, registra una profundidad estimada entre 5 y 15 cm."));
                if (string.IsNullOrWhiteSpace(data.TerrainNotes)) issues.Add(new("terrain_conditions_required", "Describe las condiciones base de topografía, vegetación y suelo."));
                if (indications is null || string.IsNullOrWhiteSpace(indications.AlternativeCauses)) issues.Add(new("alternative_causes_required", "Documenta las causas naturales o actividades civiles consideradas para descartar un falso positivo."));
                if (!data.Evidence.Any(e => e.Sensor == "LiDAR")) issues.Add(new("lidar_evidence_required", "Agrega la referencia de la nube de puntos o producto LiDAR que sustenta el indicio."));
                break;
            case "reconnaissance":
                var reconnaissance = data.Reconnaissance;
                if (!new[] { "confirmed", "false-positive" }.Contains(review.Finding)) issues.Add(new("finding_required", "El dictamen está pendiente o es inconcluso."));
                foreach (var sensor in new[] { "GPR", "Magnetometría", "RGB", "IR" }) if (!data.Evidence.Any(e => e.Sensor == sensor)) issues.Add(new("veles_evidence_required", $"Agrega la evidencia {sensor} de VELES.", new Dictionary<string, object?> { ["sensor"] = sensor }));
                if (reconnaissance is null || reconnaissance.MagneticReadingNt is not { } magneticReading || magneticReading < 10) issues.Add(new("magnetic_reading_required", "Registra una lectura magnetométrica de al menos 10 nT."));
                if (reconnaissance is null || reconnaissance.GprDepthCm is not { } gprDepth || gprDepth <= 0 || gprDepth > 30) issues.Add(new("gpr_depth_required", "Registra la profundidad GPR observada, hasta 30 cm."));
                if (reconnaissance is null || reconnaissance.RtkAccuracyCm is not { } rtkAccuracy || rtkAccuracy < 1 || rtkAccuracy > 3) issues.Add(new("rtk_accuracy_required", "Registra precisión RTK entre 1 y 3 cm."));
                if (reconnaissance is null || reconnaissance.LocalizationAccuracyCm is not { } localizationAccuracy || localizationAccuracy <= 0 || localizationAccuracy > 5) issues.Add(new("localization_accuracy_required", "Registra la localización de la amenaza con precisión de hasta 5 cm."));
                if (reconnaissance is null || !RiskLevels.Contains(reconnaissance.Probability) || !RiskLevels.Contains(reconnaissance.Priority)) issues.Add(new("risk_classification_required", "Clasifica la probabilidad y prioridad de la anomalía."));
                if (reconnaissance is null || string.IsNullOrWhiteSpace(reconnaissance.GeoreferencedPointReference)) issues.Add(new("georeferenced_point_required", "Agrega la referencia del punto georreferenciado resultante."));
                if (reconnaissance is null || string.IsNullOrWhiteSpace(reconnaissance.RiskMapReference)) issues.Add(new("risk_map_required", "Agrega la referencia del mapa de riesgo."));
                if (reconnaissance is null || string.IsNullOrWhiteSpace(reconnaissance.AiClassificationReference)) issues.Add(new("ai_classification_required", "Agrega la referencia de clasificación IA de VELES."));
                if (reconnaissance is null || !reconnaissance.FusionCompleted) issues.Add(new("sensor_fusion_required", "Confirma la fusión de GPR, magnetometría, RGB e infrarrojo térmico."));
                break;
            case "surveillance":
                var surveillance = data.Surveillance;
                if (surveillance is null || !DateTime.TryParse(surveillance.ObservationStartedAt, out _) || !DateTime.TryParse(surveillance.ObservationEndedAt, out _)) issues.Add(new("surveillance_period_required", "Registra el inicio y cierre del periodo de vigilancia."));
                if (surveillance?.RadioTechnologies is null || surveillance.RadioTechnologies.Count == 0) issues.Add(new("radio_technologies_required", "Indica las tecnologías móviles observadas: 2G, 3G, 4G o 5G."));
                if (surveillance?.Methods?.Contains("imsi-imei-tmsi-capture") != true) issues.Add(new("identity_capture_required", "Registra la captura IMSI, IMEI y TMSI de la vigilancia."));
                if (surveillance?.Methods?.Contains("uav-camera-feed") != true) issues.Add(new("uav_camera_required", "Registra la integración de cámara UAV para observación EO/IR."));
                if (surveillance?.Methods?.Any(method => method is "precision-mapping" or "silent-call-df" or "target-api") != true) issues.Add(new("geolocation_method_required", "Registra un método de geolocalización o radiogoniometría."));
                if (surveillance is null || !Deployments.Contains(surveillance.Deployment)) issues.Add(new("surveillance_deployment_required", "Indica el despliegue de vigilancia utilizado."));
                if (surveillance is not null && (surveillance.Deployment is "uav" or "combined") && (surveillance.CoveredChannels is not { } coveredChannels || coveredChannels < 2)) issues.Add(new("uav_channels_required", "Registra al menos dos canales de cobertura para el despliegue UAV."));
                if (surveillance is not null && surveillance.DuplicatedChannelConfiguration && (surveillance.CoveredChannels is not { } duplicatedChannels || duplicatedChannels < 4)) issues.Add(new("duplicated_channels_required", "La configuración duplicada requiere al menos cuatro canales de cobertura."));
                if (surveillance is null || surveillance.ActiveDeviceCount is not { } activeDeviceCount || activeDeviceCount < 0) issues.Add(new("active_devices_required", "Registra el número de dispositivos móviles activos detectados."));
                if (surveillance is null || !HumanActivities.Contains(surveillance.HumanActivity)) issues.Add(new("human_activity_required", "Clasifica la actividad humana observada."));
                if (surveillance is null || surveillance.EnvironmentDecision != "clear-for-mitigation") issues.Add(new("environment_clearance_required", "La vigilancia debe certificar el entorno humano antes de pasar a mitigación."));
                if (!data.Evidence.Any(e => e.Sensor == "EO/IR")) issues.Add(new("eo_ir_evidence_required", "Agrega la evidencia de observación EO/IR."));
                if (!data.Evidence.Any(e => e.Sensor == "IMSI/IMEI/TMSI")) issues.Add(new("identity_capture_evidence_required", "Agrega la evidencia de captura IMSI, IMEI o TMSI."));
                break;
            case "mitigation":
                var mitigation = data.Mitigation;
                if (mitigation is null || !mitigation.CoordinateReceived) issues.Add(new("coordinate_receipt_required", "Confirma la recepción de la coordenada exacta de VELES."));
                if (mitigation is null || mitigation.Platform != "husky-route-clearance") issues.Add(new("mitigation_platform_required", "Indica la plataforma Husky de limpieza de ruta."));
                if (mitigation is null || !ProtectionLevels.Contains(mitigation.ProtectionLevel)) issues.Add(new("protection_level_required", "Selecciona el nivel de protección STANAG 4569 utilizado."));
                if (mitigation is null || !mitigation.PersonnelProtectionConfirmed || !mitigation.EquipmentProtectionConfirmed) issues.Add(new("protection_confirmation_required", "Confirma la protección de personal y equipo antes de la limpieza de ruta."));
                if (mitigation is null || mitigation.DetectionAssets is not { } detectionAssetCount || detectionAssetCount < 2) issues.Add(new("detection_assets_required", "Registra al menos dos medios de detección para la limpieza de ruta."));
                if (mitigation is null || mitigation.InterrogationAssets is not { } interrogationAssetCount || interrogationAssetCount < 1) issues.Add(new("interrogation_assets_required", "Registra al menos un medio de interrogación."));
                if (mitigation is null || !mitigation.GprCoverageOverlap) issues.Add(new("gpr_overlap_required", "Confirma el traslape de cobertura GPR."));
                if (mitigation is null || mitigation.SafetyMarginM is not { } safetyMargin || safetyMargin <= 0) issues.Add(new("safety_margin_required", "Registra los márgenes de seguridad del brazo de interrogación."));
                if (mitigation is null || !mitigation.RemoteInterrogationCompleted) issues.Add(new("remote_interrogation_required", "Confirma la interrogación del objetivo a distancia segura."));
                if (mitigation is null || string.IsNullOrWhiteSpace(mitigation.ArtifactType) || string.IsNullOrWhiteSpace(mitigation.Orientation) || string.IsNullOrWhiteSpace(mitigation.ActivationMechanism)) issues.Add(new("artifact_details_required", "Documenta tipo, orientación y mecanismo de activación antes de la entrega EOD."));
                if (mitigation is null || string.IsNullOrWhiteSpace(mitigation.EodHandoffReference)) issues.Add(new("eod_handoff_required", "Agrega la referencia de entrega al operador EOD."));
                if (mitigation is null || mitigation.NeutralizationResult != "neutralized") issues.Add(new("neutralization_required", "Registra la neutralización controlada del dispositivo."));
                if (mitigation is null || !RouteResults.Contains(mitigation.RouteResult)) issues.Add(new("route_result_required", "Registra el resultado de apertura de ruta segura."));
                if (mitigation is null || string.IsNullOrWhiteSpace(mitigation.TechintComposition) || string.IsNullOrWhiteSpace(mitigation.TechintManufacture) || string.IsNullOrWhiteSpace(mitigation.TechintSignature)) issues.Add(new("techint_details_required", "Documenta composición, manufactura y firma para TECHINT."));
                if (mitigation is null || string.IsNullOrWhiteSpace(mitigation.CatalogFeedbackReference)) issues.Add(new("catalog_feedback_required", "Agrega la referencia de retroalimentación al catálogo de IA de VELES."));
                foreach (var sensor in new[] { "GPR", "EOD", "TECHINT" }) if (!data.Evidence.Any(e => e.Sensor == sensor)) issues.Add(new("mitigation_evidence_required", $"Agrega la evidencia {sensor} de mitigación.", new Dictionary<string, object?> { ["sensor"] = sensor }));
                break;
        }
        return issues;
    }

    private static bool FiniteWithin(double? value, double minimum, double maximum) => value is null || (double.IsFinite(value.Value) && value.Value >= minimum && value.Value <= maximum);
    private static bool StringWithin(string? value, int maximum) => value is not null && value.Length <= maximum;
    private static bool ListHasUniqueValues(List<string>? values) => values is null || (values.Count <= 100 && values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct().Count() == values.Count);
}
