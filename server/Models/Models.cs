namespace Notes.Api.Models;
public sealed record PublicUser(string Id, string Username, string DisplayName, string Role, string[] Permissions, bool MustChangePassword, string? CreatedAt);
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(PublicUser User);
public sealed record NoteInput(string? Title, string? Content, string? State, double? Altitude, double? Longitude, int? ExpectedRevision = null);
public sealed record Note(string Id, string UserId, string Title, string Content, string? State, double? Altitude, double? Longitude, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, NoteWorkflow? Workflow = null, int Revision = 0);
