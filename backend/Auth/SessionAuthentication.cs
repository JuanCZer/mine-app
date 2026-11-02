using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Models;

namespace Notes.Api.Auth;

public static class SessionAuthentication
{
    public const string StampClaim = "gds:security-stamp";
    public const string DisplayNameClaim = "gds:display-name";
    public const string MustChangePasswordClaim = "gds:must-change-password";
    public const string CreatedAtClaim = "gds:created-at";
    private static readonly HashSet<string> CanonicalRoles = ["operador", "validador", "administrador"];

    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var keyPath = config["DataProtection:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            if (!local) throw new InvalidOperationException("DataProtection:KeyRingPath is required outside Development/Testing.");
            keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GdsMine", "data-protection", environment.EnvironmentName);
        }
        services.AddDataProtection().SetApplicationName("GdsMine.Authentication.v1")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keyPath)));
        var authentication = services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = "GDS.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = local ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => IdentityHttpResults.WriteErrorAsync(
                    context.HttpContext, StatusCodes.Status401Unauthorized,
                    "authentication_required", "Se requiere una sesión activa.");
                options.Events.OnRedirectToAccessDenied = context => IdentityHttpResults.WriteErrorAsync(
                    context.HttpContext, StatusCodes.Status403Forbidden,
                    "permission_denied", "No tiene permisos para realizar esta acción.");
                options.Events.OnValidatePrincipal = async context =>
                {
                    var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var stamp = context.Principal?.FindFirstValue(StampClaim);
                    var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationIdentityDbContext>();
                    var user = Guid.TryParse(id, out var uuid) && uuid != Guid.Empty && !string.IsNullOrEmpty(stamp)
                        ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uuid, context.HttpContext.RequestAborted) : null;
                    if (user is not { IsActive: true } || user.SecurityStamp != stamp)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                        return;
                    }
                    try
                    {
                        // Refresh role and mandatory-password state on every request without renewing the cookie.
                        context.ReplacePrincipal(await PrincipalAsync(user, db, context.HttpContext.RequestAborted));
                        context.ShouldRenew = false;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    }
                };
            });
        if (environment.IsEnvironment("Testing") && config.GetValue<bool>("Authentication:EnableTestHeaders"))
        {
            authentication.AddScheme<AuthenticationSchemeOptions, TestingHeaderAuthentication>("TestingHeader", _ => { });
            authentication.AddPolicyScheme("TestingOrCookie", null, options =>
                options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("X-Test-User") ? "TestingHeader" : IdentityConstants.ApplicationScheme);
            services.Configure<AuthenticationOptions>(options => options.DefaultScheme = "TestingOrCookie");
        }
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy("CanValidatePhase", policy => policy.RequireAuthenticatedUser().RequireRole("validador", "administrador"));
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = "GDS.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });
        return services;
    }

    public static async Task<ClaimsPrincipal> PrincipalAsync(ApplicationUser user, ApplicationIdentityDbContext database, CancellationToken cancellationToken = default)
    {
        var roles = await (from membership in database.UserRoles
                           join role in database.Roles on membership.RoleId equals role.Id
                           where membership.UserId == user.Id select role.Name!).ToListAsync(cancellationToken);
        if (roles.Count != 1 || roles[0] is not { } canonicalRole || !CanonicalRoles.Contains(canonicalRole))
            throw new UnauthorizedAccessException("La cuenta no tiene una asignación de rol válida.");
        var claims = new List<Claim> {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.UserName!),
            new(DisplayNameClaim, user.DisplayName), new(StampClaim, user.SecurityStamp!),
            new(MustChangePasswordClaim, user.MustChangePassword ? "true" : "false"),
            new(CreatedAtClaim, user.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
        };
        claims.Add(new Claim(ClaimTypes.Role, canonicalRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
    }

    public static PublicUser Public(ClaimsPrincipal principal)
    {
        var permissions = new List<string> { "incidents:read", "incidents:write" };
        if (principal.IsInRole("validador") || principal.IsInRole("administrador")) permissions.Add("phases:validate");
        if (principal.IsInRole("administrador")) permissions.AddRange(["users:provision", "catalogs:manage"]);
        var mustChangePassword = string.Equals(principal.FindFirstValue(MustChangePasswordClaim), "true", StringComparison.Ordinal);
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? "operador";
        var createdAtClaim = principal.FindFirstValue(CreatedAtClaim);
        var createdAt = string.IsNullOrWhiteSpace(createdAtClaim)
            ? null
            : DateTimeOffset.TryParse(createdAtClaim, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToString("O", CultureInfo.InvariantCulture)
                : null;
        return new(CurrentUser.GetRequiredId(principal).ToString(), principal.Identity!.Name ?? "", principal.FindFirstValue(DisplayNameClaim) ?? "", role, permissions.ToArray(), mustChangePassword, createdAt);
    }
}
