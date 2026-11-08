using Notes.Api.Catalogs;
using Notes.Api.Models;

var repository = new TestCatalogRepository();
var service = new CatalogService(repository);
var data = PhaseData.Empty() with
{
    Soil = "andosol",
    Season = "rainy",
    Review = new PhaseReview(Finding: "confirmed", Environment: "reviewed"),
    Evidence = [new PhaseEvidence("ev-1", "LiDAR", "ref", "resumen")],
    Indications = PhaseOneIntake.Empty() with
    {
        Platform = "if1200a",
        CollectionSource = "if1200a-lidar",
        Signatures = ["surface-removal"]
    }
};

var snapshot = await service.ValidateSelectionsAsync(data, "indications", null, CancellationToken.None);
Assert(snapshot.Version == 9, "snapshot must retain the maximum catalog version");
Assert(snapshot.Selections.Any(item => item is
{
    Field: "soil", CatalogKey: "soils", Code: "andosol", Label: "Andosol al validar", Version: 7
}), "snapshot must retain the exact label and version used during validation");
Assert(snapshot.Selections.Any(item => item.Field == "evidence.sensor" && item.Code == "LiDAR"),
    "snapshot must include evidence catalog selections");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestCatalogRepository : ICatalogRepository
{
    private static readonly Dictionary<(string Key, string Code), CatalogOptionRecord> Options = new()
    {
        [("soils", "andosol")] = Item("soils", "andosol", "Andosol al validar", 7),
        [("seasons", "rainy")] = Item("seasons", "rainy", "Lluvias", 3),
        [("findings", "confirmed")] = Item("findings", "confirmed", "Confirmado", 4),
        [("environmentReviews", "reviewed")] = Item("environmentReviews", "reviewed", "Revisado", 5),
        [("sensors", "LiDAR")] = Item("sensors", "LiDAR", "LiDAR", 9),
        [("platforms", "if1200a")] = Item("platforms", "if1200a", "IF1200A", 2),
        [("indicationSources", "if1200a-lidar")] = Item("indicationSources", "if1200a-lidar", "LiDAR", 2),
        [("indicationSignatures", "surface-removal")] = Item("indicationSignatures", "surface-removal", "Remoción", 6),
    };

    public Task<IReadOnlyDictionary<string, IReadOnlyList<CatalogOptionRecord>>> GetAsync(
        IReadOnlyCollection<string> keys, string? stage, bool includeInactive, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<CatalogOptionRecord?> FindAsync(
        string key, string code, string? stage, CancellationToken cancellationToken) =>
        Task.FromResult(Options.GetValueOrDefault((key, code)));

    private static CatalogOptionRecord Item(string key, string code, string label, long version) =>
        new(Guid.NewGuid(), code, label, 1, true, version, new HashSet<string>(["indications"]));
}
