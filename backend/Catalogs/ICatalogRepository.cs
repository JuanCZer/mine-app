namespace Notes.Api.Catalogs;

public interface ICatalogRepository
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<CatalogOptionRecord>>> GetAsync(
        IReadOnlyCollection<string> keys,
        string? stage,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<CatalogOptionRecord?> FindAsync(
        string key,
        string code,
        string? stage,
        CancellationToken cancellationToken);
}
