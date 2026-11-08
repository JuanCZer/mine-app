using System.Security.Claims;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public sealed class AdminStepUpFilter(
    AdminStepUpService stepUp,
    IIdentitySecurityAudit audit) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var validation = await stepUp.ValidateAsync(
            context.HttpContext, context.HttpContext.RequestAborted);
        if (validation.IsValid) return await next(context);

        Guid? actor = Guid.TryParseExact(
            context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), "D", out var parsed)
            ? parsed : null;
        await audit.RecordRejectionAsync(new(
            IdentitySecurityActions.AdminStepUpReject,
            "admin_reauthentication_required",
            actor,
            "http_route",
            RouteId(context.HttpContext),
            "rejected",
            context.HttpContext.TraceIdentifier,
            new Dictionary<string, string>
            {
                ["httpMethod"] = context.HttpContext.Request.Method,
                ["reason"] = validation.FailureReason ?? "invalid"
            }), context.HttpContext.RequestAborted);
        IdentityHttpResults.NoStore(context.HttpContext.Response);
        return Results.Json(new
        {
            error = "Debe volver a confirmar su contraseña de administrador.",
            code = "admin_reauthentication_required"
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static string RouteId(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<AdminStepUpRouteMetadata>()?.RouteId
        ?? throw new InvalidOperationException("Administrative StepUp route metadata is required.");
}

public sealed record AdminStepUpRouteMetadata(string RouteId);

public static class AdminStepUpEndpointExtensions
{
    private static readonly HashSet<string> MutationRouteIds =
        ["admin-users-create", "admin-user-role", "admin-user-status", "admin-user-password-reset"];

    public static RouteHandlerBuilder RequireAdminStepUp(
        this RouteHandlerBuilder builder,
        string routeId)
    {
        if (!MutationRouteIds.Contains(routeId))
            throw new ArgumentException("Unknown administrative mutation route.", nameof(routeId));
        return builder
            .WithMetadata(new AdminStepUpRouteMetadata(routeId))
            .AddEndpointFilter<AdminStepUpFilter>();
    }
}
