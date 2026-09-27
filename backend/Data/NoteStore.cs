using System.Text.Json;
using Notes.Api.Models;
namespace Notes.Api.Data;

public sealed class NoteStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly string _path = configuration["Notes:DataPath"] ?? Path.Combine(environment.ContentRootPath, "Data", "notes.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<Note>> List(string userId) => (await Read()).Where(n => n.UserId == userId).OrderByDescending(n => n.UpdatedAt).ToList();

    public async Task<Note> Create(string userId, NoteInput input)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note(Guid.NewGuid().ToString("N"), userId, input.Title?.Trim() ?? "", input.Content?.Trim() ?? "", input.State?.Trim(), input.Altitude, input.Longitude, now, now, NoteWorkflow.Empty());
        await Mutate(notes => notes.Add(note));
        return note;
    }

    public Task<Note?> Update(string userId, string id, NoteInput input) => Change(userId, id, note =>
    {
        WorkflowRules.CheckRevision(note, input.ExpectedRevision);
        if (note.Workflow!.Phases.Any(phase => phase.ValidatedAt is not null))
            throw new WorkflowException("El registro inicial está vinculado a una fase validada y es de solo lectura.");
        return note with { Title = input.Title?.Trim() ?? "", Content = input.Content?.Trim() ?? "", State = input.State?.Trim(), Altitude = input.Altitude, Longitude = input.Longitude };
    });

    public Task<Note?> SavePhase(string userId, string id, string stage, SavePhaseRequest request) => Change(userId, id, note =>
    {
        WorkflowRules.CheckRevision(note, request.ExpectedRevision);
        WorkflowRules.ActivePhase(note, stage);
        WorkflowRules.CheckDraft(request.Data);
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
            History = AddEvent(note.Workflow!, data.Review.Reviewer, $"Borrador guardado: fase {Array.IndexOf(WorkflowRules.Stages, stage) + 1}. No habilita la siguiente fase.")
        } };
    });

    public Task<Note?> ValidatePhase(string userId, string id, string stage, ValidatePhaseRequest request) => Change(userId, id, note =>
    {
        WorkflowRules.CheckRevision(note, request.ExpectedRevision);
        var phase = WorkflowRules.ActivePhase(note, stage);
        WorkflowRules.CheckDraft(phase.Data);
        var issues = WorkflowRules.ValidationIssues(note, stage, phase.Data);
        if (issues.Count > 0) throw new WorkflowException("La fase no cumple los requisitos de validación.", 422, issues);
        var now = DateTimeOffset.UtcNow;
        var status = stage == "mitigation" ? "closed" : stage == "reconnaissance" && phase.Data.Review.Finding == "false-positive" ? "discarded" : "open";
        var number = Array.IndexOf(WorkflowRules.Stages, stage) + 1;
        var text = status == "discarded" ? "Fase 2 validada. Proceso cerrado como falso positivo." : status == "closed" ? "Fase 4 validada. Proceso completo con informe de cierre y TECHINT." : $"Fase {number} validada. Fase {number + 1} habilitada.";
        return note with { Workflow = new NoteWorkflow(status,
            note.Workflow!.Phases.Select(p => p.Stage == stage ? p with { ValidatedAt = now, ValidatedBy = phase.Data.Review.Reviewer.Trim() } : p).ToList(),
            AddEvent(note.Workflow!, phase.Data.Review.Reviewer, text)) };
    });

    public async Task<bool> Delete(string userId, string id)
    {
        var deleted = false;
        await Mutate(notes => deleted = notes.RemoveAll(n => n.Id == id && n.UserId == userId) > 0);
        return deleted;
    }

    private async Task<Note?> Change(string userId, string id, Func<Note, Note> update)
    {
        Note? updated = null;
        await Mutate(notes => {
            var index = notes.FindIndex(n => n.Id == id && n.UserId == userId);
            if (index < 0) return;
            var original = notes[index];
            updated = update(original) with { Revision = original.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            notes[index] = updated;
        });
        return updated;
    }

    private static List<WorkflowEvent> AddEvent(NoteWorkflow workflow, string actor, string text) => [..workflow.History, new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(actor) ? "Responsable pendiente" : actor.Trim(), text)];
    private async Task<List<Note>> Read() { await _lock.WaitAsync(); try { return await ReadUnsafe(); } finally { _lock.Release(); } }
    private async Task Mutate(Action<List<Note>> action)
    {
        await _lock.WaitAsync();
        string? temporary = null;
        try
        {
            var notes = await ReadUnsafe();
            action(notes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(notes, _json));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _lock.Release(); }
        }
    }
    private async Task<List<Note>> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        var content = await File.ReadAllTextAsync(_path);
        var notes = JsonSerializer.Deserialize<List<Note>>(content, _json) ?? [];
        // Existing notes enter phase one without losing their content or coordinates.
        return notes.Select(note => note with { Workflow = note.Workflow ?? NoteWorkflow.Empty() }).ToList();
    }
}
