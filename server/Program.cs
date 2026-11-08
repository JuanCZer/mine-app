using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Notes.Api;
using Notes.Api.Auth;
using Notes.Api.Catalogs;
using Notes.Api.Data;
using Notes.Api.Database;
using Notes.Api.HttpSecurity;
using Notes.Api.IdentityAdministration;
using Notes.Api.Models;
using Notes.Api.Swagger;

var provisioning = args.FirstOrDefault() == "provision-user";
var commandArgs = provisioning ? args.Skip(1).ToArray() : args;
var bootstrap = provisioning && commandArgs.Contains("--bootstrap", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(provisioning && !bootstrap ? [] : args);
if (provisioning) builder.Logging.ClearProviders();
if (provisioning && bootstrap)
{
    var bootstrapDataSource = IdentityBootstrapConnectionFactory.CreateFromEnvironment(builder.Environment);
    builder.Services.AddDbContext<ApplicationIdentityDbContext>(options => options.UseNpgsql(bootstrapDataSource));
    builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
        options.Stores.MaxLengthForKeys = 128;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Password.RequiredLength = 15;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
    }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<ApplicationIdentityDbContext>();
    builder.Services.Configure<PasswordHasherOptions>(options =>
    {
        options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
        options.IterationCount = 210_000;
    });
    builder.Services.AddScoped<ProvisionUserCommand>();
}
else
{
    builder.Services.AddSingleton<PostgresDataSources>();
    builder.Services.AddPersistedIdentity();
}
if (!provisioning) builder.Services.AddSessionAuthentication(builder.Configuration, builder.Environment);
if (!provisioning || !bootstrap)
{
    builder.AddHttpSecurity();
    builder.AddApplicationCors();
}
if (!provisioning && builder.Environment.IsDevelopment())
    builder.Services.AddDevelopmentSwagger();
if (!provisioning || !bootstrap)
    builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5068");
