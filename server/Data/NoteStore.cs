using System.Text.Json;
using Notes.Api.Catalogs;
using Notes.Api.Models;
namespace Notes.Api.Data;

// Almacén legado permitido únicamente como proveedor explícito de pruebas e importación.
public sealed class NoteStore(IWebHostEnvironment environment, IConfiguration configuration, CatalogService catalogs) : INoteRepository, IMapRepository
{
    private readonly string _path = configuration["Notes:DataPath"] ?? Path.Combine(environment.ContentRootPath, "Data", "notes.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<Note>> List(string userId, CancellationToken cancellationToken = default) =>
        (await Read(cancellationToken)).Where(n => n.UserId == userId).OrderByDescending(n => n.UpdatedAt).ToList();

    public Task<MapSectors> Sectors(CancellationToken cancellationToken) => ProximityCounter.ExecuteAsync(configuration,
        async (maximum, token) => (await Read(token)).Where(HasLocation).Take(maximum + 1)
            .Select(note => (note.Altitude!.Value, note.Longitude!.Value)).ToList(), cancellationToken);

    public async Task<MapIncidentPage> Incidents(MapBounds bounds, string? after, CancellationToken cancellationToken)
    {
        var notes = (await Read(cancellationToken)).Where(n => HasLocation(n) && n.Longitude >= bounds.West && n.Longitude <= bounds.East && n.Altitude >= bounds.South && n.Altitude <= bounds.North && (after is null || string.CompareOrdinal(n.Id, after) > 0))
            .OrderBy(n => n.Id, StringComparer.Ordinal).Take(501).ToList();
        var items = notes.Take(500).Select(n => new MapIncident(n.Id, n.Title, n.Content, n.State, n.Altitude!.Value, n.Longitude!.Value, n.Workflow?.Status ?? "open", n.CreatedAt)).ToList();
        return new MapIncidentPage(items, notes.Count > 500 ? items[^1].Id : null);
    }

    public async Task<Note?> SharedDetail(string id, CancellationToken cancellationToken) =>
        (await Read(cancellationToken)).SingleOrDefault(n => n.Id == id && HasLocation(n));

    private static bool HasLocation(Note n) => n.Altitude is double lat && double.IsFinite(lat) && lat >= -90 && lat <= 90 && n.Longitude is double lon && double.IsFinite(lon) && lon >= -180 && lon <= 180;

    public async Task<Note?> Get(string userId, string id, CancellationToken cancellationToken = default) =>
        (await Read(cancellationToken)).SingleOrDefault(note => note.Id == id && note.UserId == userId);

    public async Task<Note> Create(string userId, NoteInput input, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note(Guid.NewGuid().ToString("N"), userId, input.Title?.Trim() ?? "", input.Content?.Trim() ?? "", input.State?.Trim(), input.Altitude, input.Longitude, now, now, NoteWorkflow.Empty());
        await Mutate(notes => notes.Add(note), cancellationToken);
        return note;
    }

    public Task<Note?> Update(string userId, string id, NoteInput input, CancellationToken cancellationToken = default) => Change(userId, id, note =>
    {
        WorkflowRules.CheckRevision(note, input.ExpectedRevision);
        if (note.Workflow!.Phases.Any(phase => phase.ValidatedAt is not null))
            throw new WorkflowException("El registro inicial está vinculado a una fase validada y es de solo lectura.");
        return note with { Title = input.Title?.Trim() ?? "", Content = input.Content?.Trim() ?? "", State = input.State?.Trim(), Altitude = input.Altitude, Longitude = input.Longitude };
    }, cancellationToken);

    public Task<Note?> SavePhase(string userId, string id, string stage, SavePhaseRequest request, CancellationToken cancellationToken = default) => ChangeAsync(userId, id, async note =>
    {
        WorkflowRules.CheckRevision(note, request.ExpectedRevision);
        var current = WorkflowRules.ActivePhase(note, stage);
        WorkflowRules.CheckDraft(request.Data);
        await catalogs.ValidateSelectionsAsync(request.Data, stage, current.Data, cancellationToken);
        var data = request.Data with {
            Evidence = request.Data.Evidence.ToList(),
            Indications = request.Data.Indications is null ? null : request.Data.Indications with { Signatures = request.Data.Indications.Signatures?.ToList() ?? [] },
            Surveillance = request.Data.Surveillance is null ? null : request.Data.Surveillance with {
                RadioTechnologies = request.Data.Surveillance.RadioTechnologies?.ToList() ?? [],
                Methods = request.Data.Surveillance.Methods?.ToList() ?? []
            }
        };
        return note with { Workflow = note.Workflow! with {
            Phases = note.Workflow!.Phases.Select(phase => phase.Stage == stage ? phase with { Data = data } : phase).ToList(),
            History = AddEvent(note.Workflow!, userId, $"Borrador guardado: fase {Array.IndexOf(WorkflowRules.Stages, stage) + 1}. No habilita la siguiente fase.")
        } };
    }, cancellationToken);

    public Task<Note?> ValidatePhase(string userId, string id, string stage, ValidatePhaseRequest request, CancellationToken cancellationToken = default) => ChangeAsync(userId, id, async note =>
    {
        WorkflowRules.CheckRevision(note, request.ExpectedRevision);
        var phase = WorkflowRules.ActivePhase(note, stage);
        WorkflowRules.CheckDraft(phase.Data);
        await catalogs.ValidateSelectionsAsync(phase.Data, stage, phase.Data, cancellationToken);
        var issues = WorkflowRules.ValidationIssues(note, stage, phase.Data);
        if (issues.Count > 0) throw new WorkflowException("La fase no cumple los requisitos de validación.", 422, issues);
        var now = DateTimeOffset.UtcNow;
        var status = stage == "mitigation" ? "closed" : stage == "reconnaissance" && phase.Data.Review.Finding == "false-positive" ? "discarded" : "open";
        var number = Array.IndexOf(WorkflowRules.Stages, stage) + 1;
        var text = status == "discarded" ? "Fase 2 validada. Proceso cerrado como falso positivo." : status == "closed" ? "Fase 4 validada. Proceso completo con informe de cierre y TECHINT." : $"Fase {number} validada. Fase {number + 1} habilitada.";
        return note with { Workflow = new NoteWorkflow(status,
            note.Workflow!.Phases.Select(p => p.Stage == stage ? p with { ValidatedAt = now, ValidatedBy = userId } : p).ToList(),
            AddEvent(note.Workflow!, userId, text)) };
    }, cancellationToken);

    public async Task<bool> Delete(string userId, string id, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var deleted = false;
        await Mutate(notes =>
        {
            var note = notes.SingleOrDefault(n => n.Id == id && n.UserId == userId);
            if (note is null) return;
            WorkflowRules.CheckRevision(note, expectedRevision);
            deleted = notes.Remove(note);
        }, cancellationToken);
        return deleted;
    }

    private async Task<Note?> Change(string userId, string id, Func<Note, Note> update, CancellationToken cancellationToken)
    {
        Note? updated = null;
        await Mutate(notes => {
            var index = notes.FindIndex(n => n.Id == id && n.UserId == userId);
            if (index < 0) return;
            var original = notes[index];
            updated = update(original) with { Revision = original.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            notes[index] = updated;
        }, cancellationToken);
        return updated;
    }

    private async Task<Note?> ChangeAsync(string userId, string id, Func<Note, Task<Note>> update, CancellationToken cancellationToken)
    {
        Note? updated = null;
        await MutateAsync(async notes => {
            var index = notes.FindIndex(n => n.Id == id && n.UserId == userId);
            if (index < 0) return;
            var original = notes[index];
            updated = (await update(original)) with { Revision = original.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            notes[index] = updated;
        }, cancellationToken);
        return updated;
    }

    private static List<WorkflowEvent> AddEvent(NoteWorkflow workflow, string actor, string text) => [..workflow.History, new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(actor) ? "Responsable pendiente" : actor.Trim(), text)];
    private async Task<List<Note>> Read(CancellationToken cancellationToken) { await _lock.WaitAsync(cancellationToken); try { return await ReadUnsafe(cancellationToken); } finally { _lock.Release(); } }
    private async Task Mutate(Action<List<Note>> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            var notes = await ReadUnsafe(cancellationToken);
            action(notes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(notes, _json), cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _lock.Release(); }
        }
    }

    private async Task MutateAsync(Func<List<Note>, Task> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            var notes = await ReadUnsafe(cancellationToken);
            await action(notes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(notes, _json), cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _lock.Release(); }
        }
    }
    private async Task<List<Note>> ReadUnsafe(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return [];
        var content = await File.ReadAllTextAsync(_path, cancellationToken);
        var notes = JsonSerializer.Deserialize<List<Note>>(content, _json) ?? [];
        // Existing notes enter phase one without losing their content or coordinates.
        return notes.Select(note => note with { Workflow = note.Workflow ?? NoteWorkflow.Empty() }).ToList();
    }
}
