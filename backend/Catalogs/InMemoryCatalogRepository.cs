using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notes.Api.Catalogs;

public sealed record CatalogTestSeed(string Key, string Code, string Label, int SortOrder, bool Active, string[]? Stages = null);

public sealed class InMemoryCatalogRepository(IConfiguration configuration) : ICatalogRepository
{
    private readonly string? _overridePath = configuration["Catalogs:SeedFile"];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CatalogOptionRecord>>> GetAsync(
        IReadOnlyCollection<string> keys,
        string? stage,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var all = await Load(cancellationToken);
        return keys.ToDictionary(
            key => key,
            key => (IReadOnlyList<CatalogOptionRecord>)all[key]
                .Where(option => (includeInactive || option.Active) && Applies(option, stage))
                .OrderBy(option => option.SortOrder).ThenBy(option => option.Code, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    public async Task<CatalogOptionRecord?> FindAsync(string key, string code, string? stage, CancellationToken cancellationToken)
    {
        if (!CatalogRegistry.TryGetTable(key, out _)) throw new CatalogRequestException($"La clave de catálogo '{key}' no es válida.");
        var all = await Load(cancellationToken);
        return all[key].SingleOrDefault(option => option.Code == code && Applies(option, stage));
    }

    private static bool Applies(CatalogOptionRecord option, string? stage) =>
        stage is null || option.Stages.Count == 0 || option.Stages.Contains(stage);

    private async Task<Dictionary<string, List<CatalogOptionRecord>>> Load(CancellationToken cancellationToken)
    {
        var records = StandardSeeds().ToDictionary(seed => (seed.Key, seed.Code));
        if (!string.IsNullOrWhiteSpace(_overridePath) && File.Exists(_overridePath))
        {
            await using var stream = File.OpenRead(_overridePath);
            var overrides = await JsonSerializer.DeserializeAsync<List<CatalogTestSeed>>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken) ?? [];
            foreach (var seed in overrides)
            {
                if (!CatalogRegistry.TryGetTable(seed.Key, out _)) throw new InvalidDataException($"Unknown test catalog key: {seed.Key}");
                records[(seed.Key, seed.Code)] = seed;
            }
        }
        return CatalogRegistry.Keys.ToDictionary(
            key => key,
            key => records.Values.Where(seed => seed.Key == key).Select(ToRecord).ToList(),
            StringComparer.Ordinal);
    }

    private static CatalogOptionRecord ToRecord(CatalogTestSeed seed) => new(
        StableId(seed.Key, seed.Code), seed.Code, seed.Label, seed.SortOrder, seed.Active, 1,
        (seed.Stages ?? []).ToHashSet(StringComparer.Ordinal));

    private static Guid StableId(string key, string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{key}:{code}"));
        return new Guid(bytes[..16]);
    }

    private static IEnumerable<CatalogTestSeed> StandardSeeds()
    {
        var states = new[] { "Aguascalientes", "Baja California", "Baja California Sur", "Campeche", "Chiapas", "Chihuahua", "Ciudad de México", "Coahuila", "Colima", "Durango", "Estado de México", "Guanajuato", "Guerrero", "Hidalgo", "Jalisco", "Michoacán", "Morelos", "Nayarit", "Nuevo León", "Oaxaca", "Puebla", "Querétaro", "Quintana Roo", "San Luis Potosí", "Sinaloa", "Sonora", "Tabasco", "Tamaulipas", "Tlaxcala", "Veracruz", "Yucatán", "Zacatecas" };
        for (var index = 0; index < states.Length; index++) yield return S("mexicoStates", Slug(states[index]), states[index], index + 1);
        foreach (var seed in Many("soils", [T("unknown", "Sin determinar"), T("vertisol", "Vertisol"), T("andosol", "Andosol"), T("leptosol", "Leptosol")])) yield return seed;
        foreach (var seed in Many("seasons", [T("unknown", "Sin determinar"), T("dry", "Seca"), T("rainy", "Lluvias")])) yield return seed;
        foreach (var seed in Many("riskLevels", [T("low", "Baja"), T("medium", "Media"), T("high", "Alta")])) yield return seed;
        yield return S("sensors", "LiDAR", "LiDAR", 1, ["indications"]);
        yield return S("sensors", "RGB", "RGB", 2, ["indications", "reconnaissance"]);
        yield return S("sensors", "IR", "Infrarrojo térmico", 3, ["indications", "reconnaissance"]);
        yield return S("sensors", "GPR", "Radar de penetración terrestre", 4, ["reconnaissance", "mitigation"]);
        yield return S("sensors", "Magnetometría", "Magnetometría", 5, ["reconnaissance"]);
        yield return S("sensors", "EO/IR", "EO/IR", 6, ["surveillance"]);
        yield return S("sensors", "IMSI/IMEI/TMSI", "IMSI / IMEI / TMSI", 7, ["surveillance"]);
        yield return S("sensors", "EOD", "EOD", 8, ["mitigation"]);
        yield return S("sensors", "TECHINT", "TECHINT", 9, ["mitigation"]);
        yield return S("sensors", "Observación", "Observación", 10, ["indications", "reconnaissance", "surveillance", "mitigation"]);
        yield return S("sensors", "Informe", "Informe", 11, ["indications", "reconnaissance", "surveillance", "mitigation"]);
        yield return S("platforms", "if1200a", "Inspired Flight IF1200A", 1, ["indications"]);
        yield return S("platforms", "husky-route-clearance", "Husky para limpieza de ruta", 2, ["mitigation"]);
        foreach (var seed in Many("indicationSources", [T("if1200a-lidar", "IF1200A · LiDAR"), T("if1200a-lidar-rgb-ir", "IF1200A · LiDAR + RGB + IR")], ["indications"])) yield return seed;
        foreach (var seed in Many("indicationSignatures", [T("surface-removal", "Remoción o depresión superficial"), T("irregular-edge", "Borde irregular"), T("vegetation-change", "Cambio de vegetación"), T("soil-density-change", "Cambio de densidad del suelo")], ["indications"])) yield return seed;
        foreach (var seed in Many("radioTechnologies", [T("2G", "2G"), T("3G", "3G"), T("4G", "4G"), T("5G", "5G")], ["surveillance"])) yield return seed;
        foreach (var seed in Many("surveillanceMethods", [T("imsi-imei-tmsi-capture", "Captura IMSI / IMEI / TMSI"), T("target-import-export", "Importar / exportar objetivos"), T("precision-mapping", "Mapeo de precisión"), T("silent-call-df", "Radiogoniometría por llamada silenciosa"), T("mass-sms-transmission", "Transmisión de mensajes SMS masivos"), T("imsi-imei-alert", "Alerta IMSI / IMEI"), T("operations-center-access", "Acceso desde centro de operaciones"), T("selective-signal-block", "Bloqueo selectivo de señal"), T("online-offline-mapping", "Mapeo en línea y fuera de línea"), T("uav-camera-feed", "Integración de cámara UAV"), T("gsma-tac", "Consulta GSMA TAC"), T("target-api", "API de objetivos"), T("ground-refinement-realtime", "Refinamiento terrestre en tiempo real"), T("radio-stack-configuration", "Configuración del stack de radio")], ["surveillance"])) yield return seed;
        foreach (var seed in Many("surveillanceDeployments", [T("uav", "UAV"), T("ground-backpack", "Mochila terrestre"), T("combined", "Combinado")], ["surveillance"])) yield return seed;
        foreach (var seed in Many("humanActivities", [T("not-detected", "Sin actividad detectada"), T("movement-detected", "Movimiento detectado"), T("irregular-pattern", "Patrón irregular"), T("custody-or-activation-risk", "Riesgo de custodia o activación")], ["surveillance"])) yield return seed;
        foreach (var seed in Many("environmentDecisions", [T("clear-for-mitigation", "Entorno certificado para mitigación"), T("hold-for-reassessment", "Detener para reevaluación")], ["surveillance"])) yield return seed;
        foreach (var seed in Many("protectionLevels", [T("stanag-4", "STANAG 4569 nivel 4"), T("stanag-5", "STANAG 4569 nivel 5")], ["mitigation"])) yield return seed;
        foreach (var seed in Many("neutralizationResults", [T("neutralized", "Neutralizado"), T("not-neutralized", "No neutralizado")], ["mitigation"])) yield return seed;
        foreach (var seed in Many("routeResults", [T("route-opened", "Ruta abierta"), T("not-applicable", "No aplica")], ["mitigation"])) yield return seed;
        foreach (var seed in Many("findings", [T("pending", "Pendiente"), T("confirmed", "Confirmado"), T("inconclusive", "Inconcluso"), T("false-positive", "Falso positivo")])) yield return seed;
        foreach (var seed in Many("environmentReviews", [T("pending", "Pendiente"), T("blocked", "Bloqueado"), T("reviewed", "Revisado")])) yield return seed;
        foreach (var seed in Many("workflowStatuses", [T("open", "Abierta"), T("closed", "Cerrada"), T("discarded", "Descartada")])) yield return seed;
        yield return S("workflowStages", "indications", "Indicios tempranos", 1, ["indications"]);
        yield return S("workflowStages", "reconnaissance", "Reconocimiento", 2, ["reconnaissance"]);
        yield return S("workflowStages", "surveillance", "Vigilancia", 3, ["surveillance"]);
        yield return S("workflowStages", "mitigation", "Mitigación y cierre", 4, ["mitigation"]);
    }

    private static (string Code, string Label) T(string code, string label) => (code, label);
    private static CatalogTestSeed S(string key, string code, string label, int order, string[]? stages = null) => new(key, code, label, order, true, stages);
    private static IEnumerable<CatalogTestSeed> Many(string key, (string Code, string Label)[] values, string[]? stages = null) => values.Select((value, index) => S(key, value.Code, value.Label, index + 1, stages));
    private static string Slug(string value) => value.ToLowerInvariant().Normalize(NormalizationForm.FormD).Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark).Aggregate(new StringBuilder(), (builder, character) => builder.Append(char.IsLetterOrDigit(character) ? character : '-')).ToString().Replace("--", "-").Trim('-');
}