var catalogProvider = builder.Configuration["Catalogs:Provider"] ?? "PostgreSql";
if (string.Equals(catalogProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    if (!builder.Environment.IsEnvironment("Testing"))
        throw new InvalidOperationException("The in-memory catalog provider is permitted only in the Testing environment.");
    builder.Services.AddSingleton<ICatalogRepository, InMemoryCatalogRepository>();
}
else if (string.Equals(catalogProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ICatalogRepository, PostgresCatalogRepository>();
}
else
{
    throw new InvalidOperationException("Catalogs:Provider must be PostgreSql or InMemory.");
}
builder.Services.AddSingleton<CatalogService>();
var noteProvider = builder.Configuration["Notes:Provider"] ?? "PostgreSql";
if (string.Equals(noteProvider, "Json", StringComparison.OrdinalIgnoreCase))
{
    if (!builder.Environment.IsEnvironment("Testing"))
        throw new InvalidOperationException("The JSON note provider is permitted only in the Testing environment.");
    builder.Services.AddSingleton<NoteStore>();
    builder.Services.AddSingleton<INoteRepository>(services => services.GetRequiredService<NoteStore>());
    builder.Services.AddSingleton<IMapRepository>(services => services.GetRequiredService<NoteStore>());
}
else if (string.Equals(noteProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<PostgresNoteRepository>();
    builder.Services.AddSingleton<INoteRepository>(services => services.GetRequiredService<PostgresNoteRepository>());
    builder.Services.AddSingleton<IMapRepository>(services => services.GetRequiredService<PostgresNoteRepository>());
}
else
{
    throw new InvalidOperationException("Notes:Provider must be PostgreSql or Json.");
}
var app = builder.Build();
// Validate transport policy before serving requests or running an administrative command.
// Construction creates shared pools but does not open a physical connection.
if (provisioning)
{
    try
    {
        if (bootstrap)
        {
            await using var adminScope = app.Services.CreateAsyncScope();
            var command = adminScope.ServiceProvider.GetRequiredService<ProvisionUserCommand>();
            Environment.ExitCode = await command.RunAsync(commandArgs.Where(arg => !string.Equals(arg, "--bootstrap", StringComparison.Ordinal)).ToArray(), Console.In, Console.Out);
            return;
        }

        _ = app.Services.GetRequiredService<PostgresDataSources>();
        await using var runtimeScope = app.Services.CreateAsyncScope();
        Environment.ExitCode = await runtimeScope.ServiceProvider.GetRequiredService<ProvisionUserCommand>()
            .RunAsync(commandArgs, Console.In, Console.Out);
    }
    catch (Exception)
    {
        Console.Error.WriteLine("No se pudo aprovisionar la cuenta. Revise conexión, esquema y permisos administrativos.");
        Environment.ExitCode = 1;
    }
    return;
}
var dataSources = app.Services.GetRequiredService<PostgresDataSources>();
_ = IdentitySecurityStartupValidator.Validate(builder.Configuration, dataSources);
app.UseHttpSecurity();
app.UseCors();
// Keep infrastructure details, connection strings and credential material out of HTTP responses.
app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/api/auth")
        || context.Request.Path.StartsWithSegments("/api/admin"))
        IdentityHttpResults.NoStore(context.Response);
    try
    {
        await next(context);
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized && !context.Response.HasStarted)
            await IdentityHttpResults.WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                "authentication_required", "Se requiere una sesión activa.");
    }
    catch (BadHttpRequestException error) when (!context.Response.HasStarted) {
        await IdentityHttpResults.WriteErrorAsync(context, error.StatusCode,
            "validation_failed", "Solicitud inválida.");
    }
    catch (UnauthorizedAccessException) when (!context.Response.HasStarted) {
        await IdentityHttpResults.WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
            "authentication_required", "Se requiere una sesión activa.");
    }
    catch (Exception) when (!context.Response.HasStarted) {
        await IdentityHttpResults.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
            "service_unavailable", "El servicio no está disponible temporalmente.");
    }
});
if (app.Environment.IsDevelopment())
{
    app.UseDevelopmentSwagger();
}
else
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(context);
    });
}
app.UseAuthentication();
app.UseMiddleware<RequiredPasswordChangeFilter>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/health", async (HttpContext context) =>
{
    IdentityReadinessResult result;
    try
    {
        var readiness = context.RequestServices.GetRequiredService<IIdentityReadiness>();
        result = await readiness.CheckAsync(context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
    catch (Exception)
    {
        result = new(false, "identity_readiness_unavailable", Array.Empty<string>());
    }
    return result.IsReady
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "unavailable", code = result.Code }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapCatalogEndpoints();
app.MapAuthEndpoints();
app.MapIdentityAdministrationEndpoints();
app.MapMapEndpoints();

var notes = app.MapGroup("/api/notes").RequireAuthorization();
notes.AddEndpointFilter<CsrfFilter>();
notes.AddEndpointFilter(async (context, next) => {
    try { return await next(context); }
    catch (WorkflowException error) { return Results.Json(new { error = error.Message, issues = error.Issues }, statusCode: error.StatusCode); }
    catch (CatalogUnavailableException) { return Results.Json(new { error = "El servicio de catálogos no está disponible temporalmente." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (NoteRepositoryUnavailableException) { return Results.Json(new { error = "El servicio de incidencias no está disponible temporalmente." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
});
notes.MapGet("/", async (HttpContext context, INoteRepository store) => Results.Ok(await store.List(CurrentUser.GetRequiredId(context.User).ToString(), context.RequestAborted)));
notes.MapGet("/{id}", async (string id, HttpContext context, INoteRepository store) => {
    var note = await store.Get(CurrentUser.GetRequiredId(context.User).ToString(), id, context.RequestAborted);
    return note is null ? Results.NotFound() : Results.Ok(note);
});
notes.MapPost("/", async (HttpContext context, INoteRepository store, NoteInput input) => Invalid(input) is { } error ? Results.BadRequest(new { error }) : Results.Ok(await store.Create(CurrentUser.GetRequiredId(context.User).ToString(), input, context.RequestAborted)));
notes.MapPut("/{id}", async (string id, HttpContext context, INoteRepository store, NoteInput input) => { if (Invalid(input) is { } error) return Results.BadRequest(new { error }); var note = await store.Update(CurrentUser.GetRequiredId(context.User).ToString(), id, input, context.RequestAborted); return note is null ? Results.NotFound() : Results.Ok(note); });
notes.MapDelete("/{id}", async (string id, int expectedRevision, HttpContext context, INoteRepository store) => await store.Delete(CurrentUser.GetRequiredId(context.User).ToString(), id, expectedRevision, context.RequestAborted) ? Results.NoContent() : Results.NotFound());
notes.MapPut("/{id}/phases/{stage}", async (string id, string stage, SavePhaseRequest request, HttpContext context, INoteRepository store) => {
    var note = await store.SavePhase(CurrentUser.GetRequiredId(context.User).ToString(), id, stage, request, context.RequestAborted);
    return note is null ? Results.NotFound() : Results.Ok(note);
});
notes.MapPost("/{id}/phases/{stage}/validate", async (string id, string stage, ValidatePhaseRequest request, HttpContext context, INoteRepository store) => {
    var note = await store.ValidatePhase(CurrentUser.GetRequiredId(context.User).ToString(), id, stage, request, context.RequestAborted);
    return note is null ? Results.NotFound() : Results.Ok(note);
}).RequireAuthorization("CanValidatePhase");
app.Run();

static string? Invalid(NoteInput input) { if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 120) return "El identificador de la incidencia es obligatorio y admite hasta 120 caracteres."; if (string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 10000) return "La descripción del indicio es obligatoria y admite hasta 10000 caracteres."; if (string.IsNullOrWhiteSpace(input.State) || input.State.Length > 100) return "El área o corredor de interés es obligatorio y admite hasta 100 caracteres."; if (input.Altitude is null || !double.IsFinite(input.Altitude.Value) || Math.Abs(input.Altitude.Value) > 90) return "La latitud debe estar entre -90 y 90."; if (input.Longitude is null || !double.IsFinite(input.Longitude.Value) || Math.Abs(input.Longitude.Value) > 180) return "La longitud debe estar entre -180 y 180."; return null; }
