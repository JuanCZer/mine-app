using System.Collections.Concurrent;
using System.Security.Cryptography;
using Notes.Api.Data;
using Notes.Api.Models;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5068");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost:4200", "http://127.0.0.1:4200", "http://localhost:4317", "http://127.0.0.1:4317").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<NoteStore>();
builder.Services.AddSingleton<ConcurrentDictionary<string, string>>();
var app = builder.Build();
app.UseCors();
var demo = new User("demo-user", "demo", "Usuario Demo", Hash("Demo123!"));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/auth/login", (LoginRequest request, ConcurrentDictionary<string, string> sessions) => {
    if (!string.Equals(request.Username?.Trim(), demo.Username, StringComparison.OrdinalIgnoreCase) || !Verify(request.Password ?? "", demo.PasswordHash)) return Results.Unauthorized();
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); sessions[token] = demo.Id;
    return Results.Ok(new LoginResponse(token, new PublicUser(demo.Id, demo.Username, demo.DisplayName)));
});
app.MapPost("/api/auth/logout", (HttpContext context, ConcurrentDictionary<string, string> sessions) => { if (Token(context) is { } token) sessions.TryRemove(token, out _); return Results.NoContent(); });
app.MapGet("/api/auth/me", (HttpContext context, ConcurrentDictionary<string, string> sessions) => UserId(context, sessions) is null ? Results.Unauthorized() : Results.Ok(new PublicUser(demo.Id, demo.Username, demo.DisplayName)));

var notes = app.MapGroup("/api/notes");
notes.AddEndpointFilter(async (context, next) => {
    try { return await next(context); }
    catch (WorkflowException error) { return Results.Json(new { error = error.Message, issues = error.Issues }, statusCode: error.StatusCode); }
});
notes.MapGet("/", async (HttpContext context, ConcurrentDictionary<string, string> sessions, NoteStore store) => Results.Ok(await store.List(UserId(context, sessions)!)));
notes.MapPost("/", async (HttpContext context, ConcurrentDictionary<string, string> sessions, NoteStore store, NoteInput input) => Invalid(input) is { } error ? Results.BadRequest(new { error }) : Results.Ok(await store.Create(UserId(context, sessions)!, input)));
notes.MapPut("/{id}", async (string id, HttpContext context, ConcurrentDictionary<string, string> sessions, NoteStore store, NoteInput input) => { if (Invalid(input) is { } error) return Results.BadRequest(new { error }); var note = await store.Update(UserId(context, sessions)!, id, input); return note is null ? Results.NotFound() : Results.Ok(note); });
notes.MapDelete("/{id}", async (string id, HttpContext context, ConcurrentDictionary<string, string> sessions, NoteStore store) => await store.Delete(UserId(context, sessions)!, id) ? Results.NoContent() : Results.NotFound());
notes.MapPut("/{id}/phases/{stage}", async (string id, string stage, SavePhaseRequest request, HttpContext context, ConcurrentDictionary<string, string> sessions, NoteStore store) => {
    var note = await store.SavePhase(UserId(context, sessions)!, id, stage, request);
    return note is null ? Results.NotFound() : Results.Ok(note);
});
notes.MapPost("/{id}/phases/{stage}/validate", async (string id, string stage, ValidatePhaseRequest request, HttpContext context, ConcurrentDictionary<string, string> sessions, NoteStore store) => {
    var note = await store.ValidatePhase(UserId(context, sessions)!, id, stage, request);
    return note is null ? Results.NotFound() : Results.Ok(note);
});
app.Run();

static string? Token(HttpContext context) { var value = context.Request.Headers.Authorization.ToString(); return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null; }
// Mientras el login está desactivado, las peticiones anónimas usan el espacio demo.
static string? UserId(HttpContext context, ConcurrentDictionary<string, string> sessions) => Token(context) is { } token && sessions.TryGetValue(token, out var id) ? id : "demo-user";
static string? Invalid(NoteInput input) { if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 120) return "El identificador de la incidencia es obligatorio y admite hasta 120 caracteres."; if (string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 10000) return "La descripción del indicio es obligatoria y admite hasta 10000 caracteres."; if (string.IsNullOrWhiteSpace(input.State) || input.State.Length > 100) return "El área o corredor de interés es obligatorio."; if (input.Altitude is null || !double.IsFinite(input.Altitude.Value) || Math.Abs(input.Altitude.Value) > 90) return "La latitud debe estar entre -90 y 90."; if (input.Longitude is null || !double.IsFinite(input.Longitude.Value) || Math.Abs(input.Longitude.Value) > 180) return "La longitud debe estar entre -180 y 180."; return null; }
static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32); return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}"; }
static bool Verify(string password, string stored) { var parts = stored.Split('.'); if (parts.Length != 2) return false; var salt = Convert.FromBase64String(parts[0]); var expected = Convert.FromBase64String(parts[1]); var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(actual, expected); }
