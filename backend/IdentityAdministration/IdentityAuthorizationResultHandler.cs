using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public sealed class IdentityAuthorizationResultHandler(IIdentitySecurityAudit audit)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await _fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var code = authorizeResult.Challenged
            ? "authentication_required"
            : authorizeResult.Forbidden ? "permission_denied" : null;
        if (code is null)
        {
            await _fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        Guid? actorUserId = null;
        var actorClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParseExact(actorClaim, "D", out var parsedActor)) actorUserId = parsedActor;
        await audit.RecordRejectionAsync(new(
            IdentitySecurityActions.AuthorizationReject,
            code,
            actorUserId,
            "authentication",
            "unknown",
            "rejected",
            context.TraceIdentifier,
            new Dictionary<string, string>()), context.RequestAborted);

        await IdentityHttpResults.WriteErrorAsync(
            context,
            authorizeResult.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden,
            code,
            authorizeResult.Challenged ? "Se requiere una sesión activa." : "No cuenta con permisos para realizar esta operación.");
    }
}
