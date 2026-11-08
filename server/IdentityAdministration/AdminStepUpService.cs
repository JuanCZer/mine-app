using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Auth;

namespace Notes.Api.IdentityAdministration;

public sealed record AdminStepUpValidation(bool IsValid, string? FailureReason = null)
{
    public static AdminStepUpValidation Valid { get; } = new(true);
    public static AdminStepUpValidation Invalid(string reason) => new(false, reason);
}

public sealed class AdminStepUpService
{
    public const string CookieName = "GDS.AdminStepUp";
    public const string ProtectorPurpose = "GdsMine.IdentityAdministration.AdminStepUp.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _protector;
    private readonly ApplicationIdentityDbContext _database;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _clock;

    public AdminStepUpService(
        IDataProtectionProvider protection,
        ApplicationIdentityDbContext database,
        IHostEnvironment environment,
        TimeProvider clock)
    {
        _protector = protection.CreateProtector(ProtectorPurpose);
        _database = database;
        _environment = environment;
        _clock = clock;
    }

    public void Issue(HttpContext context, ApplicationUser user)
    {
        var issuedAt = _clock.GetUtcNow();
        var expiresAt = issuedAt.Add(Lifetime);
        var payload = JsonSerializer.Serialize(new Payload(
            user.Id.ToString("D"), user.SecurityStamp ?? string.Empty, issuedAt, expiresAt));
        context.Response.Cookies.Append(CookieName, _protector.Protect(payload), CookieOptions(expiresAt));
    }

    public async Task<AdminStepUpValidation> ValidateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var protectedValue)
            || string.IsNullOrWhiteSpace(protectedValue))
            return AdminStepUpValidation.Invalid("missing");

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(protectedValue));
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            Clear(context);
            return AdminStepUpValidation.Invalid("invalid");
        }

        var now = _clock.GetUtcNow();
        var actorClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (payload is null
            || !Guid.TryParseExact(payload.UserId, "D", out var payloadUserId)
            || payloadUserId == Guid.Empty
            || !Guid.TryParseExact(actorClaim, "D", out var actorUserId)
            || actorUserId != payloadUserId
            || string.IsNullOrEmpty(payload.SecurityStamp)
            || payload.ExpiresAt <= now
            || payload.ReauthenticatedAt > now
            || payload.ExpiresAt - payload.ReauthenticatedAt != Lifetime)
        {
            Clear(context);
            return AdminStepUpValidation.Invalid("expired_or_mismatched");
        }

        var state = await (from user in _database.Users.AsNoTracking()
                           where user.Id == actorUserId
                           select new
                           {
                               user.IsActive,
                               user.SecurityStamp,
                               Roles = (from membership in _database.UserRoles
                                        join role in _database.Roles on membership.RoleId equals role.Id
                                        where membership.UserId == user.Id
                                        select role.Name).ToList()
                           }).SingleOrDefaultAsync(cancellationToken);
        if (state is not { IsActive: true }
            || !string.Equals(state.SecurityStamp, payload.SecurityStamp, StringComparison.Ordinal)
            || state.Roles.Count != 1
            || !string.Equals(state.Roles[0], "administrador", StringComparison.Ordinal))
        {
            Clear(context);
            return AdminStepUpValidation.Invalid("security_state_changed");
        }

        return AdminStepUpValidation.Valid;
    }

    public void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, CookieOptions(DateTimeOffset.UnixEpoch));

    private CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = !(_environment.IsDevelopment() || _environment.IsEnvironment("Testing")),
        SameSite = SameSiteMode.Strict,
        Path = "/api/admin",
        Expires = expiresAt,
        MaxAge = expiresAt == DateTimeOffset.UnixEpoch ? TimeSpan.Zero : Lifetime,
        IsEssential = true
    };

    private sealed record Payload(
        string UserId,
        string SecurityStamp,
        DateTimeOffset ReauthenticatedAt,
        DateTimeOffset ExpiresAt);
}
