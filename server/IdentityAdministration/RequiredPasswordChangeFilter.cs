using Notes.Api.Auth;
using System.Security.Claims;

namespace Notes.Api.IdentityAdministration;

public sealed class RequiredPasswordChangeFilter(RequestDelegate next, IIdentitySecurityAudit audit)
{
    private static readonly HashSet<(string Method, string Path)> AllowedRoutes =
    [
        (HttpMethods.Get, "/api/auth/me"),
        (HttpMethods.Get, "/api/auth/csrf"),
        (HttpMethods.Post, "/api/auth/change-password"),
        (HttpMethods.Post, "/api/auth/logout")
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var requiresChange = context.User.Identity?.IsAuthenticated == true
            && string.Equals(context.User.FindFirst(SessionAuthentication.MustChangePasswordClaim)?.Value, "true", StringComparison.Ordinal);
        var path = context.Request.Path.Value ?? string.Empty;
        if (requiresChange && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !AllowedRoutes.Contains((context.Request.Method, path)))
        {
            Guid? actorUserId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : null;
            await audit.RecordRejectionAsync(new(
                IdentitySecurityActions.PasswordChangeRequiredReject,
                "password_change_required",
                actorUserId,
                "authentication",
                "unknown",
                "rejected",
                context.TraceIdentifier,
                new Dictionary<string, string>()), context.RequestAborted);
            await IdentityHttpResults.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "password_change_required", "Debe cambiar su contraseña antes de continuar.");
            return;
        }
        await next(context);
    }
}
