using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Notes.Api.Auth;

// Registered only under Testing plus an explicit opt-in. Never replaces cookies elsewhere.
internal sealed class TestingHeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsEnvironment("Testing")) return Task.FromResult(AuthenticateResult.Fail("Testing only."));
        if (!Request.Headers.TryGetValue("X-Test-User", out var value)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Guid.TryParse(value.ToString(), out var id) || id == Guid.Empty) return Task.FromResult(AuthenticateResult.Fail("Invalid test UUID."));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, "Testing") };
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(role => ProvisionUserCommand.MinimumRoles.Contains(role)).Select(role => new Claim(ClaimTypes.Role, role)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
