using System.Security.Claims;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Models;
using Notes.Api.IdentityAdministration;

namespace Notes.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery, IHostEnvironment environment) =>
        {
            IssueCsrf(context, antiforgery, environment);
            return Results.NoContent();
        }).AllowAnonymous();
        endpoints.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, AuthenticationService authentication,
            ApplicationIdentityDbContext database, IAuthenticationAuditStore audit, IAntiforgery antiforgery,
            IHostEnvironment environment, IIdentityRateLimiter rateLimiter, ILookupNormalizer normalizer, TimeProvider clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var username = request.Username?.Trim();
            var hasCandidateCredentials = !string.IsNullOrWhiteSpace(username)
                && PasswordInputPolicy.HasAllowedLength(request.Password);
            if (hasCandidateCredentials)
            {
                var normalizedUsername = normalizer.NormalizeName(username!);
                try
                {
                    var quota = await rateLimiter.ConsumeAsync(new(
                        "login",
                        normalizedUsername,
                        NormalizeIp(context.Connection.RemoteIpAddress),
                        clock.GetUtcNow()), context.RequestAborted);
                    if (!quota.Allowed)
                    {
                        if (quota.RetryAfter is { } retryAt)
                        {
                            var seconds = Math.Max(1, (int)Math.Ceiling((retryAt - clock.GetUtcNow()).TotalSeconds));
                            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }
                        await audit.WriteAsync(Audit(context, "auth.login", "rejected", null, "anonymous", "rate_limited"), context.RequestAborted);
                        return Results.Json(new { error = "Demasiados intentos. Reintente más tarde.", code = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);
                    }
                }
                catch (IdentityRateLimitUnavailableException)
                {
                    await audit.WriteAsync(Audit(context, "auth.login", "rejected", null, "anonymous", "service_unavailable"), context.RequestAborted);
                    return Results.Json(new { error = "El servicio no está disponible temporalmente.", code = "service_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var attempt = await authentication.AuthenticateAttemptAsync(request.Username, request.Password);
            if (attempt.User is not { } user)
            {
                await audit.WriteAsync(Audit(context, "auth.login", "rejected", null, "anonymous", attempt.AuditReason), context.RequestAborted);
                return Results.Unauthorized();
            }
            var principal = await SessionAuthentication.PrincipalAsync(user, database, context.RequestAborted);
            try
            {
                await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
                    new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
                await audit.WriteAsync(Audit(context, "auth.login", "succeeded", user.Id, user.Id.ToString(), null), context.RequestAborted);
            }
            catch
            {
                // Cookie authentication is stateless. Removing the pending response cookie
                // compensates both a partial handler failure and an audit failure after signing.
                context.Response.Headers.Remove("Set-Cookie");
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                throw;
            }
            context.User = principal;
            IssueCsrf(context, antiforgery, environment);
            return Results.Ok(new LoginResponse(SessionAuthentication.Public(principal)));
        }).AllowAnonymous().AddEndpointFilter<CsrfFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024));
        endpoints.MapGet("/api/auth/me", (HttpContext context) => Results.Ok(SessionAuthentication.Public(context.User)));
        endpoints.MapPost("/api/auth/reauth", ReauthenticateAsync)
            .RequireAuthorization("AdministerUsers");
        endpoints.MapPost("/api/auth/change-password", ChangePasswordAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/auth/logout", async (HttpContext context, ApplicationIdentityDbContext database,
            RuntimeIdentityStore users, IAuthenticationAuditStore audit, IAntiforgery antiforgery,
            IHostEnvironment environment, AdminStepUpService stepUp) =>
        {
            var id = CurrentUser.GetRequiredId(context.User);
            // Retry a racing login/failed-password update without broadening the runtime SQL grants.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, context.RequestAborted);
                if (user is not { IsActive: true })
                {
                    await audit.WriteAsync(Audit(context, "auth.logout", "failed", id, id.ToString(), "account_unavailable"), context.RequestAborted);
                    return Results.Unauthorized();
                }
                if (!await users.RotateSecurityStampAsync(user)) continue;
                await audit.WriteAsync(Audit(context, "auth.logout", "succeeded", id, id.ToString(), null), context.RequestAborted);
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                stepUp.Clear(context);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                IssueCsrf(context, antiforgery, environment);
                return Results.NoContent();
            }
            await audit.WriteAsync(Audit(context, "auth.logout", "failed", id, id.ToString(), "account_state_changed"), context.RequestAborted);
            return Results.Json(new { error = "No se pudo cerrar la sesión. Reintente." }, statusCode: 503);
        }).AddEndpointFilter<CsrfFilter>();
        return endpoints;
    }

    public static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IIdentityRateLimiter rateLimiter,
        IIdentityAdministrationStore store,
        IIdentitySecurityAudit rejectionAudit,
        IPasswordHasher<ApplicationUser> hasher,
        AdminStepUpService stepUp,
        TimeProvider clock)
    {
        IdentityHttpResults.NoStore(context.Response);
        var actor = CurrentUser.GetRequiredId(context.User);
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await RejectPasswordChangeAsync(context, rejectionAudit,
                IdentitySecurityActions.CsrfReject, "csrf_invalid", actor, "csrf", []);
            return Error(StatusCodes.Status400BadRequest, "csrf_invalid", "Token CSRF inválido.");
        }

        var read = await ReadChangePasswordRequestAsync(context.Request, context.RequestAborted);
        if (read.TooLarge)
        {
            await RejectPasswordChangeAsync(context, rejectionAudit,
                IdentitySecurityActions.ValidationReject, "validation_failed", actor,
                "body_size", ["body"]);
            return Error(StatusCodes.Status413PayloadTooLarge, "validation_failed", "Solicitud inválida.");
        }
        if (read.Request is null)
        {
            await RejectPasswordChangeAsync(context, rejectionAudit,
                IdentitySecurityActions.ValidationReject, "validation_failed", actor,
                "validation", read.FieldErrors.Keys);
            return ValidationError(read.FieldErrors);
        }

        IdentityRateLimitDecision quota;
        try
        {
            quota = await rateLimiter.ConsumeAsync(new(
                "change-password", actor.ToString("D"), null, clock.GetUtcNow()),
                context.RequestAborted);
        }
        catch (IdentityRateLimitUnavailableException)
        {
            await RejectPasswordChangeAsync(context, rejectionAudit,
                IdentitySecurityActions.RateLimitReject, "service_unavailable", actor,
                "quota_unavailable", []);
            return Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                "El servicio no está disponible temporalmente.");
        }
        if (!quota.Allowed)
        {
            if (quota.RetryAfter is { } retryAt)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(
                    (retryAt - clock.GetUtcNow()).TotalSeconds));
                context.Response.Headers.RetryAfter = seconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            await RejectPasswordChangeAsync(context, rejectionAudit,
                IdentitySecurityActions.RateLimitReject, "rate_limited", actor,
                "quota_exceeded", []);
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited",
                "Demasiados intentos. Reintente más tarde.");
        }

        ChangePasswordOutcome outcome;
        try
        {
            outcome = await store.ChangePasswordAsync(new(
                actor, read.Request.CurrentPassword, read.Request.NewPassword,
                clock.GetUtcNow(), context.TraceIdentifier), hasher, context.RequestAborted);
        }
        catch (IdentityAdminUnavailableException)
        {
            await RejectPasswordChangeAsync(context, rejectionAudit,
                IdentitySecurityActions.ConcurrencyReject, "service_unavailable", actor,
                "password_change_unavailable", []);
            return Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                "El servicio no está disponible temporalmente.");
        }

        if (outcome != ChangePasswordOutcome.Succeeded)
        {
            var (status, code, message, action, reason) = outcome switch
            {
                ChangePasswordOutcome.CurrentPasswordInvalid =>
                    (StatusCodes.Status400BadRequest, "current_password_invalid",
                        "La contraseña actual no es válida.",
                        IdentitySecurityActions.ValidationReject, "current_password"),
                ChangePasswordOutcome.PasswordReuse =>
                    (StatusCodes.Status400BadRequest, "password_reuse",
                        "La nueva contraseña debe ser distinta de la actual.",
                        IdentitySecurityActions.ValidationReject, "password_reuse"),
                ChangePasswordOutcome.CredentialStateChanged =>
                    (StatusCodes.Status409Conflict, "credential_state_changed",
                        "Las credenciales cambiaron. Inicie sesión nuevamente.",
                        IdentitySecurityActions.ConcurrencyReject, "credential_state"),
                _ => throw new InvalidOperationException("Unknown password-change outcome.")
            };
            await RejectPasswordChangeAsync(context, rejectionAudit, action, code, actor, reason, []);
            return Error(status, code, message);
        }

        // The protected transaction has committed before either browser cookie is touched.
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        stepUp.Clear(context);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        IdentityHttpResults.NoStore(context.Response);
        return Results.NoContent();
    }

    public static bool HasValidNewPasswordLength(string? password)
    {
        if (password is null) return false;
        var count = 0;
        foreach (var _ in password.EnumerateRunes())
            if (++count > PasswordInputPolicy.MaximumLength) return false;
        return count >= 15;
    }

    private static async Task<ChangePasswordReadResult> ReadChangePasswordRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 4 * 1024;
        if (request.ContentLength > maximumBytes)
            return new(null, true, new Dictionary<string, string[]>());
        await using var buffer = new MemoryStream();
        var block = new byte[1024];
        while (true)
        {
            var count = await request.Body.ReadAsync(block.AsMemory(), cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes)
                return new(null, true, new Dictionary<string, string[]>());
            await buffer.WriteAsync(block.AsMemory(0, count), cancellationToken);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? currentPassword = null;
        string? newPassword = null;
        string? confirmation = null;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors["body"] = ["El cuerpo debe ser un objeto JSON."];
            }
            else
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        errors[property.Name is "currentPassword" or "newPassword" or "confirmation"
                            ? property.Name : "body"] = ["El campo JSON no puede aparecer más de una vez."];
                        continue;
                    }
                    if (property.Name is not ("currentPassword" or "newPassword" or "confirmation"))
                    {
                        errors["body"] = ["El cuerpo contiene campos no permitidos."];
                        continue;
                    }
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        errors[property.Name] = ["El campo debe ser un string."];
                        continue;
                    }
                    var value = property.Value.GetString();
                    if (property.Name == "currentPassword") currentPassword = value;
                    else if (property.Name == "newPassword") newPassword = value;
                    else confirmation = value;
                }
            }
        }
        catch (JsonException)
        {
            errors["body"] = ["El cuerpo JSON no es válido."];
        }

        if (!PasswordInputPolicy.HasAllowedLength(currentPassword))
            errors["currentPassword"] = ["La contraseña actual es obligatoria y admite hasta 128 caracteres."];
        if (!HasValidNewPasswordLength(newPassword))
            errors["newPassword"] = ["La nueva contraseña debe tener de 15 a 128 caracteres."];
        if (confirmation is null || !string.Equals(newPassword, confirmation, StringComparison.Ordinal))
            errors["confirmation"] = ["La confirmación no coincide con la nueva contraseña."];
        return errors.Count == 0
            ? new(new(currentPassword!, newPassword!), false, errors)
            : new(null, false, errors);
    }

    private static IResult ValidationError(IReadOnlyDictionary<string, string[]> fieldErrors) =>
        Results.Json(new
        {
            error = "Los datos de contraseña no son válidos.",
            code = "validation_failed",
            fieldErrors
        }, statusCode: StatusCodes.Status400BadRequest);

    private static async Task RejectPasswordChangeAsync(
        HttpContext context,
        IIdentitySecurityAudit audit,
        string action,
        string code,
        Guid actor,
        string reason,
        IEnumerable<string> validationFields)
    {
        try
        {
            await audit.RecordRejectionAsync(new(
                action, code, actor, "http_route", "auth-change-password", "rejected",
                context.TraceIdentifier, new Dictionary<string, string>
                {
                    ["httpMethod"] = HttpMethods.Post,
                    ["routeTemplate"] = "auth-change-password",
                    ["reason"] = reason,
                    ["validationFields"] = string.Join(',', validationFields.Order(StringComparer.Ordinal))
                }), context.RequestAborted);
        }
        catch
        {
            // Rejection auditing is best-effort and must never replace the protected response.
        }
    }

    private sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    private sealed record ChangePasswordReadResult(
        ChangePasswordRequest? Request,
        bool TooLarge,
        IReadOnlyDictionary<string, string[]> FieldErrors);

    public static async Task<IResult> ReauthenticateAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IIdentityRateLimiter rateLimiter,
        IPasswordHasher<ApplicationUser> hasher,
        ApplicationIdentityDbContext database,
        IAuthenticationAuditStore successAudit,
        IIdentitySecurityAudit rejectionAudit,
        AdminStepUpService stepUp,
        TimeProvider clock)
    {
        IdentityHttpResults.NoStore(context.Response);
        var actor = CurrentUser.GetRequiredId(context.User);
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await RejectAsync(context, rejectionAudit, IdentitySecurityActions.CsrfReject,
                "csrf_invalid", actor, "csrf");
            return Error(StatusCodes.Status400BadRequest, "csrf_invalid", "Token CSRF inválido.");
        }

        var read = await ReadReauthenticationRequestAsync(context.Request, context.RequestAborted);
        if (read.TooLarge)
        {
            await RejectAsync(context, rejectionAudit, IdentitySecurityActions.ValidationReject,
                "validation_failed", actor, "body_size");
            return Error(StatusCodes.Status413PayloadTooLarge, "validation_failed", "Solicitud inválida.");
        }
        if (read.Password is null || !PasswordInputPolicy.HasAllowedLength(read.Password))
        {
            await RejectAsync(context, rejectionAudit, IdentitySecurityActions.ValidationReject,
                "reauthentication_failed", actor, "password_length");
            return Error(StatusCodes.Status400BadRequest, "reauthentication_failed", "No fue posible confirmar la contraseña.");
        }

        IdentityRateLimitDecision quota;
        try
        {
            var now = clock.GetUtcNow();
            quota = await rateLimiter.ConsumeAsync(new(
                "reauth", actor.ToString("D"), NormalizeIp(context.Connection.RemoteIpAddress),
                now), context.RequestAborted);
        }
        catch (IdentityRateLimitUnavailableException)
        {
            await RejectAsync(context, rejectionAudit, IdentitySecurityActions.RateLimitReject,
                "service_unavailable", actor, "quota_unavailable");
            return Error(StatusCodes.Status503ServiceUnavailable, "service_unavailable", "El servicio no está disponible temporalmente.");
        }
        if (!quota.Allowed)
        {
            if (quota.RetryAfter is { } retryAt)
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((retryAt - clock.GetUtcNow()).TotalSeconds)).ToString();
            await RejectAsync(context, rejectionAudit, IdentitySecurityActions.RateLimitReject,
                "rate_limited", actor, "quota_exceeded");
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Demasiados intentos. Reintente más tarde.");
        }

        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor, context.RequestAborted);
        var roles = user is null
            ? []
            : await (from membership in database.UserRoles.AsNoTracking()
                     join role in database.Roles.AsNoTracking() on membership.RoleId equals role.Id
                     where membership.UserId == actor
                     select role.Name).ToListAsync(context.RequestAborted);
        var verified = user is { IsActive: true, PasswordHash: not null }
            && roles.Count == 1
            && string.Equals(roles[0], "administrador", StringComparison.Ordinal)
            && hasher.VerifyHashedPassword(user, user.PasswordHash, read.Password)
                != PasswordVerificationResult.Failed;
        if (!verified)
        {
            await RejectAsync(context, rejectionAudit, IdentitySecurityActions.AuthorizationReject,
                "reauthentication_failed", actor, "credentials");
            return Error(StatusCodes.Status400BadRequest, "reauthentication_failed", "No fue posible confirmar la contraseña.");
        }

        await successAudit.WriteAsync(Audit(context, "auth.admin.reauthenticate", "succeeded",
            actor, actor.ToString("D"), null), context.RequestAborted);
        stepUp.Issue(context, user!);
        return Results.NoContent();
    }

    private static async Task<(string? Password, bool TooLarge)> ReadReauthenticationRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 4 * 1024;
        if (request.ContentLength > maximumBytes) return (null, true);
        await using var buffer = new MemoryStream();
        var block = new byte[1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(block.AsMemory(0, block.Length), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) return (null, true);
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("password", out var password)
                || password.ValueKind != JsonValueKind.String)
                return (null, false);
            return (password.GetString(), false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static async Task RejectAsync(
        HttpContext context,
        IIdentitySecurityAudit audit,
        string action,
        string code,
        Guid actor,
        string reason) =>
        await audit.RecordRejectionAsync(new(action, code, actor, "http_route", "admin-reauthenticate",
            "rejected", context.TraceIdentifier, new Dictionary<string, string>
            {
                ["httpMethod"] = HttpMethods.Post,
                ["reason"] = reason,
                ["routeTemplate"] = "admin-reauthenticate"
            }), context.RequestAborted);

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { error = message, code }, statusCode: statusCode);

    private static string NormalizeIp(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString().ToLowerInvariant();
    }

    private static AuthenticationAuditRecord Audit(
        HttpContext context,
        string action,
        string outcome,
        Guid? actorUserId,
        string entityId,
        string? reason) =>
        new(action, outcome, actorUserId, entityId, reason,
            context.TraceIdentifier.Length <= 128 ? context.TraceIdentifier : context.TraceIdentifier[..128]);

    private static void IssueCsrf(HttpContext context, IAntiforgery antiforgery, IHostEnvironment environment)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions {
            HttpOnly = false, Path = "/", SameSite = SameSiteMode.Lax,
            Secure = !(environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        });
        context.Response.Headers.CacheControl = "no-store";
    }
}

public sealed class CsrfFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Token CSRF inválido." }); }
        }
        return await next(context);
    }
}
