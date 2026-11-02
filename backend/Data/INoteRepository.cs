using Notes.Api.Models;

namespace Notes.Api.Data;

public interface INoteRepository
{
    Task<IReadOnlyList<Note>> List(string userId, CancellationToken cancellationToken = default);
    Task<Note?> Get(string userId, string id, CancellationToken cancellationToken = default);
    Task<Note> Create(string userId, NoteInput input, CancellationToken cancellationToken = default);
    Task<Note?> Update(string userId, string id, NoteInput input, CancellationToken cancellationToken = default);
    Task<bool> Delete(string userId, string id, int expectedRevision, CancellationToken cancellationToken = default);
    Task<Note?> SavePhase(string userId, string id, string stage, SavePhaseRequest request, CancellationToken cancellationToken = default);
    Task<Note?> ValidatePhase(string userId, string id, string stage, ValidatePhaseRequest request, CancellationToken cancellationToken = default);
}

public sealed class NoteRepositoryUnavailableException(Exception? inner = null)
    : Exception("El servicio de incidencias no está disponible temporalmente.", inner);
