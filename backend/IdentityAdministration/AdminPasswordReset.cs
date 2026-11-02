using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public sealed record AdminResetRequest(JsonElement ExpectedManagementVersion);
public sealed record AdminResetCommand(Guid Actor, Guid Target, long Version, Guid Key, DateTimeOffset Now, string? CorrelationId, string ActorSecurityStamp);
public enum AdminResetResult { Succeeded, NotFound, Stale, SelfForbidden, Inactive, ResultUnavailable, KeyReused, ActorStateChanged }
public sealed record AdminResetOutcome(AdminResetResult Result, string? TemporaryPassword = null);

public static class AdminPasswordReset
{
    public static async Task<IResult> EndpointAsync(HttpContext context, string id, AdminResetRequest request,
        [FromServices] IAntiforgery antiforgery, [FromServices] IIdentityRateLimiter limiter, [FromServices] IIdentityAdministrationStore store,
        [FromServices] IPasswordHasher<ApplicationUser> hasher, [FromServices] TimeProvider clock,
        [FromServices] IIdentitySecurityAudit audit)
    {
        IdentityHttpResults.NoStore(context.Response);
        context.Response.Headers.Pragma = "no-cache";
        var actor = CurrentUser.GetRequiredId(context.User);
        async Task Reject(string action, string code, string reason) =>
            await audit.RecordRejectionAsync(new(action, code, actor, "http_route", "admin-user-password-reset", "rejected",
                context.TraceIdentifier, new Dictionary<string, string> { ["httpMethod"] = "POST", ["reason"] = reason }), context.RequestAborted);
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            await Reject(IdentitySecurityActions.CsrfReject, "csrf_invalid", "csrf");
            return Error(400, "csrf_invalid", "Token CSRF inválido.");
        }
        var text = request.ExpectedManagementVersion.ValueKind == JsonValueKind.String
            ? request.ExpectedManagementVersion.GetString() : null;
        var validVersion = text is { Length: > 0 } && text[0] is >= '1' and <= '9'
            && text.All(c => c is >= '0' and <= '9')
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        var keyValid = context.Request.Headers.TryGetValue("Idempotency-Key", out var keys)
            && keys.Count == 1 && IdentityCreationProtocol.TryParseIdempotencyKey(keys[0], out _);
        if (!Guid.TryParseExact(id, "D", out var target) || target == Guid.Empty || target.ToString("D") != id
            || !validVersion || !keyValid)
        {
            await Reject(IdentitySecurityActions.ValidationReject, "validation_failed", "validation");
            return Error(400, "validation_failed", "Los datos de la solicitud no son válidos.");
        }
        if (actor == target)
        {
            await Reject(IdentitySecurityActions.ValidationReject, "self_management_forbidden", "self");
            return Error(409, "self_management_forbidden", "Solicite el restablecimiento a otro administrador.");
        }
        var actorStamp = context.User.FindFirstValue(SessionAuthentication.StampClaim);
        if (string.IsNullOrEmpty(actorStamp))
        {
            await Reject(IdentitySecurityActions.AdminStepUpReject, "admin_reauthentication_required", "missing_security_stamp");
            return Error(403, "admin_reauthentication_required", "Debe volver a confirmar su contraseña de administrador.");
        }
        try
        {
            var quota = await limiter.ConsumeAsync(new("admin-mutation", actor.ToString("D"), null, clock.GetUtcNow()), context.RequestAborted);
            if (!quota.Allowed)
            {
                await Reject(IdentitySecurityActions.RateLimitReject, "rate_limited", "quota_exceeded");
                return Error(429, "rate_limited", "Demasiados intentos. Reintente más tarde.");
            }
            var result = await store.ResetPasswordAsync(new(actor, target, long.Parse(text!, CultureInfo.InvariantCulture),
                Guid.Parse(keys[0]!), clock.GetUtcNow(), context.TraceIdentifier, actorStamp), hasher, context.RequestAborted);
            if (result.Result != AdminResetResult.Succeeded)
                await Reject(result.Result == AdminResetResult.Stale ? IdentitySecurityActions.ConcurrencyReject : IdentitySecurityActions.ValidationReject,
                    result.Result switch { AdminResetResult.ActorStateChanged => "admin_reauthentication_required", AdminResetResult.Stale => "stale_management_version", AdminResetResult.SelfForbidden => "self_management_forbidden", AdminResetResult.KeyReused => "idempotency_key_reused", _ => "validation_failed" }, result.Result.ToString());
            return result.Result switch
            {
                AdminResetResult.Succeeded => Results.Ok(new { temporaryPassword = result.TemporaryPassword }),
                AdminResetResult.ActorStateChanged => Error(403, "admin_reauthentication_required", "Debe volver a confirmar su contraseña de administrador."),
                AdminResetResult.NotFound => Error(404, "user_not_found", "El usuario no existe."),
                AdminResetResult.Stale => Error(409, "stale_management_version", "La cuenta cambió. Recargue los datos."),
                AdminResetResult.SelfForbidden => Error(409, "self_management_forbidden", "Solicite el restablecimiento a otro administrador."),
                AdminResetResult.Inactive => Error(409, "user_inactive", "El usuario está inactivo."),
                AdminResetResult.ResultUnavailable => Error(409, "reset_result_unavailable", "El resultado anterior no está disponible. Solicite otro restablecimiento."),
                AdminResetResult.KeyReused => Error(409, "idempotency_key_reused", "La clave de solicitud ya se usó con otros datos."),
                _ => throw new IdentityAdminUnavailableException()
            };
        }
        catch (IdentityRateLimitUnavailableException) { await Reject(IdentitySecurityActions.RateLimitReject, "service_unavailable", "quota_unavailable"); return Error(503, "service_unavailable", "El servicio no está disponible temporalmente."); }
        catch (IdentityAdminUnavailableException) { return Error(503, "service_unavailable", "El servicio no está disponible temporalmente."); }
    }
    private static IResult Error(int status, string code, string error) => Results.Json(new { error, code }, statusCode: status);
}
