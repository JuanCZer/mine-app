using Notes.Api.Models;

namespace Notes.Api.Catalogs;

public sealed class CatalogService(ICatalogRepository repository)
{
    public async Task<CatalogBundleDto> GetAsync(
        IReadOnlyCollection<string> keys,
        string? stage,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        ValidateStage(stage);
        foreach (var key in keys)
            if (!CatalogRegistry.TryGetTable(key, out _)) throw new CatalogRequestException($"La clave de catálogo '{key}' no es válida.");

        var records = await repository.GetAsync(keys, stage, includeInactive, cancellationToken);
        var version = records.Values.SelectMany(options => options).Select(option => option.Version).DefaultIfEmpty(0).Max();
        var catalogs = records.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CatalogOptionDto>)pair.Value.Select(option => new CatalogOptionDto(
                option.Id, option.Code, option.Label, option.SortOrder, option.Active)).ToArray(),
            StringComparer.Ordinal);
        return new CatalogBundleDto(version, catalogs);
    }

    public async Task ResolveSelectionAsync(
        string catalogKey,
        string? code,
        string stage,
        string? previousCode,
        string field,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code)) return;
        ValidateStage(stage);
        _ = await ResolveAsync(
            catalogKey, code, stage, previousCode, field,
            (key, value, selectedStage, token) => repository.FindAsync(key, value, selectedStage, token),
            cancellationToken);
    }

    public Task<CatalogValidationSnapshot> ValidateSelectionsAsync(
        PhaseData data,
        string stage,
        PhaseData? previous,
        CancellationToken cancellationToken) =>
        ValidateSelectionsAsync(
            data,
            stage,
            previous,
            (key, code, selectedStage, token) => repository.FindAsync(key, code, selectedStage, token),
            cancellationToken);

    public async Task<CatalogValidationSnapshot> ValidateSelectionsAsync(
        PhaseData data,
        string stage,
        PhaseData? previous,
        Func<string, string, string, CancellationToken, Task<CatalogOptionRecord?>> resolver,
        CancellationToken cancellationToken)
    {
        ValidateStage(stage);
        var snapshots = new List<CatalogSelectionSnapshot>();
        foreach (var selection in Selections(data, stage, previous)
                     .Where(item => !string.IsNullOrEmpty(item.Code))
                     .DistinctBy(item => (item.Field, item.CatalogKey, item.Code)))
        {
            var option = await ResolveAsync(
                selection.CatalogKey,
                selection.Code,
                stage,
                selection.PreviousCode,
                selection.Field,
                resolver,
                cancellationToken);
            snapshots.Add(new CatalogSelectionSnapshot(
                selection.Field,
                selection.CatalogKey,
                option.Code,
                option.Label,
                option.Version));
        }

        return new CatalogValidationSnapshot(
            snapshots.Select(item => item.Version).DefaultIfEmpty(0).Max(),
            snapshots);
    }

    private static async Task<CatalogOptionRecord> ResolveAsync(
        string catalogKey,
        string code,
        string stage,
        string? previousCode,
        string field,
        Func<string, string, string, CancellationToken, Task<CatalogOptionRecord?>> resolver,
        CancellationToken cancellationToken)
    {
        var option = await resolver(catalogKey, code, stage, cancellationToken);
        if (option is null)
            throw new WorkflowException($"El valor de '{field}' no pertenece al catálogo o a la fase indicada.", StatusCodes.Status400BadRequest, [field]);
        if (!option.Active && !string.Equals(code, previousCode, StringComparison.Ordinal))
            throw new WorkflowException($"La opción de '{field}' fue desactivada. Recarga los catálogos antes de continuar.", StatusCodes.Status409Conflict, [field]);
        return option;
    }

    private static IEnumerable<Selection> Selections(PhaseData data, string stage, PhaseData? previous)
    {
        yield return new("soils", data.Soil, previous?.Soil, "soil");
        yield return new("seasons", data.Season, previous?.Season, "season");
        yield return new("findings", data.Review.Finding, previous?.Review.Finding, "review.finding");
        yield return new("environmentReviews", data.Review.Environment, previous?.Review.Environment, "review.environment");

        var oldSensors = (previous?.Evidence ?? []).Select(item => item.Sensor).ToHashSet(StringComparer.Ordinal);
        foreach (var sensor in data.Evidence.Select(item => item.Sensor).Distinct(StringComparer.Ordinal))
            yield return new("sensors", sensor, oldSensors.Contains(sensor) ? sensor : null, "evidence.sensor");

        if (stage == "indications" && data.Indications is { } indications)
        {
            var old = previous?.Indications;
            yield return new("platforms", indications.Platform, old?.Platform, "indications.platform");
            yield return new("indicationSources", indications.CollectionSource, old?.CollectionSource, "indications.collectionSource");
            foreach (var item in ListSelections("indicationSignatures", indications.Signatures, old?.Signatures, "indications.signatures"))
                yield return item;
        }
        else if (stage == "reconnaissance" && data.Reconnaissance is { } reconnaissance)
        {
            var old = previous?.Reconnaissance;
            yield return new("riskLevels", reconnaissance.Probability, old?.Probability, "reconnaissance.probability");
            yield return new("riskLevels", reconnaissance.Priority, old?.Priority, "reconnaissance.priority");
        }
        else if (stage == "surveillance" && data.Surveillance is { } surveillance)
        {
            var old = previous?.Surveillance;
            foreach (var item in ListSelections("radioTechnologies", surveillance.RadioTechnologies, old?.RadioTechnologies, "surveillance.radioTechnologies"))
                yield return item;
            foreach (var item in ListSelections("surveillanceMethods", surveillance.Methods, old?.Methods, "surveillance.methods"))
                yield return item;
            yield return new("surveillanceDeployments", surveillance.Deployment, old?.Deployment, "surveillance.deployment");
            yield return new("humanActivities", surveillance.HumanActivity, old?.HumanActivity, "surveillance.humanActivity");
            yield return new("environmentDecisions", surveillance.EnvironmentDecision, old?.EnvironmentDecision, "surveillance.environmentDecision");
        }
        else if (stage == "mitigation" && data.Mitigation is { } mitigation)
        {
            var old = previous?.Mitigation;
            yield return new("platforms", mitigation.Platform, old?.Platform, "mitigation.platform");
            yield return new("protectionLevels", mitigation.ProtectionLevel, old?.ProtectionLevel, "mitigation.protectionLevel");
            yield return new("neutralizationResults", mitigation.NeutralizationResult, old?.NeutralizationResult, "mitigation.neutralizationResult");
            yield return new("routeResults", mitigation.RouteResult, old?.RouteResult, "mitigation.routeResult");
        }
    }

    private static IEnumerable<Selection> ListSelections(
        string key,
        IReadOnlyCollection<string>? values,
        IReadOnlyCollection<string>? previous,
        string field)
    {
        var oldValues = (previous ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var value in values ?? [])
            yield return new(key, value, oldValues.Contains(value) ? value : null, field);
    }

    private sealed record Selection(string CatalogKey, string Code, string? PreviousCode, string Field);

    private static void ValidateStage(string? stage)
    {
        if (stage is not null && !CatalogRegistry.IsStage(stage))
            throw new CatalogRequestException("La fase indicada no es válida.");
    }
}
