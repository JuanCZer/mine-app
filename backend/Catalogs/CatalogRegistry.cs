using System.Collections.ObjectModel;

namespace Notes.Api.Catalogs;

public static class CatalogRegistry
{
    public static readonly string[] WorkflowStages = ["indications", "reconnaissance", "surveillance", "mitigation"];

    private static readonly IReadOnlyDictionary<string, string> Tables =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mexicoStates"] = "cat_mexico_states",
            ["soils"] = "cat_soils",
            ["seasons"] = "cat_seasons",
            ["riskLevels"] = "cat_risk_levels",
            ["sensors"] = "cat_sensors",
            ["platforms"] = "cat_platforms",
            ["indicationSources"] = "cat_indication_sources",
            ["indicationSignatures"] = "cat_indication_signatures",
            ["radioTechnologies"] = "cat_radio_technologies",
            ["surveillanceMethods"] = "cat_surveillance_methods",
            ["surveillanceDeployments"] = "cat_surveillance_deployments",
            ["humanActivities"] = "cat_human_activities",
            ["environmentDecisions"] = "cat_environment_decisions",
            ["protectionLevels"] = "cat_protection_levels",
            ["neutralizationResults"] = "cat_neutralization_results",
            ["routeResults"] = "cat_route_results",
            ["findings"] = "cat_findings",
            ["environmentReviews"] = "cat_environment_reviews",
            ["workflowStatuses"] = "cat_workflow_statuses",
            ["workflowStages"] = "workflow_stages"
        });

    public static IReadOnlyCollection<string> Keys { get; } = Tables.Keys.ToArray();

    public static bool IsStage(string? stage) => stage is not null && WorkflowStages.Contains(stage, StringComparer.Ordinal);

    public static bool TryGetTable(string key, out string table) => Tables.TryGetValue(key, out table!);

    public static IReadOnlyList<string> ParseKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Keys.Order(StringComparer.Ordinal).ToArray();
        var keys = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0 || keys.Any(key => !Tables.ContainsKey(key)))
            throw new CatalogRequestException("Una o más claves de catálogo no son válidas.");
        return keys;
    }
}
