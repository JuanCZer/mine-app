using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public static class IdentityAdministrationEndpoints
{
    private static readonly IReadOnlyList<AdminRoleOption> Roles =
    [
        new("operador", "Operador"),
        new("validador", "Validador"),
        new("administrador", "Administrador")
    ];

    public static IEndpointRouteBuilder MapIdentityAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin")
            .RequireAuthorization("AdministerUsers");
        group.MapGet("/users", ListUsersAsync);
        ProtectMutation(group.MapPost("/users", CreateUserAsync), "admin-users-create");
        ProtectMutation(group.MapPut("/users/{id}/role", MutateRoleAsync), "admin-user-role");
        ProtectMutation(group.MapPut("/users/{id}/status", MutateStatusAsync), "admin-user-status");
        ProtectMutation(group.MapPost("/users/{id}/password-reset", AdminPasswordReset.EndpointAsync), "admin-user-password-reset");
        group.MapGet("/roles", RolesAsync);
        return endpoints;
    }

    public static RouteHandlerBuilder ProtectMutation(RouteHandlerBuilder builder, string routeId) =>
        builder.RequireAdminStepUp(routeId);

    public static IReadOnlyList<AdminRoleOption> CanonicalRoles() => Roles;

    public static async Task<IResult> RolesAsync(HttpContext context, ApplicationIdentityDbContext database)
    {
        IdentityHttpResults.NoStore(context.Response);
        var persisted = await database.Roles.AsNoTracking()
            .Select(role => new { role.Name, role.NormalizedName })
            .ToListAsync(context.RequestAborted);
        var available = Roles.Where(option => persisted.Any(role =>
            string.Equals(role.Name, option.Code, StringComparison.Ordinal)
            && string.Equals(role.NormalizedName, option.Code.ToUpperInvariant(), StringComparison.Ordinal)))
            .ToArray();
        return Results.Ok(available);
    }

    public static async Task<IResult> ListUsersAsync(
        HttpContext context,
        IIdentityAdministrationStore store,
        IIdentitySecurityAudit audit)
    {
        var queryValues = context.Request.Query;
        if (!AdminUserDirectoryQuery.TryParse(
                queryValues["q"].FirstOrDefault(),
                queryValues["role"].FirstOrDefault(),
                queryValues["status"].FirstOrDefault(),
                queryValues["page"].FirstOrDefault(),
                queryValues["pageSize"].FirstOrDefault(),
                out var query,
                out var fieldErrors))
        {
            await audit.RecordRejectionAsync(new(
                IdentitySecurityActions.ValidationReject,
                "validation_failed",
                TryActor(context.User),
                "user_directory",
                "user-directory",
                "rejected",
                context.TraceIdentifier,
                new Dictionary<string, string>
                {
                    ["routeTemplate"] = "admin-users-list",
                    ["validationFields"] = string.Join(',', fieldErrors.Keys.Order(StringComparer.Ordinal))
                }), context.RequestAborted);
            IdentityHttpResults.NoStore(context.Response);
            return Results.Json(new
            {
                error = "Los filtros de consulta no son válidos.",
                code = "validation_failed",
                fieldErrors
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = CurrentUser.GetRequiredId(context.User);
        var result = await store.ListUsersAsync(query!, context.RequestAborted);
        await store.RecordSuccessfulListAsync(new(
            actor,
            "user_directory",
            "user-directory",
            context.TraceIdentifier,
            query!), context.RequestAborted);
        IdentityHttpResults.NoStore(context.Response);
        return Results.Ok(result);
    }

    public static async Task<IResult> CreateUserAsync(
        HttpContext context,
        AdminCreateUserRequest request,
        [FromServices] IAntiforgery antiforgery,
        [FromServices] IIdentityRateLimiter rateLimiter,
        [FromServices] IIdentityAdministrationStore store,
        [FromServices] IIdentitySecurityAudit audit,
        [FromServices] ILookupNormalizer normalizer,
        [FromServices] IPasswordHasher<ApplicationUser> hasher,
        [FromServices] TimeProvider clock)
    {
        NoStore(context.Response);
        var actor = CurrentUser.GetRequiredId(context.User);
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await RejectAsync(context, audit, IdentitySecurityActions.CsrfReject,
                "csrf_invalid", actor, "csrf", []);
            return Error(StatusCodes.Status400BadRequest, "csrf_invalid", "Token CSRF inválido.");
        }

        var hasSingleKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues)
            && keyValues.Count == 1
            && !keyValues[0]!.Contains(',', StringComparison.Ordinal);
        var rawKey = hasSingleKey ? keyValues[0] : null;
        var validIntent = AdminCreateUserIntent.TryCreate(request, normalizer,
            out var intent, out var fieldErrors);
        var validKey = IdentityCreationProtocol.TryParseIdempotencyKey(rawKey, out var idempotencyKey);
        if (!validKey)
            fieldErrors = new Dictionary<string, string[]>(fieldErrors, StringComparer.Ordinal)
                { ["idempotencyKey"] = ["Se requiere un UUID v4 canónico en minúsculas."] };
        if (!validIntent || !validKey)
        {
            await RejectAsync(context, audit, IdentitySecurityActions.ValidationReject,
                "validation_failed", actor, "validation", fieldErrors.Keys);
            return Results.Json(new
            {
                error = "Los datos de la cuenta no son válidos.",
                code = "validation_failed",
                fieldErrors
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        IdentityRateLimitDecision quota;
        try
        {
            quota = await rateLimiter.ConsumeAsync(new(
                "admin-mutation", actor.ToString("D"), null, clock.GetUtcNow()),
                context.RequestAborted);
        }
        catch (IdentityRateLimitUnavailableException)
        {
            await RejectAsync(context, audit, IdentitySecurityActions.RateLimitReject,
                "service_unavailable", actor, "quota_unavailable", []);
            return Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                "El servicio no está disponible temporalmente.");
        }
        if (!quota.Allowed)
        {
            await RejectAsync(context, audit, IdentitySecurityActions.RateLimitReject,
                "rate_limited", actor, "quota_exceeded", []);
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited",
                "Demasiados intentos. Reintente más tarde.");
        }

        var outcome = await store.CreateUserAsync(new(
            actor, idempotencyKey, intent!, clock.GetUtcNow(), context.TraceIdentifier),
            creationIntent =>
            {
                var temporaryPassword = IdentityCreationProtocol.GenerateTemporaryPassword();
                var userId = Guid.NewGuid();
                var user = new ApplicationUser { Id = userId, UserName = creationIntent.Username };
                return new(userId, hasher.HashPassword(user, temporaryPassword), temporaryPassword);
            }, context.RequestAborted);
        switch (outcome.Kind)
        {
            case AdminCreateUserOutcomeKind.Created:
                return ExactStoredResponse(outcome);
            case AdminCreateUserOutcomeKind.Replayed:
                context.Response.Headers["Idempotency-Replayed"] = "true";
                return ExactStoredResponse(outcome);
            case AdminCreateUserOutcomeKind.UsernameConflict:
                await RejectAsync(context, audit, IdentitySecurityActions.ValidationReject,
                    "username_conflict", actor, "username_conflict", ["username"]);
                return Error(StatusCodes.Status409Conflict, "username_conflict", "El nombre de usuario ya existe.");
            case AdminCreateUserOutcomeKind.IdempotencyKeyReused:
                await RejectAsync(context, audit, IdentitySecurityActions.ValidationReject,
                    "idempotency_key_reused", actor, "idempotency_key_reused", []);
                return Error(StatusCodes.Status409Conflict, "idempotency_key_reused",
                    "La clave de idempotencia ya se usó con otros datos.");
            case AdminCreateUserOutcomeKind.IdempotencyResultExpired:
                await RejectAsync(context, audit, IdentitySecurityActions.ValidationReject,
                    "idempotency_result_expired", actor, "idempotency_result_expired", []);
                return Error(StatusCodes.Status409Conflict, "idempotency_result_expired",
                    "El resultado idempotente ya expiró.");
            default:
                throw new InvalidOperationException("Unknown administrative creation outcome.");
        }
    }

    public static Task<IResult> MutateRoleAsync(
        HttpContext context,
        string id,
        [FromServices] IAntiforgery antiforgery,
        [FromServices] IIdentityRateLimiter rateLimiter,
        [FromServices] IIdentityAdministrationStore store,
        [FromServices] IIdentitySecurityAudit audit,
        [FromServices] TimeProvider clock) =>
        MutateUserAsync(context, id, AdminUserMutationKind.Role, antiforgery,
            rateLimiter, store, audit, clock);

    public static Task<IResult> MutateStatusAsync(
        HttpContext context,
        string id,
        [FromServices] IAntiforgery antiforgery,
        [FromServices] IIdentityRateLimiter rateLimiter,
        [FromServices] IIdentityAdministrationStore store,
        [FromServices] IIdentitySecurityAudit audit,
        [FromServices] TimeProvider clock) =>
        MutateUserAsync(context, id, AdminUserMutationKind.Status, antiforgery,
            rateLimiter, store, audit, clock);

    private static async Task<IResult> MutateUserAsync(
        HttpContext context,
        string id,
        AdminUserMutationKind kind,
        IAntiforgery antiforgery,
        IIdentityRateLimiter rateLimiter,
        IIdentityAdministrationStore store,
        IIdentitySecurityAudit audit,
        TimeProvider clock)
    {
        NoStore(context.Response);
        var actor = CurrentUser.GetRequiredId(context.User);
        var routeId = kind == AdminUserMutationKind.Role ? "admin-user-role" : "admin-user-status";
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await RejectMutationAsync(context, audit, IdentitySecurityActions.CsrfReject,
                "csrf_invalid", actor, routeId, id, "csrf", [], null);
            return Error(StatusCodes.Status400BadRequest, "csrf_invalid", "Token CSRF inválido.");
        }

        AdminUserMutationIntent? intent = null;
        IReadOnlyDictionary<string, string[]> fieldErrors;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal)
                    { ["body"] = ["El cuerpo debe ser un objeto JSON."] };
            }
            else
            {
                var root = document.RootElement;
                var propertyNames = new HashSet<string>(StringComparer.Ordinal);
                string? duplicateName = null;
                foreach (var property in root.EnumerateObject())
                {
                    if (propertyNames.Add(property.Name)) continue;
                    duplicateName = property.Name;
                    break;
                }
                if (duplicateName is not null)
                {
                    var duplicateField = duplicateName is "role" or "isActive" or "expectedManagementVersion"
                        ? duplicateName : "body";
                    fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal)
                        { [duplicateField] = ["El campo JSON no puede aparecer más de una vez."] };
                }
                else
                {
                    var version = root.TryGetProperty("expectedManagementVersion", out var versionValue)
                        ? versionValue : default;
                    if (kind == AdminUserMutationKind.Role)
                    {
                        var role = root.TryGetProperty("role", out var roleValue) ? roleValue : default;
                        _ = AdminUserMutationIntent.TryCreateRole(
                            id, new(role, version), out intent, out fieldErrors);
                    }
                    else
                    {
                        var isActive = root.TryGetProperty("isActive", out var statusValue)
                            ? statusValue : default;
                        _ = AdminUserMutationIntent.TryCreateStatus(
                            id, new(isActive, version), out intent, out fieldErrors);
                    }
                }
            }
        }
        catch (JsonException)
        {
            fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal)
                { ["body"] = ["El cuerpo JSON no es válido."] };
        }

        if (intent is null)
        {
            await RejectMutationAsync(context, audit, IdentitySecurityActions.ValidationReject,
                "validation_failed", actor, routeId, id, "validation", fieldErrors.Keys, null);
            return Results.Json(new
            {
                error = "Los datos de la cuenta no son válidos.",
                code = "validation_failed",
                fieldErrors
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        IdentityRateLimitDecision quota;
        try
        {
            quota = await rateLimiter.ConsumeAsync(new(
                "admin-mutation", actor.ToString("D"), null, clock.GetUtcNow()),
                context.RequestAborted);
        }
        catch (IdentityRateLimitUnavailableException)
        {
            await RejectMutationAsync(context, audit, IdentitySecurityActions.RateLimitReject,
                "service_unavailable", actor, routeId, id, "quota_unavailable", [],
                intent.ExpectedManagementVersionText);
            return Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                "El servicio no está disponible temporalmente.");
        }
        if (!quota.Allowed)
        {
            if (quota.RetryAfter is { } retryAfter)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(
                    (retryAfter - clock.GetUtcNow()).TotalSeconds));
                context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            await RejectMutationAsync(context, audit, IdentitySecurityActions.RateLimitReject,
                "rate_limited", actor, routeId, id, "quota_exceeded", [],
                intent.ExpectedManagementVersionText);
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited",
                "Demasiados intentos. Reintente más tarde.");
        }

        AdminUserMutationOutcome outcome;
        try
        {
            outcome = await store.MutateUserAsync(new(
                actor, kind, intent, clock.GetUtcNow(), context.TraceIdentifier), context.RequestAborted);
        }
        catch (IdentityAdminUnavailableException)
        {
            await RejectMutationAsync(context, audit, IdentitySecurityActions.ValidationReject,
                "service_unavailable", actor, routeId, id, "mutation_unavailable", [],
                intent.ExpectedManagementVersionText);
            return Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                "El servicio no está disponible temporalmente.");
        }
        if (outcome.Kind is AdminUserMutationOutcomeKind.Updated or AdminUserMutationOutcomeKind.NoOp)
            return Results.Ok(outcome.User ?? throw new IdentityAdminUnavailableException());

        var (status, code, message, action) = outcome.Kind switch
        {
            AdminUserMutationOutcomeKind.NotFound =>
                (StatusCodes.Status404NotFound, "validation_failed", "El usuario no existe.",
                    IdentitySecurityActions.ValidationReject),
            AdminUserMutationOutcomeKind.StaleManagementVersion =>
                (StatusCodes.Status409Conflict, "stale_management_version",
                    "La cuenta cambió. Recargue los datos antes de continuar.",
                    IdentitySecurityActions.ConcurrencyReject),
            AdminUserMutationOutcomeKind.SelfManagementForbidden =>
                (StatusCodes.Status409Conflict, "self_management_forbidden",
                    "No puede modificar su propia cuenta administrativa.",
                    IdentitySecurityActions.ValidationReject),
            AdminUserMutationOutcomeKind.LastActiveAdministrator =>
                (StatusCodes.Status409Conflict, "last_active_administrator",
                    "Debe permanecer al menos un administrador activo.",
                    IdentitySecurityActions.ValidationReject),
            _ => throw new InvalidOperationException("Unknown administrative mutation outcome.")
        };
        await RejectMutationAsync(context, audit, action, code, actor, routeId, id,
            outcome.Kind.ToString(), [], intent.ExpectedManagementVersionText);
        return Error(status, code, message);
    }

    private static void NoStore(HttpResponse response)
    {
        IdentityHttpResults.NoStore(response);
        response.Headers.Pragma = "no-cache";
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = message, code }, statusCode: status);

    private static IResult ExactStoredResponse(AdminCreateUserOutcome outcome)
    {
        if (outcome.StatusCode != StatusCodes.Status201Created || outcome.Body is null)
            throw new IdentityAdminUnavailableException();
        return new ExactJsonResult(outcome.StatusCode, outcome.Body);
    }

    private static Task RejectAsync(
        HttpContext context,
        IIdentitySecurityAudit audit,
        string action,
        string code,
        Guid actor,
        string reason,
        IEnumerable<string> validationFields) =>
        audit.RecordRejectionAsync(new(
            action, code, actor, "http_route", "admin-users-create", "rejected",
            context.TraceIdentifier,
            new Dictionary<string, string>
            {
                ["httpMethod"] = context.Request.Method,
                ["routeTemplate"] = "admin-users-create",
                ["reason"] = reason,
                ["validationFields"] = string.Join(',', validationFields.Order(StringComparer.Ordinal))
            }), context.RequestAborted);

    private static Task RejectMutationAsync(
        HttpContext context,
        IIdentitySecurityAudit audit,
        string action,
        string code,
        Guid actor,
        string routeId,
        string targetId,
        string reason,
        IEnumerable<string> validationFields,
        string? expectedVersion)
    {
        var canonicalTarget = Guid.TryParseExact(targetId, "D", out var target)
            && target != Guid.Empty
            && string.Equals(target.ToString("D"), targetId, StringComparison.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["httpMethod"] = context.Request.Method,
            ["routeTemplate"] = routeId,
            ["reason"] = reason,
            ["validationFields"] = string.Join(',', validationFields.Order(StringComparer.Ordinal))
        };
        if (expectedVersion is not null) metadata["expectedVersion"] = expectedVersion;
        return audit.RecordRejectionAsync(new(
            action, code, actor,
            canonicalTarget ? "user" : "http_route",
            canonicalTarget ? targetId : routeId,
            "rejected", context.TraceIdentifier, metadata), context.RequestAborted);
    }

    private static Guid? TryActor(ClaimsPrincipal principal) =>
        Guid.TryParseExact(principal.FindFirstValue(ClaimTypes.NameIdentifier), "D", out var actor)
            ? actor : null;

    private sealed class ExactJsonResult(int statusCode, byte[] body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            httpContext.Response.ContentLength = body.Length;
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted);
        }
    }
}
