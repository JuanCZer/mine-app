using Notes.Api.Models;

namespace Notes.Api.Catalogs;

public sealed record CatalogOptionDto(
    Guid Id,
    string Code,
    string Label,
    int SortOrder,
    bool Active);

public sealed record CatalogBundleDto(
    long Version,
    IReadOnlyDictionary<string, IReadOnlyList<CatalogOptionDto>> Catalogs);

public sealed record CatalogOptionRecord(
    Guid Id,
    string Code,
    string Label,
    int SortOrder,
    bool Active,
    long Version,
    IReadOnlySet<string> Stages);

public sealed record CatalogSelectionSnapshot(
    string Field,
    string CatalogKey,
    string Code,
    string Label,
    long Version);

public sealed record CatalogValidationSnapshot(
    long Version,
    IReadOnlyList<CatalogSelectionSnapshot> Selections);

public sealed record PhaseValidationSnapshot(
    PhaseData Data,
    CatalogValidationSnapshot Catalogs);

public sealed class CatalogRequestException(string message, string code, int statusCode = StatusCodes.Status400BadRequest,
    IReadOnlyDictionary<string, object?>? parameters = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?>? Parameters { get; } = parameters;
}

public sealed class CatalogUnavailableException(Exception? inner = null)
    : Exception("El servicio de catálogos no está disponible temporalmente.", inner);
