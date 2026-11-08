using Microsoft.AspNetCore.Identity;

namespace Notes.Api.Auth;

public sealed class AuthenticationService(
    RuntimeIdentityStore users,
    ILookupNormalizer normalizer,
    IPasswordHasher<ApplicationUser> hasher)
{
    public async Task<AuthenticationAttempt> AuthenticateAttemptAsync(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || !PasswordInputPolicy.HasAllowedLength(password))
            return AuthenticationAttempt.Rejected(AuthenticationRejection.InvalidCredentials);
        var suppliedPassword = password!;
        var user = await users.FindByNameAsync(normalizer.NormalizeName(username.Trim()));
        if (user is null || user.PasswordHash is null)
        {
            // Derive and discard an ephemeral hash of attacker-supplied input. This
            // pays the configured work factor without retaining a dummy credential.
            _ = hasher.HashPassword(new ApplicationUser(), suppliedPassword);
            return AuthenticationAttempt.Rejected(AuthenticationRejection.InvalidCredentials);
        }
        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, suppliedPassword);
        if (!user.IsActive) return AuthenticationAttempt.Rejected(AuthenticationRejection.Inactive);
        if (user.LockoutEnabled && user.LockoutEnd >= DateTimeOffset.UtcNow)
            return AuthenticationAttempt.Rejected(AuthenticationRejection.LockedOut);
        if (verification == PasswordVerificationResult.Failed)
        {
            await users.RecordFailedAccessAsync(user);
            return AuthenticationAttempt.Rejected(AuthenticationRejection.InvalidCredentials);
        }
        var upgradedHash = verification == PasswordVerificationResult.SuccessRehashNeeded
            ? hasher.HashPassword(user, suppliedPassword) : null;
        return await users.CompleteSignInAsync(user, upgradedHash)
            ? AuthenticationAttempt.Succeeded(user)
            : AuthenticationAttempt.Rejected(AuthenticationRejection.AccountStateChanged);
    }

    // All credential/account failures have the same public result. Session creation
    // is the caller's responsibility; this service never accepts an inactive user.
    public async Task<ApplicationUser?> AuthenticateAsync(string? username, string? password) =>
        (await AuthenticateAttemptAsync(username, password)).User;

}

public enum AuthenticationRejection
{
    None,
    InvalidCredentials,
    Inactive,
    LockedOut,
    AccountStateChanged
}

public sealed record AuthenticationAttempt(ApplicationUser? User, AuthenticationRejection Rejection)
{
    public string? AuditReason => Rejection switch
    {
        AuthenticationRejection.InvalidCredentials => "invalid_credentials",
        AuthenticationRejection.Inactive => "inactive",
        AuthenticationRejection.LockedOut => "locked_out",
        AuthenticationRejection.AccountStateChanged => "account_state_changed",
        _ => null
    };

    public static AuthenticationAttempt Succeeded(ApplicationUser user) => new(user, AuthenticationRejection.None);
    public static AuthenticationAttempt Rejected(AuthenticationRejection rejection) => new(null, rejection);
}
