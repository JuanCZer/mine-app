// Kestrel HTTP uses persistent SQLite for ordinary scenarios. The runner-owned
// Task 13 scenario explicitly supplies disposable PostgreSQL connections.
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notes.Api.Auth;
using Notes.Api.Models;
using Notes.Api.Data;
using Notes.Api.IdentityAdministration;
using Npgsql;
using NpgsqlTypes;

Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_NOTES_API", Path.GetFullPath("backend"));
var scenario = args[0];
var realPasswordChange = scenario == "change-password-real-postgres";
Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", scenario=="forwarded-untrusted"?"true":null);
var environment = scenario switch {
    "secure" or "headers-production-https" or "headers-production-http" or "forwarded-untrusted" or "forwarded-trusted" => "Production",
    "development" or "headers-development-https" => "Development",
    _ => "Testing"
};
Environment.SetEnvironmentVariable("Notes__Provider", environment == "Testing" ? "Json" : "PostgreSql");
Environment.SetEnvironmentVariable("Catalogs__Provider", environment == "Testing" ? "InMemory" : "PostgreSql");
Environment.SetEnvironmentVariable("Authentication__EnableTestHeaders", environment == "Testing" ? "false" : "true");
Environment.SetEnvironmentVariable("Notes__DataPath", Path.Combine(args[1], "notes.json"));
Environment.SetEnvironmentVariable("DataProtection__KeyRingPath", Path.Combine(args[1], "keys"));
var directory = args[1];
var password = "test phrase " + Guid.NewGuid().ToString("N");
var realActorIds = realPasswordChange
    ? Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray()
    : Array.Empty<Guid>();
var realNameSuffix = realPasswordChange ? Guid.NewGuid().ToString("N") : string.Empty;
var realActorNames = realPasswordChange
    ? new[] { "task13.race.", "task13.disable.", "task13.passwordwins.", "task13.privacy." }
        .Select(prefix => prefix + realNameSuffix).ToArray()
    : Array.Empty<string>();
void Check(bool value, string message) { if (!value) throw new Exception("ASSERT: " + message); }
using var factory = new SessionFactory(directory, environment, scenario);
using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
if (scenario is "secure" or "headers-production-https" or "headers-development-https" or "headers-testing-https")
    factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
else factory.UseKestrel(0);
using var bootstrap = factory.CreateClient();
var hashes = new List<string>();
if (realPasswordChange)
{
    var controlValue = Environment.GetEnvironmentVariable("GDS_TEST_CONTROL_CONNECTION")
        ?? throw new Exception("ASSERT: missing disposable PostgreSQL control connection");
    var hasher = new PasswordHasher<ApplicationUser>();
    await using var control = new NpgsqlConnection(controlValue);
    await control.OpenAsync();
    await using var transaction = await control.BeginTransactionAsync();
    await using (var actor = new NpgsqlCommand(
        "SELECT set_config('app.actor_user_id','00000000-0000-4000-8000-000000000001',true)",
        control, transaction))
        _ = await actor.ExecuteScalarAsync();
    for (var index = 0; index < realActorIds.Length; index++)
    {
        var user = new ApplicationUser
        {
            Id = realActorIds[index], UserName = realActorNames[index], NormalizedUserName = realActorNames[index].ToUpperInvariant(),
            DisplayName = "Task 13 disposable actor", IsActive = true, MustChangePassword = true,
            SecurityStamp = "task13-security-" + index, ConcurrencyStamp = "task13-concurrency-" + index,
            ManagementVersion = 10
        };
        var hash = hasher.HashPassword(user, password);
        hashes.Add(hash);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO public."AspNetUsers"
                ("Id","UserName","NormalizedUserName","PasswordHash","SecurityStamp",
                 "ConcurrencyStamp","DisplayName","IsActive","CreatedAt","UpdatedAt",
                 "MustChangePassword","PasswordChangedAt","ManagementVersion")
            VALUES (@id,@username,@normalized,@hash,@security,@concurrency,@display,TRUE,now(),now(),TRUE,NULL,10)
            ON CONFLICT ("Id") DO UPDATE SET
                "UserName"=EXCLUDED."UserName", "NormalizedUserName"=EXCLUDED."NormalizedUserName",
                "PasswordHash"=EXCLUDED."PasswordHash", "SecurityStamp"=EXCLUDED."SecurityStamp",
                "ConcurrencyStamp"=EXCLUDED."ConcurrencyStamp", "DisplayName"=EXCLUDED."DisplayName",
                "IsActive"=TRUE, "UpdatedAt"=now(), "MustChangePassword"=TRUE,
                "PasswordChangedAt"=NULL, "ManagementVersion"=10
            """, control, transaction);
        insert.Parameters.AddWithValue("id", user.Id);
        insert.Parameters.AddWithValue("username", user.UserName);
        insert.Parameters.AddWithValue("normalized", user.NormalizedUserName);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("security", user.SecurityStamp!);
        insert.Parameters.AddWithValue("concurrency", user.ConcurrencyStamp!);
        insert.Parameters.AddWithValue("display", user.DisplayName);
        _ = await insert.ExecuteNonQueryAsync();
        await using var membership = new NpgsqlCommand("""
            INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
            SELECT @id,"Id" FROM public."AspNetRoles"
            WHERE "Name"='operador' AND "NormalizedName"='OPERADOR'
            ON CONFLICT ("UserId","RoleId") DO NOTHING
            """, control, transaction);
        membership.Parameters.AddWithValue("id", user.Id);
        Check(await membership.ExecuteNonQueryAsync() is 0 or 1, "seed canonical operator membership");
    }
    await transaction.CommitAsync();
}
else using (var scope = factory.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
    await db.Database.EnsureCreatedAsync();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var role in new[] {"operador", "validador", "administrador"})
    {
        await roles.CreateAsync(new IdentityRole<Guid>(role) {Id = Guid.NewGuid()});
        foreach (var suffix in new[] {"", "2"})
        {
            var user = new ApplicationUser {UserName = role + suffix, DisplayName = "Display " + role};
            if (scenario is "mandatory-password-routes" or "mandatory-password-audit-before-response" or "rejection-audit-exactly-once" or "change-password-success-state"
                && role == "operador" && suffix == "")
            {
                var property = typeof(ApplicationUser).GetProperty("MustChangePassword");
                Check(property is not null, "ApplicationUser.MustChangePassword must exist");
                property!.SetValue(user, true);
            }
            Check((await users.CreateAsync(user, password)).Succeeded, "seed user");
            Check((await users.AddToRoleAsync(user, role)).Succeeded, "seed role");
            hashes.Add(user.PasswordHash!);
        }
    }
    var inactive = new ApplicationUser {UserName="inactive", IsActive=false};
    await users.CreateAsync(inactive, password);
    var locked = new ApplicationUser {UserName="locked", LockoutEnabled=true, LockoutEnd=DateTimeOffset.UtcNow.AddHours(1)};
    await users.CreateAsync(locked, password);
}
factory.PasswordHasher.Reset();
factory.RateLimiter.Reset();
Browser NewBrowser() => new(bootstrap.BaseAddress!);
async Task<HttpResponseMessage> Csrf(Browser browser)
{
    var response = await browser.Send("GET", "/api/auth/csrf");
    Check(response.StatusCode == HttpStatusCode.NoContent, "CSRF bootstrap must return 204, got " + (int)response.StatusCode);
    return response;
}
async Task<HttpResponseMessage> Login(Browser browser, string username="validador", string? secret=null)
{
    await Csrf(browser);
    return await browser.Send("POST", "/api/auth/login", new {username, password=secret ?? password});
}
async Task ChangeUser(string name, Func<ApplicationUser,Task> change)
{
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
    var user = await db.Users.SingleAsync(x=>x.UserName==name);
    await change(user);
    await db.SaveChangesAsync();
}
var noteInput = new {title="Owned note", content="Indication", state="Area", altitude=19.0, longitude=-99.0, expectedRevision=0};
async Task<JsonElement> Error(HttpResponseMessage response, HttpStatusCode status, string code)
{
    Check(response.StatusCode == status, $"{code}: expected {(int)status}, got {(int)response.StatusCode}");
    Check(response.Headers.CacheControl?.ToString() == "no-store", code + " must use exact Cache-Control: no-store");
    var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    var fields = body.EnumerateObject().Select(x => x.Name).Order().ToArray();
    Check(fields.Contains("error") && fields.Contains("code"), code + " requires error and code");
    Check(fields.All(x => x is "error" or "code" or "fieldErrors" or "traceId"), code + " envelope contains only allowlisted fields");
    Check(body.GetProperty("error").ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()), code + " requires a public message");
    Check(body.GetProperty("code").GetString() == code, "expected error code " + code);
    var serialized = body.GetRawText();
    foreach (var forbidden in new[] { "connection", "stack", "exception", "passwordHash", "securityStamp" })
        Check(!serialized.Contains(forbidden, StringComparison.OrdinalIgnoreCase), code + " must omit infrastructure details");
    return body;
}
using var browser = NewBrowser();
switch (scenario)
{
    case "change-password-csrf":
    {
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "login before change-password CSRF");
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset(); factory.SecurityAudit.Events.Clear();
        await Error(await browser.Send("POST", "/api/auth/change-password",
            new { currentPassword = password, newPassword = "a new valid password phrase", confirmation = "a new valid password phrase" }, csrf: false),
            HttpStatusCode.BadRequest, "csrf_invalid");
        await Error(await browser.SendRaw("POST", "/api/auth/change-password",
            "{" + new string('x', 4096), csrf: false),
            HttpStatusCode.BadRequest, "csrf_invalid");
        Check(factory.PasswordHasher.TotalCalls == 0 && factory.RateLimiter.ConsumeCalls == 0
            && factory.PasswordChangeStore.Calls == 0, "CSRF must precede parsing, quota, store and hasher");
        Check(factory.SecurityAudit.Events.Count == 2 && factory.SecurityAudit.Events.All(item => item.Code == "csrf_invalid"),
            "each CSRF rejection has exactly one endpoint-owned audit before body inspection");
        break;
    }
    case "change-password-body-size":
    {
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "login before body limit");
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset(); factory.SecurityAudit.Events.Clear();
        var body = "{\"currentPassword\":\"x\",\"newPassword\":\"123456789012345\",\"confirmation\":\"123456789012345\"}";
        while (System.Text.Encoding.UTF8.GetByteCount(body) < 4097) body += " ";
        Check(System.Text.Encoding.UTF8.GetByteCount(body) == 4097, "test body is exactly 4097 bytes");
        await Error(await browser.SendRaw("POST", "/api/auth/change-password", body),
            HttpStatusCode.RequestEntityTooLarge, "validation_failed");
        await Error(await browser.SendRaw("POST", "/api/auth/change-password", body, chunked: true),
            HttpStatusCode.RequestEntityTooLarge, "validation_failed");
        Check(factory.PasswordHasher.TotalCalls == 0 && factory.RateLimiter.ConsumeCalls == 0
            && factory.PasswordChangeStore.Calls == 0, "4097 bytes must fail before quota/store/hasher");
        Check(factory.SecurityAudit.Events.Count == 2,
            "Content-Length and chunked 4097-byte bodies are each audited exactly once");
        break;
    }
    case "change-password-current-rune-limit":
    {
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "login before current rune limit");
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset(); factory.SecurityAudit.Events.Clear();
        var oversizedCurrent = string.Concat(Enumerable.Repeat("😀", 129));
        await Error(await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = oversizedCurrent,
            newPassword = "a new valid password phrase",
            confirmation = "a new valid password phrase"
        }), HttpStatusCode.BadRequest, "validation_failed");
        Check(factory.PasswordHasher.TotalCalls == 0 && factory.RateLimiter.ConsumeCalls == 0
            && factory.PasswordChangeStore.Calls == 0, "129 runes must fail before quota/store/hasher");
        break;
    }
    case "change-password-new-rune-boundaries":
    {
        var fourteen = string.Concat(Enumerable.Repeat("😀", 14));
        var fifteen = string.Concat(Enumerable.Repeat("😀", 15));
        var oneTwentyEight = string.Concat(Enumerable.Repeat("😀", 128));
        var oneTwentyNine = string.Concat(Enumerable.Repeat("😀", 129));
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "login for 14/15 boundaries");
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset();
        await Error(await browser.Send("POST", "/api/auth/change-password",
            new { currentPassword = password, newPassword = fourteen, confirmation = fourteen }),
            HttpStatusCode.BadRequest, "validation_failed");
        Check(factory.PasswordHasher.TotalCalls == 0 && factory.RateLimiter.ConsumeCalls == 0
            && factory.PasswordChangeStore.Calls == 0,
            "14 supplementary runes fail before quota/store/hasher");
        var accepted15 = await browser.Send("POST", "/api/auth/change-password",
            new { currentPassword = password, newPassword = fifteen, confirmation = fifteen });
        Check(accepted15.StatusCode == HttpStatusCode.NoContent, "15 supplementary runes accepted");
        Check((await Login(browser, secret: fifteen)).StatusCode == HttpStatusCode.OK, "new 15-rune password authenticates exactly");
        var callsBefore129 = factory.PasswordChangeStore.Calls;
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset();
        await Error(await browser.Send("POST", "/api/auth/change-password",
            new { currentPassword = fifteen, newPassword = oneTwentyNine, confirmation = oneTwentyNine }),
            HttpStatusCode.BadRequest, "validation_failed");
        Check(factory.PasswordHasher.TotalCalls == 0 && factory.RateLimiter.ConsumeCalls == 0
            && factory.PasswordChangeStore.Calls == callsBefore129,
            "129 supplementary runes fail before quota/store/hasher");
        var accepted128 = await browser.Send("POST", "/api/auth/change-password",
            new { currentPassword = fifteen, newPassword = oneTwentyEight, confirmation = oneTwentyEight });
        Check(accepted128.StatusCode == HttpStatusCode.NoContent, "128 supplementary runes accepted");
        break;
    }
    case "change-password-exact-codes":
    {
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "login before exact codes");
        browser.Cookies.Add(browser.Client.BaseAddress!, new Cookie("GDS.AdminStepUp", "preserve-on-rejection", "/api/admin"));
        factory.SecurityAudit.Events.Clear(); factory.RateLimiter.Reset();
        factory.PasswordHasher.Reset(); factory.Order.Clear();
        var wrongCurrentResponse = await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password + "wrong", newPassword = "a different valid password", confirmation = "a different valid password"
        });
        await Error(wrongCurrentResponse, HttpStatusCode.BadRequest, "current_password_invalid");
        Check(!wrongCurrentResponse.Headers.TryGetValues("Set-Cookie", out var rejectedCookies)
            || rejectedCookies.All(value => !value.StartsWith("GDS.Session=", StringComparison.Ordinal)
                && !value.StartsWith("GDS.AdminStepUp=", StringComparison.Ordinal)),
            "rejection neither renews nor deletes session cookies");
        await Error(await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password, newPassword = "a different valid password", confirmation = "does not match new password"
        }), HttpStatusCode.BadRequest, "validation_failed");
        await Error(await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password, newPassword = password, confirmation = password
        }), HttpStatusCode.BadRequest, "password_reuse");
        factory.RateLimiter.DenyNext = true;
        var passwordLimitedResponse = await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password, newPassword = "a different valid password", confirmation = "a different valid password"
        });
        await Error(passwordLimitedResponse, HttpStatusCode.TooManyRequests, "rate_limited");
        Check(passwordLimitedResponse.Headers.RetryAfter is not null, "rate limit exposes stable Retry-After");
        factory.RateLimiter.FailNext = true;
        await Error(await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password, newPassword = "a different valid password", confirmation = "a different valid password"
        }), HttpStatusCode.ServiceUnavailable, "service_unavailable");
        factory.PasswordChangeStore.ForcedOutcome = ChangePasswordOutcome.CredentialStateChanged;
        await Error(await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password, newPassword = "a different valid password", confirmation = "a different valid password"
        }), HttpStatusCode.Conflict, "credential_state_changed");
        factory.PasswordChangeStore.ForcedOutcome = null;
        factory.PasswordChangeStore.Fail = true;
        await Error(await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password, newPassword = "a different valid password", confirmation = "a different valid password"
        }), HttpStatusCode.ServiceUnavailable, "service_unavailable");
        factory.PasswordChangeStore.Fail = false;
        Check(factory.RateLimiter.ConsumeCalls == 6 && factory.PasswordChangeStore.Calls == 4,
            "every syntactically valid attempt consumes quota before the store");
        Check(factory.RateLimiter.Requests.All(request => request.Policy == "change-password"
            && request.NormalizedIp is null), "password quota is actor-only without IP material");
        Check(factory.PasswordHasher.VerifyCalls == 3 && factory.PasswordHasher.HashCalls == 0,
            "wrong-current and reuse verify observed state without generating a hash");
        Check(factory.Order.SequenceEqual(new[]
        {
            "quota", "store", "hasher", "audit", "audit",
            "quota", "store", "hasher", "hasher", "audit",
            "quota", "audit", "quota", "audit",
            "quota", "store", "audit", "quota", "store", "audit"
        }), "valid attempts commit quota before store/hasher and each rejection audits once");
        Check(factory.SecurityAudit.Events.Select(x => x.Code).SequenceEqual(
            new[] { "current_password_invalid", "validation_failed", "password_reuse",
                "rate_limited", "service_unavailable", "credential_state_changed", "service_unavailable" }),
            "each exact rejection has one ordered audit");
        Check((await browser.Send("GET", "/api/auth/me")).StatusCode == HttpStatusCode.OK,
            "rejections preserve the current session");
        Check(browser.Cookies.GetCookies(new Uri(browser.Client.BaseAddress!, "/api/admin/users"))["GDS.AdminStepUp"]?.Value == "preserve-on-rejection",
            "rejections preserve the existing StepUp cookie");
        break;
    }
    case "change-password-policy-errors":
    {
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "login before policy matrix");
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset(); factory.SecurityAudit.Events.Clear();
        foreach (var raw in new[]
        {
            "{", "[]", "null",
            "{\"currentPassword\":null,\"newPassword\":\"123456789012345\",\"confirmation\":\"123456789012345\"}",
            "{\"currentPassword\":\"x\",\"currentPassword\":\"y\",\"newPassword\":\"123456789012345\",\"confirmation\":\"123456789012345\"}",
            "{\"currentPassword\":\"x\",\"newPassword\":\"123456789012345\",\"confirmation\":\"123456789012345\",\"password\":\"unexpected\"}"
        })
            await Error(await browser.SendRaw("POST", "/api/auth/change-password", raw),
                HttpStatusCode.BadRequest, "validation_failed");
        factory.SecurityAudit.FailWrites = true;
        await Error(await browser.SendRaw("POST", "/api/auth/change-password", "{}"),
            HttpStatusCode.BadRequest, "validation_failed");
        factory.SecurityAudit.FailWrites = false;
        Check(factory.PasswordHasher.TotalCalls == 0 && factory.RateLimiter.ConsumeCalls == 0
            && factory.PasswordChangeStore.Calls == 0, "policy failures do not reach quota/store/hasher");
        Check(factory.SecurityAudit.Events.Count == 7
            && factory.SecurityAudit.Events.All(x => x.Code == "validation_failed"),
            "each policy error attempts exactly one audit without changing the response");
        Check(factory.SecurityAudit.Events.All(item => item.Metadata.Values.All(value =>
            !value.Contains(password, StringComparison.Ordinal)
            && !value.Contains("123456789012345", StringComparison.Ordinal)
            && !value.Contains("unexpected", StringComparison.Ordinal))),
            "rejection audit omits password fields and request material");
        break;
    }
    case "change-password-success-state":
    {
        using var otherSession = NewBrowser();
        Check((await Login(browser, "operador")).StatusCode == HttpStatusCode.OK, "mandatory session can log in");
        Check((await Login(otherSession, "operador")).StatusCode == HttpStatusCode.OK, "second old-stamp session exists");
        ApplicationUser before;
        using (var scope = factory.Services.CreateScope())
            before = await scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Users.AsNoTracking()
                .SingleAsync(x => x.UserName == "operador");
        browser.Cookies.Add(browser.Client.BaseAddress!, new Cookie("GDS.AdminStepUp", "opaque-test-value", "/api/admin"));
        factory.PasswordHasher.Reset(); factory.RateLimiter.Reset(); factory.Order.Clear();
        var changed = await browser.Send("POST", "/api/auth/change-password", new
        {
            currentPassword = password,
            newPassword = "the replacement password phrase",
            confirmation = "the replacement password phrase"
        });
        Check(changed.StatusCode == HttpStatusCode.NoContent, "password change returns 204");
        Check(changed.Headers.CacheControl?.ToString() == "no-store", "204 remains no-store");
        var setCookies = changed.Headers.GetValues("Set-Cookie").ToArray();
        Check(setCookies.Any(value => value.StartsWith("GDS.Session=", StringComparison.Ordinal)
            && value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)),
            "normal session is deleted after commit");
        Check(setCookies.Any(value => value.StartsWith("GDS.AdminStepUp=", StringComparison.Ordinal)
            && value.Contains("path=/api/admin", StringComparison.OrdinalIgnoreCase)),
            "StepUp cookie is deleted at its exact path after commit");
        Check(browser.Cookies.GetCookies(browser.Client.BaseAddress!)["GDS.Session"] is null,
            "normal session is absent after success");
        Check(factory.Order.SequenceEqual(new[] { "quota", "store", "hasher", "hasher", "hasher" })
            && factory.PasswordHasher.VerifyCalls == 2 && factory.PasswordHasher.HashCalls == 1,
            "success commits actor-only quota before observed-hash verification and one new hash");
        using (var scope = factory.Services.CreateScope())
        {
            var after = await scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Users.AsNoTracking()
                .SingleAsync(x => x.UserName == "operador");
            Check(!after.MustChangePassword && after.PasswordChangedAt is not null,
                "mandatory state and changed timestamp are persisted");
            Check(after.ManagementVersion == before.ManagementVersion + 1,
                "management version increments exactly once");
            Check(after.SecurityStamp != before.SecurityStamp && after.ConcurrencyStamp != before.ConcurrencyStamp,
                "both stamps rotate independently");
            Check(after.SecurityStamp?.Length == 64 && after.ConcurrencyStamp?.Length == 64
                && after.SecurityStamp != after.ConcurrencyStamp,
                "both stamps are independent 32-byte CSPRNG hex values");
            Check(after.IsActive, "password change never reactivates or disables the actor");
        }
        await Error(await otherSession.Send("GET", "/api/auth/me"),
            HttpStatusCode.Unauthorized, "authentication_required");
        Check(factory.SecurityAudit.Events.All(item => item.EntityId != "auth-change-password"),
            "successful change creates no password-change rejection audit");
        break;
    }
    case "health-readiness-unavailable":
    {
        var response = await browser.Send("GET", "/api/health");
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "unready identity health must return 503");
        var payload = await response.Content.ReadAsStringAsync();
        Check(factory.UnavailableReadiness.Calls == 1, "health must invoke injected readiness exactly once");
        Check(payload.Contains("identity_rate_limit_key_missing", StringComparison.Ordinal), "health must expose only stable readiness code");
        Check(!payload.Contains("secret-key-id", StringComparison.Ordinal), "health must hide missing key IDs");
        break;
    }
    case "headers-production-https":
    case "headers-production-http":
    case "headers-development-https":
    case "headers-testing-https":
        var hardened = await browser.Send("GET", "/api/health");
        Check(hardened.Headers.GetValues("X-Content-Type-Options").Single()=="nosniff","nosniff header");
        Check(hardened.Headers.GetValues("X-Frame-Options").Single()=="DENY","frame denial fallback");
        Check(hardened.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'"),"enforced frame-ancestors");
        Check(hardened.Headers.GetValues("Referrer-Policy").Single()=="strict-origin-when-cross-origin","referrer policy");
        Check(hardened.Headers.GetValues("Permissions-Policy").Single().Contains("camera=()"),"permissions policy");
        Check(hardened.Headers.GetValues("Content-Security-Policy-Report-Only").Single().Contains("default-src 'self'"),"report-only application CSP");
        var shouldHaveHsts=scenario=="headers-production-https";
        Check(hardened.Headers.Contains("Strict-Transport-Security")==shouldHaveHsts,"HSTS only on Production HTTPS");
        break;
    case "forwarded-untrusted":
    case "forwarded-trusted":
        using(var forwarded=new HttpRequestMessage(HttpMethod.Get,"/api/health")) {
            forwarded.Headers.Add("X-Forwarded-Proto","https");
            forwarded.Headers.Add("X-Forwarded-For","203.0.113.10");
            var forwardedResponse=await browser.Client.SendAsync(forwarded);
            Check(forwardedResponse.Headers.Contains("Strict-Transport-Security")==(scenario=="forwarded-trusted"),"forwarded proto accepted only from configured proxy");
        }
        break;
    case "claims":
        foreach (var principal in new[] {
            new ClaimsPrincipal(new ClaimsIdentity()),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())])),
            new ClaimsPrincipal(new ClaimsIdentity([], "synthetic")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "not-uuid")], "synthetic")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString())], "synthetic"))}) {
            try {CurrentUser.GetRequiredId(principal); throw new Exception("ASSERT: invalid principal accepted");}
            catch(UnauthorizedAccessException) {}
        }
        break;
    case "secure":
    case "development":
        var secureCsrf = await Csrf(browser);
        var secureLogin = await Login(browser);
        Check(secureLogin.StatusCode == HttpStatusCode.OK, "login by environment");
        foreach(var cookie in secureCsrf.Headers.GetValues("Set-Cookie").Concat(secureLogin.Headers.GetValues("Set-Cookie")))
            Check(cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase) == (scenario=="secure"), "environment cookie Secure attribute");
        using(var malformed = new HttpRequestMessage(HttpMethod.Post,"/api/auth/login")) {
            malformed.Content = new StringContent("{",System.Text.Encoding.UTF8,"application/json");
            malformed.Headers.Add("X-XSRF-TOKEN",browser.Token);
            Check((await browser.Client.SendAsync(malformed)).StatusCode==HttpStatusCode.BadRequest,"malformed JSON stays 400 by environment");
        }
        using(var forged = NewBrowser()) {
            forged.Client.DefaultRequestHeaders.Add("X-Test-User",Guid.NewGuid().ToString());
            forged.Client.DefaultRequestHeaders.Add("X-Test-Roles","administrador");
            Check((await forged.Send("GET","/api/catalogs")).StatusCode==HttpStatusCode.Unauthorized,"test headers impossible outside Testing even when enabled");
        }
        break;
    case "restart":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login before restart");
        using(var second = new SessionFactory(directory)) {
            second.UseKestrel(0);
            using var boot = second.CreateClient();
            using var after = new Browser(boot.BaseAddress!);
            after.CopyFrom(browser);
            Check((await after.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.OK,"cookie survives recreated host and persisted identity/key ring");
        }
        break;
    case "role-change":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"role login");
        using(var scope=factory.Services.CreateScope()) {
            var users=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var demotedUser=await users.FindByNameAsync("validador");
            Check((await users.RemoveFromRoleAsync(demotedUser!,"validador")).Succeeded,"remove role");
        }
        await Error(await browser.Send("GET","/api/auth/me"), HttpStatusCode.Unauthorized, "authentication_required");
        await Error(await browser.Send("POST","/api/notes/missing/phases/indications/validate",new{expectedRevision=0}), HttpStatusCode.Unauthorized, "authentication_required");
        break;
    case "profile-contract":
    {
        var expectedCreated = new DateTimeOffset(2021, 4, 3, 12, 34, 56, TimeSpan.Zero);
        await ChangeUser("validador", user => { user.CreatedAt = expectedCreated; return Task.CompletedTask; });
        Check((await Login(browser)).StatusCode == HttpStatusCode.OK, "profile owner login");
        using var other = NewBrowser();
        Check((await Login(other, "operador2")).StatusCode == HttpStatusCode.OK, "other user login");
        var otherResponse = await other.Send("GET", "/api/auth/me");
        Check(otherResponse.StatusCode == HttpStatusCode.OK, "other profile available");
        var otherUser = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync()).RootElement;
        var otherId = otherUser.GetProperty("id").GetString();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        var persisted = await db.Users.AsNoTracking().SingleAsync(x => x.UserName == "validador");
        var response = await browser.Send("GET", "/api/auth/me?id=" + otherId);
        Check(response.StatusCode == HttpStatusCode.OK, "profile request succeeds");
        Check(response.Headers.CacheControl?.ToString() == "no-store", "profile is not cached");
        var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(profile.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(
            new[] { "createdAt", "displayName", "id", "mustChangePassword", "permissions", "role", "username" }),
            "profile exposes only approved public fields");
        Check(profile.GetProperty("id").GetString() == persisted.Id.ToString()
            && profile.GetProperty("id").GetString() != otherId, "profile uses session identity, not requested id");
        Check(profile.GetProperty("username").GetString() == persisted.UserName
            && profile.GetProperty("displayName").GetString() == persisted.DisplayName, "profile names match persisted user");
        Check(profile.GetProperty("role").GetString() == "validador", "profile includes actual canonical role");
        Check(profile.GetProperty("mustChangePassword").GetBoolean() == persisted.MustChangePassword, "profile mandatory flag matches persisted user");
        Check(profile.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()).Order().SequenceEqual(
            new[] { "incidents:read", "incidents:write", "phases:validate" }), "validator profile permissions");
        Check(DateTimeOffset.TryParse(profile.GetProperty("createdAt").GetString(), out var actualCreated)
            && actualCreated == persisted.CreatedAt && actualCreated == expectedCreated && actualCreated.Offset == TimeSpan.Zero,
            "creation date matches persisted instant in UTC");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var changed = await users.FindByNameAsync("validador");
        Check((await users.RemoveFromRoleAsync(changed!, "validador")).Succeeded, "remove previous role");
        Check((await users.AddToRoleAsync(changed!, "operador")).Succeeded, "assign new canonical role");
        var refreshed = await browser.Send("GET", "/api/auth/me");
        Check(refreshed.StatusCode == HttpStatusCode.OK, "session stays valid after role change");
        var updated = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()).RootElement;
        Check(updated.GetProperty("role").GetString() == "operador", "profile refreshes changed role");
        Check(updated.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()).Order().SequenceEqual(
            new[] { "incidents:read", "incidents:write" }), "profile refreshes changed permissions");
        Check(updated.GetProperty("id").GetString() == persisted.Id.ToString(), "role change preserves identity");
        break;
    }
    case "anonymous":
        foreach (var (method,path,body) in new (string,string,object?)[] {
            ("GET","/api/auth/me",null),("GET","/api/catalogs",null),("GET","/api/notes",null),
            ("POST","/api/notes",noteInput),("PUT","/api/notes/other",noteInput),
            ("DELETE","/api/notes/other?expectedRevision=0",null),
            ("PUT","/api/notes/other/phases/indications",new {expectedRevision=0,data=PhaseData.Empty()}),
            ("POST","/api/notes/other/phases/indications/validate",new {expectedRevision=0}),
            ("POST","/api/auth/logout",null),
            ("POST","/api/auth/change-password",new {currentPassword="x",newPassword="123456789012345",confirmation="123456789012345"})})
        {
            using var response = await browser.Send(method,path,body);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, $"anonymous {path}: expected 401 got {(int)response.StatusCode}");
            Check(response.Headers.Location is null, "no redirect");
        }
        browser.Client.DefaultRequestHeaders.Authorization = new("Bearer", "transient-browser-identity");
        browser.Client.DefaultRequestHeaders.Add("X-Test-User",Guid.NewGuid().ToString());
        Check((await browser.Send("GET","/api/catalogs")).StatusCode == HttpStatusCode.Unauthorized,"headers disabled unless explicitly enabled in Testing");
        break;
    case "login":
        var login = await Login(browser);
        Check(login.StatusCode == HttpStatusCode.OK,"valid login");
        var text = await login.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement;
        Check(json.EnumerateObject().Select(p=>p.Name).SequenceEqual(new[]{"user"}),"no token or secret in login");
        var user = json.GetProperty("user");
        Check(user.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"createdAt","displayName","id","mustChangePassword","permissions","role","username"}),"public fields only");
        Check(!user.GetProperty("mustChangePassword").GetBoolean(), "ordinary user has no mandatory password change");
        Check(Guid.TryParse(user.GetProperty("id").GetString(),out _),"UUID public id");
        Check(user.GetProperty("role").GetString() == "validador", "canonical role is public");
        Check(DateTimeOffset.TryParse(user.GetProperty("createdAt").GetString(), out var createdAt) && createdAt > DateTimeOffset.UnixEpoch, "createdAt is a real server timestamp");
        Check(user.GetProperty("permissions").EnumerateArray().Select(x=>x.GetString()).Order().SequenceEqual(new[]{"incidents:read","incidents:write","phases:validate"}),"validator permissions");
        var me = await browser.Send("GET","/api/auth/me");
        Check(me.StatusCode == HttpStatusCode.OK,"authenticated me");
        Check(JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()==user.GetProperty("id").GetString(),"same me");
        Check(!text.Contains(password) && !text.Contains("passwordHash",StringComparison.OrdinalIgnoreCase),"no secret in response");
        break;
    case "mandatory-password-routes":
        Check((await Login(browser, "operador")).StatusCode == HttpStatusCode.OK, "mandatory user login");
        var mandatoryMe = await browser.Send("GET", "/api/auth/me");
        Check(mandatoryMe.StatusCode == HttpStatusCode.OK, "GET /me remains allowed");
        Check(JsonDocument.Parse(await mandatoryMe.Content.ReadAsStringAsync()).RootElement.GetProperty("mustChangePassword").GetBoolean(), "public session exposes mandatory flag");
        Check(!mandatoryMe.Headers.TryGetValues("Set-Cookie", out var refreshedCookies)
            || refreshedCookies.All(value => !value.StartsWith("GDS.Session=", StringComparison.Ordinal)),
            "per-request principal refresh must not renew the session cookie");
        Check((await browser.Send("GET", "/api/auth/csrf")).StatusCode == HttpStatusCode.NoContent, "GET /csrf remains allowed");
        var allowedPasswordRoute = await browser.Send("POST", "/api/auth/change-password", new { currentPassword = password, newPassword = "another valid security phrase", confirmation = "different confirmation" });
        await Error(allowedPasswordRoute, HttpStatusCode.BadRequest, "validation_failed");
        foreach (var (method, path, body) in new (string, string, object?)[]
        {
            ("GET", "/api/health", null),
            ("GET", "/api/catalogs", null),
            ("GET", "/api/auth/change-password", null),
            ("POST", "/api/auth/me", null),
            ("POST", "/api/notes", noteInput)
        })
            await Error(await browser.Send(method, path, body, csrf: false), HttpStatusCode.Forbidden, "password_change_required");
        var notesPath = Path.Combine(directory, "notes.json");
        int SavedNoteCount()
        {
            if (!File.Exists(notesPath)) return 0;
            using var document = JsonDocument.Parse(File.ReadAllText(notesPath));
            return document.RootElement.GetArrayLength();
        }
        var notesBeforeMixedCaseRequest = SavedNoteCount();
        var mixedCaseMutation = await browser.Send("POST", "/API/notes", noteInput);
        Check(SavedNoteCount() == notesBeforeMixedCaseRequest,
            "mixed-case API route must be rejected before endpoint state mutation");
        await Error(mixedCaseMutation, HttpStatusCode.Forbidden, "password_change_required");
        await Error(await browser.Send("GET", "/API/AUTH/ME"), HttpStatusCode.Forbidden, "password_change_required");
        Check((await browser.Send("GET", "/apiary")).StatusCode == HttpStatusCode.NotFound,
            "the /api segment boundary must not classify /apiary as an API route");
        Check((await browser.Send("POST", "/api/auth/logout")).StatusCode == HttpStatusCode.NoContent, "POST /logout remains allowed");
        using (var refreshedMandatory = NewBrowser())
        {
            Check((await Login(refreshedMandatory, "operador")).StatusCode == HttpStatusCode.OK, "seed mandatory flag refresh session");
            await ChangeUser("operador", user => { user.MustChangePassword = false; return Task.CompletedTask; });
            Check((await refreshedMandatory.Send("GET", "/api/health")).StatusCode == HttpStatusCode.OK,
                "per-request principal refresh observes a cleared mandatory-password flag");
        }
        break;
    case "auth-response-contract":
        var csrfContract = await Csrf(browser);
        Check(csrfContract.Headers.CacheControl?.ToString() == "no-store", "CSRF uses exact Cache-Control: no-store");
        var loginContract = await browser.Send("POST", "/api/auth/login", new { username = "validador", password });
        Check(loginContract.StatusCode == HttpStatusCode.OK, "contract login succeeds");
        Check(loginContract.Headers.CacheControl?.ToString() == "no-store", "login uses exact Cache-Control: no-store");
        var meContract = await browser.Send("GET", "/api/auth/me");
        Check(meContract.StatusCode == HttpStatusCode.OK, "contract me succeeds");
        Check(meContract.Headers.CacheControl?.ToString() == "no-store", "me uses exact Cache-Control: no-store");
        var futureAdminContract = await browser.Send("GET", "/api/admin/not-yet-implemented");
        Check(futureAdminContract.StatusCode == HttpStatusCode.NotFound, "future admin branch remains unwired before its owner task");
        Check(futureAdminContract.Headers.CacheControl?.ToString() == "no-store", "future admin branch uses exact Cache-Control: no-store");

        using (var missing = NewBrowser())
            await Error(await missing.Send("GET", "/api/auth/me"), HttpStatusCode.Unauthorized, "authentication_required");
        using (var invalid = NewBrowser())
        {
            await Csrf(invalid);
            await Error(await invalid.Send("POST", "/api/auth/login", new { username = "missing", password }), HttpStatusCode.Unauthorized, "authentication_required");
        }
        using (var expired = NewBrowser())
        {
            Check((await Login(expired)).StatusCode == HttpStatusCode.OK, "seed expired session");
            var expiredOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var cookie = expired.Cookies.GetCookies(expired.Client.BaseAddress!)["GDS.Session"]!;
            var expiredTicket = expiredOptions.TicketDataFormat.Unprotect(cookie.Value)!;
            expiredTicket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            cookie.Value = expiredOptions.TicketDataFormat.Protect(expiredTicket);
            await Error(await expired.Send("GET", "/api/auth/me"), HttpStatusCode.Unauthorized, "authentication_required");
        }
        using (var revoked = NewBrowser())
        {
            Check((await Login(revoked, "validador2")).StatusCode == HttpStatusCode.OK, "seed revoked session");
            await ChangeUser("validador2", user => { user.SecurityStamp = Guid.NewGuid().ToString(); return Task.CompletedTask; });
            await Error(await revoked.Send("GET", "/api/auth/me"), HttpStatusCode.Unauthorized, "authentication_required");
        }
        using (var forbidden = NewBrowser())
        {
            Check((await Login(forbidden, "operador2")).StatusCode == HttpStatusCode.OK, "seed forbidden session");
            var forbiddenCreated = JsonDocument.Parse(await (await forbidden.Send("POST", "/api/notes", noteInput)).Content.ReadAsStringAsync()).RootElement;
            await Error(await forbidden.Send("POST", $"/api/notes/{forbiddenCreated.GetProperty("id").GetString()}/phases/indications/validate", new { expectedRevision = 0 }), HttpStatusCode.Forbidden, "permission_denied");
        }
        break;
    case "authorization-result-handler-audit-ownership":
    {
        factory.SecurityAudit.Events.Clear();
        var anonymous = await browser.Send("GET", "/api/notes/");
        await Error(anonymous, HttpStatusCode.Unauthorized, "authentication_required");
        Check(factory.SecurityAudit.Events.Count == 1, "anonymous authorization must write exactly one rejection audit");
        Check(factory.SecurityAudit.Events[0].Code == "authentication_required", "anonymous rejection code");

        using var operatorBrowser = NewBrowser();
        var operatorLogin = await Login(operatorBrowser, "operador");
        Check(operatorLogin.IsSuccessStatusCode, "operator login: " + (int)operatorLogin.StatusCode + " " + await operatorLogin.Content.ReadAsStringAsync());
        await Csrf(operatorBrowser);
        factory.SecurityAudit.Events.Clear();
        var forbidden = await operatorBrowser.Send("POST", "/api/notes/missing/phases/recognition/validate", new { validationNotes = "x" });
        await Error(forbidden, HttpStatusCode.Forbidden, "permission_denied");
        Check(factory.SecurityAudit.Events.Count == 1 && factory.SecurityAudit.Events[0].Code == "permission_denied", "generic forbidden must write exactly one rejection audit");
        Check(factory.SecurityAudit.Events.All(x => x.Code is "authentication_required" or "permission_denied"), "authorization handler owns only generic authorization results");
        break;
    }
    case "authorization-unknown-fixed-entity":
    {
        factory.SecurityAudit.Events.Clear();
        await Error(await browser.Send("GET", "/api/notes/"), HttpStatusCode.Unauthorized, "authentication_required");
        var audit = factory.SecurityAudit.Events.Single();
        Check(audit.Action == IdentitySecurityActions.AuthorizationReject, "authorization action must be fixed");
        Check(audit.ActorUserId is null && audit.EntityType == "authentication" && audit.EntityId == "unknown" && audit.Outcome == "rejected", "unknown authorization target must use fixed entity");
        break;
    }
    case "mandatory-password-audit-before-response":
    {
        using var mandatory = NewBrowser();
        var mandatoryLogin = await Login(mandatory, "operador");
        Check(mandatoryLogin.IsSuccessStatusCode, "mandatory login: " + (int)mandatoryLogin.StatusCode + " " + await mandatoryLogin.Content.ReadAsStringAsync());
        await Csrf(mandatory);
        factory.SecurityAudit.Events.Clear();
        var response = await mandatory.Send("POST", "/api/notes/", noteInput);
        await Error(response, HttpStatusCode.Forbidden, "password_change_required");
        Check(factory.SecurityAudit.ObservedBeforeResponse && factory.SecurityAudit.Events.Count == 1, "mandatory filter must audit before writing response");
        Check(factory.SecurityAudit.Events.Single().Action == IdentitySecurityActions.PasswordChangeRequiredReject, "mandatory filter owns its action");
        break;
    }
    case "rejection-audit-exactly-once":
    {
        factory.SecurityAudit.Events.Clear();
        await Error(await browser.Send("GET", "/api/notes/"), HttpStatusCode.Unauthorized, "authentication_required");
        Check(factory.SecurityAudit.Events.Count == 1, "authorization rejection must be audited once");
        using var mandatory = NewBrowser();
        var mandatoryLogin = await Login(mandatory, "operador");
        Check(mandatoryLogin.IsSuccessStatusCode, "mandatory login: " + (int)mandatoryLogin.StatusCode + " " + await mandatoryLogin.Content.ReadAsStringAsync());
        await Csrf(mandatory);
        factory.SecurityAudit.Events.Clear();
        await Error(await mandatory.Send("POST", "/api/notes/", noteInput), HttpStatusCode.Forbidden, "password_change_required");
        Check(factory.SecurityAudit.Events.Count == 1, "mandatory rejection must be audited once without authorization duplicate");
        break;
    }
    case "invalid":
        string? previous = null;
        foreach(var pair in new[]{("missing",password),("operador",password+"wrong"),("inactive",password),("locked",password)})
        {
            using var b = NewBrowser();
            var response=await Login(b,pair.Item1,pair.Item2);
            Check(response.StatusCode==HttpStatusCode.Unauthorized,"generic credential failure " + pair.Item1);
            var body=await response.Content.ReadAsStringAsync();
            Check(previous is null || previous==body,"uniform public failure body"); previous=body;
        }
        break;
    case "auth-audit":
        Check((await browser.Send("GET", "/api/auth/me")).StatusCode==HttpStatusCode.Unauthorized,"generic unauthorized response");
        Check(factory.Audit.Events.Count==0,"generic 401 is not an authentication audit event");
        Check((await browser.Send("POST","/api/auth/login",new {username="validador",password},csrf:false)).StatusCode==HttpStatusCode.BadRequest,"CSRF rejects before authentication");
        Check(factory.Audit.Events.Count==0,"rejected CSRF is not an authentication audit event");
        foreach(var pair in new[]{("missing",password),("operador",password+"wrong"),("inactive",password),("locked",password)})
        {
            using var rejected = NewBrowser();
            Check((await Login(rejected,pair.Item1,pair.Item2)).StatusCode==HttpStatusCode.Unauthorized,"audited rejection " + pair.Item1);
        }
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"audited successful login");
        Check((await browser.Send("POST","/api/auth/logout")).StatusCode==HttpStatusCode.NoContent,"audited logout");
        var auditEvents=factory.Audit.Events.ToArray();
        Check(auditEvents.Length==6,"one bounded event per completed authentication action");
        Check(auditEvents.Take(4).All(x=>x.Action=="auth.login"&&x.Outcome=="rejected"&&x.ActorUserId is null&&x.EntityId=="anonymous"),"rejected login shape");
        Check(auditEvents.Take(4).Select(x=>x.Reason).SequenceEqual(new[]{"invalid_credentials","invalid_credentials","inactive","locked_out"}),"internal rejection reasons");
        var successfulLogin=auditEvents[4];
        Check(successfulLogin.Action=="auth.login"&&successfulLogin.Outcome=="succeeded"&&successfulLogin.ActorUserId is not null&&successfulLogin.EntityId==successfulLogin.ActorUserId.ToString(),"successful login shape");
        var successfulLogout=auditEvents[5];
        Check(successfulLogout.Action=="auth.logout"&&successfulLogout.Outcome=="succeeded"&&successfulLogout.ActorUserId==successfulLogin.ActorUserId,"successful logout shape");
        Check(auditEvents.All(x=>!string.IsNullOrWhiteSpace(x.CorrelationId)&&x.CorrelationId!.Length<=128),"bounded correlation IDs");
        var auditJson=JsonSerializer.Serialize(auditEvents);
        Check(!auditJson.Contains("Username",StringComparison.OrdinalIgnoreCase),"audit contract has no username field");
        foreach(var forbidden in new[]{password,"missing","operador","GDS.Session","XSRF-TOKEN"})
            Check(!auditJson.Contains(forbidden,StringComparison.OrdinalIgnoreCase),"audit excludes credential, username, cookie and token material: " + forbidden);
        break;
    case "auth-audit-failure":
        factory.Audit.FailWrites=true;
        var unavailableLogin=await Login(browser);
        Check(unavailableLogin.StatusCode==HttpStatusCode.ServiceUnavailable,"successful credentials fail closed when audit storage is unavailable");
        Check(browser.Cookies.GetCookies(browser.Client.BaseAddress!)["GDS.Session"] is null,"audit failure issues no session cookie");
        using(var rejected=NewBrowser())
            Check((await Login(rejected,"missing")).StatusCode==HttpStatusCode.ServiceUnavailable,"rejected credentials use the same audit failure response");
        factory.Audit.FailWrites=false;
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login recovers with audit storage");
        factory.Audit.FailWrites=true;
        Check((await browser.Send("POST","/api/auth/logout")).StatusCode==HttpStatusCode.ServiceUnavailable,"logout reports unavailable audit storage");
        Check((await browser.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.Unauthorized,"logout remains fail-safe by revoking the session before the failed audit");
        break;
    case "auth-signin-failure":
        var failedSignIn=await Login(browser);
        Check(failedSignIn.StatusCode==HttpStatusCode.ServiceUnavailable,"cookie handler failure is unavailable");
        Check(browser.Cookies.GetCookies(browser.Client.BaseAddress!)["GDS.Session"] is null,"cookie handler failure issues no session cookie");
        Check(factory.Audit.Events.All(x=>x.Action!="auth.login"||x.Outcome!="succeeded"),"failed cookie emission cannot be audited as a successful login");
        Check((await browser.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.Unauthorized,"cookie handler failure leaves no usable session");
        break;
    case "csrf":
        Check((await browser.Send("POST","/api/auth/login",new {username="validador",password},csrf:false)).StatusCode==HttpStatusCode.BadRequest,"login requires CSRF");
        await Csrf(browser);
        Check((await browser.Send("POST","/api/auth/login",new {username="validador",password},token:"invalid")).StatusCode==HttpStatusCode.BadRequest,"invalid login CSRF");
        var anonymousToken = browser.Token;
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login");
        Check(browser.Token!=anonymousToken,"login issues identity-bound token");
        foreach(var (method,path,body) in new (string,string,object?)[]{
            ("POST","/api/auth/logout",null),("POST","/api/notes",noteInput),
            ("PUT","/api/notes/other",noteInput),("DELETE","/api/notes/other?expectedRevision=0",null),
            ("PUT","/api/notes/other/phases/indications",new {expectedRevision=0,data=PhaseData.Empty()}),
            ("POST","/api/notes/other/phases/indications/validate",new {expectedRevision=0})})
        {
            Check((await browser.Send(method,path,body,csrf:false)).StatusCode==HttpStatusCode.BadRequest,"missing CSRF " + path);
            Check((await browser.Send(method,path,body,token:"invalid")).StatusCode==HttpStatusCode.BadRequest,"wrong CSRF " + path);
            Check((await browser.Send(method,path,body,token:anonymousToken)).StatusCode==HttpStatusCode.BadRequest,"anonymous token rejected " + path);
        }
        Check((await browser.Send("POST","/api/notes",noteInput)).StatusCode==HttpStatusCode.OK,"new authenticated token valid");
        break;
    case "revocation":
    case "disabled":
    case "deleted":
    case "stamp":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login");
        using (var copy=NewBrowser())
        {
            copy.CopyFrom(browser);
            Check((await copy.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.OK,"copied cookie works before revocation");
            if(scenario=="revocation")
            {
                var oldToken=browser.Token;
                Check((await browser.Send("POST","/api/auth/logout")).StatusCode==HttpStatusCode.NoContent,"logout");
                Check(browser.Token!=oldToken,"logout replaces bound token");
                Check((await browser.Send("POST","/api/auth/login",new {username="validador",password})).StatusCode==HttpStatusCode.OK,"anonymous token usable after logout");
            }
            else if(scenario=="disabled") await ChangeUser("validador", u=>{u.IsActive=false;return Task.CompletedTask;});
            else if(scenario=="stamp") await ChangeUser("validador", u=>{u.SecurityStamp=Guid.NewGuid().ToString();return Task.CompletedTask;});
            else {using var scope=factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Users.Where(x=>x.UserName=="validador").ExecuteDeleteAsync();}
            Check((await copy.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.Unauthorized,"old cookie revoked on next request");
        }
        break;
    case "cookie":
        var csrfResponse=await Csrf(browser);
        var csrfCookies=csrfResponse.Headers.GetValues("Set-Cookie").ToArray();
        Check(csrfCookies.Length==2,"two CSRF cookies");
        Check(csrfCookies.Single(x=>x.StartsWith("XSRF-TOKEN=")).Contains("httponly",StringComparison.OrdinalIgnoreCase)==false,"readable XSRF token");
        Check(csrfCookies.Any(x=>!x.StartsWith("XSRF-TOKEN=")&&x.Contains("httponly",StringComparison.OrdinalIgnoreCase)),"internal HttpOnly token");
        var cookieResponse=await Login(browser);
        var authSet=cookieResponse.Headers.GetValues("Set-Cookie").Single(x=>x.StartsWith("GDS.Session="));
        foreach(var cookie in csrfCookies.Append(authSet)) {
            Check(cookie.Contains("path=/",StringComparison.OrdinalIgnoreCase)&&cookie.Contains("samesite=lax",StringComparison.OrdinalIgnoreCase),"cookie scope");
            Check(!cookie.Contains("domain=",StringComparison.OrdinalIgnoreCase),"host only");
            Check(!cookie.Contains("secure",StringComparison.OrdinalIgnoreCase),"Testing HTTP cookie");
        }
        Check(authSet.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&!authSet.Contains("expires=",StringComparison.OrdinalIgnoreCase)&&!authSet.Contains("max-age",StringComparison.OrdinalIgnoreCase),"session not persistent");
        var options=factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        Check(options.ExpireTimeSpan==TimeSpan.FromHours(8)&&!options.SlidingExpiration,"8h absolute no sliding");
        var authCookie=browser.Cookies.GetCookies(browser.Client.BaseAddress!)["GDS.Session"]!;
        var ticket=options.TicketDataFormat.Unprotect(authCookie.Value)!;
        Check(ticket.Properties.ExpiresUtc-ticket.Properties.IssuedUtc==TimeSpan.FromHours(8),"actual ticket 8h");
        ticket.Properties.ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(-1);
        authCookie.Value=options.TicketDataFormat.Protect(ticket);
        Check((await browser.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.Unauthorized,"expired cookie rejected");
        Check((await browser.Send("POST","/api/auth/logout")).StatusCode==HttpStatusCode.Unauthorized,"expired logout is 401");
        authCookie.Value="tampered";
        Check((await browser.Send("GET","/api/auth/me")).StatusCode==HttpStatusCode.Unauthorized,"altered cookie rejected");
        break;
    case "limited-read":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login");
        var older=JsonDocument.Parse(await (await browser.Send("POST","/api/notes",noteInput)).Content.ReadAsStringAsync()).RootElement;
        var olderId=older.GetProperty("id").GetString();
        Check((await browser.Send("POST","/api/notes",noteInput)).StatusCode==HttpStatusCode.OK,"newer note");
        var limited=JsonDocument.Parse(await (await browser.Send("GET","/api/notes")).Content.ReadAsStringAsync()).RootElement;
        Check(limited.GetArrayLength()==1 && limited[0].GetProperty("id").GetString()!=olderId,"older own note is outside list limit");
        var readOlder=await browser.Send("GET","/api/notes/"+olderId);
        Check(readOlder.StatusCode==HttpStatusCode.OK,"own note outside list limit must remain readable; got "+(int)readOlder.StatusCode);
        Check(JsonDocument.Parse(await readOlder.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()==olderId,"correct individual record");
        using(var stranger=NewBrowser()) {
            Check((await Login(stranger,"validador2")).StatusCode==HttpStatusCode.OK,"other login");
            Check((await stranger.Send("GET","/api/notes/"+olderId)).StatusCode==HttpStatusCode.NotFound,"other owner remains 404 outside list limit");
        }
        Check((await browser.Send("GET","/api/notes/does-not-exist")).StatusCode==HttpStatusCode.NotFound,"missing note 404");
        break;
    case "exception-logs":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login");
        var failure=await browser.Send("GET","/api/notes");
        var failureBody=await failure.Content.ReadAsStringAsync();
        Check(failure.StatusCode==HttpStatusCode.ServiceUnavailable,"repository failure is 503");
        Check(JsonDocument.Parse(failureBody).RootElement.GetProperty("error").GetString()=="El servicio de incidencias no está disponible temporalmente.","HTTP failure remains generic");
        Check(!failureBody.Contains(LimitedTestRepository.ExceptionSentinel),"exception never exposed by HTTP");
        Check(factory.Logs.ToString().Contains(LimitedTestRepository.LogMessage),"logger captures formatted message");
        Check(factory.Logs.ToString().Contains("System.InvalidOperationException: "+LimitedTestRepository.ExceptionSentinel),"logger must capture exception.ToString separately from message");
        Check(factory.Logs.ToString().Contains("LimitedTestRepository"),"logger captures exception stack");
        break;
    case "ownership":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login");
        var created=await browser.Send("POST","/api/notes",noteInput);
        var note=JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id=note.GetProperty("id").GetString();
        Check((await browser.Send("GET","/api/notes/"+id)).StatusCode==HttpStatusCode.OK,"owner can read");
        using(var other=NewBrowser()) {
            Check((await Login(other,"validador2")).StatusCode==HttpStatusCode.OK,"other login");
            Check((await (await other.Send("GET","/api/notes")).Content.ReadAsStringAsync())=="[]","other cannot list");
            foreach(var (method,path,body) in new (string,string,object?)[]{
                ("GET","/api/notes/"+id,null),("PUT","/api/notes/"+id,noteInput),("DELETE","/api/notes/"+id+"?expectedRevision=0",null),
                ("PUT",$"/api/notes/{id}/phases/indications",new {expectedRevision=0,data=PhaseData.Empty()}),
                ("POST",$"/api/notes/{id}/phases/indications/validate",new {expectedRevision=0})})
                Check((await other.Send(method,path,body)).StatusCode==HttpStatusCode.NotFound,"ownership " + method+path);
        }
        break;
    case "roles":
        foreach(var role in new[]{"operador","validador","administrador"}) {
            using var b=NewBrowser(); Check((await Login(b,role)).StatusCode==HttpStatusCode.OK,"role login");
            var createdRole=await b.Send("POST","/api/notes",noteInput);
            var own=JsonDocument.Parse(await createdRole.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
            var status=(await b.Send("POST",$"/api/notes/{own}/phases/indications/validate",new{expectedRevision=0})).StatusCode;
            Check((int)status==(role=="operador"?403:422),"role enforcement before domain validation "+role);
            if(role!="operador") {
                var phaseData=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"phase.json"))).RootElement;
                Check((await b.Send("PUT",$"/api/notes/{own}/phases/indications",new {expectedRevision=0,data=phaseData})).StatusCode==HttpStatusCode.OK,"save own valid phase");
                var valid=await b.Send("POST",$"/api/notes/{own}/phases/indications/validate",new {expectedRevision=1});
                Check(valid.StatusCode==HttpStatusCode.OK,"validator/admin validates own complete phase");
                var validated=JsonDocument.Parse(await valid.Content.ReadAsStringAsync()).RootElement;
                Check(validated.GetProperty("workflow").GetProperty("phases")[0].GetProperty("validatedBy").GetString()==validated.GetProperty("userId").GetString(),"actor must be authenticated owner UUID");
            }
            var publicMe=JsonDocument.Parse(await (await b.Send("GET","/api/auth/me")).Content.ReadAsStringAsync()).RootElement;
            var permissions=publicMe.GetProperty("permissions").EnumerateArray().Select(x=>x.GetString()).ToArray();
            Check(permissions.Length==(role=="operador"?2:role=="validador"?3:5),"permission count "+role);
        }
        break;
    case "rate":
        await Csrf(browser);
        var statuses=new List<int>();
        for(var i=0;i<12;i++) statuses.Add((int)(await browser.Send("POST","/api/auth/login",new {username="missing",password})).StatusCode);
        Check(statuses.Contains(429)&&statuses.Take(3).All(x=>x==401),"narrow login throttle");
        Check((await browser.Send("GET","/api/health")).StatusCode==HttpStatusCode.OK,"health not throttled");
        break;
    case "login-size":
        await Csrf(browser);
        var oversized = await browser.Send("POST", "/api/auth/login", new {username="validador", password=new string('x', 8 * 1024)});
        Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "login body must have a narrow endpoint limit");
        break;
    case "global-body-size":
        Check((await Login(browser)).StatusCode==HttpStatusCode.OK,"login for body limits");
        var maximumBusinessInput=new {title="Maximum business input",content=new string('x',10_000),state="Area",altitude=19.0,longitude=-99.0,expectedRevision=0};
        Check((await browser.Send("POST","/api/notes",maximumBusinessInput)).StatusCode==HttpStatusCode.OK,"maximum documented note content remains accepted");
        var globallyOversized=new {title="Oversized",content=new string('x',2 * 1024 * 1024),state="Area",altitude=19.0,longitude=-99.0,expectedRevision=0};
        Check((await browser.Send("POST","/api/notes",globallyOversized)).StatusCode==HttpStatusCode.RequestEntityTooLarge,"global request body limit");
        break;
    case "stepup-lifecycle":
        Check((await Login(browser, "administrador")).StatusCode == HttpStatusCode.OK, "administrator login");
        var firstStepUp = await browser.Send("POST", "/api/auth/reauth", new { password });
        Check(firstStepUp.StatusCode == HttpStatusCode.NoContent, "reauth must issue StepUp");
        var stepCookie = firstStepUp.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("GDS.AdminStepUp=", StringComparison.Ordinal));
        Check(stepCookie.Contains("path=/api/admin", StringComparison.OrdinalIgnoreCase)
            && stepCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && stepCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)
            && stepCookie.Contains("max-age=300", StringComparison.OrdinalIgnoreCase)
            && !stepCookie.Contains("; secure", StringComparison.OrdinalIgnoreCase),
            "Testing StepUp cookie must use the exact path, HttpOnly, Strict, five-minute and local Secure contract");
        var stepValue = stepCookie.Split(';', 2)[0].Split('=', 2)[1];
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
            var actor = await database.Users.SingleAsync(user => user.UserName == "administrador");
            var actorPrincipal = await SessionAuthentication.PrincipalAsync(actor, database);
            var service = scope.ServiceProvider.GetRequiredService<AdminStepUpService>();
            var protection = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            var purposeWasSeparate = false;
            try { _ = protection.CreateProtector("GdsMine.Authentication.v1").Unprotect(stepValue); }
            catch (System.Security.Cryptography.CryptographicException) { purposeWasSeparate = true; }
            Check(purposeWasSeparate, "StepUp must use a protector purpose separate from the normal session");
            async Task<(AdminStepUpValidation Validation, DefaultHttpContext Context)> Validate()
            {
                var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = actorPrincipal };
                context.Request.Headers.Cookie = $"GDS.AdminStepUp={stepValue}";
                return (await service.ValidateAsync(context, default), context);
            }
            var reusableOne = await Validate();
            var reusableTwo = await Validate();
            Check(reusableOne.Validation.IsValid && reusableTwo.Validation.IsValid,
                "one StepUp must authorize repeated validations");
            Check(!reusableOne.Context.Response.Headers.ContainsKey("Set-Cookie")
                && !reusableTwo.Context.Response.Headers.ContainsKey("Set-Cookie"),
                "validation must neither consume nor renew StepUp");
            var other = await database.Users.SingleAsync(user => user.UserName == "operador");
            other.SecurityStamp = Guid.NewGuid().ToString();
            await database.SaveChangesAsync();
            Check((await Validate()).Validation.IsValid,
                "changing another user's state must not invalidate actor StepUp");
            actor.SecurityStamp = Guid.NewGuid().ToString();
            await database.SaveChangesAsync();
            var invalidated = await Validate();
            Check(!invalidated.Validation.IsValid
                && invalidated.Context.Response.Headers.SetCookie.Any(value => value?.StartsWith("GDS.AdminStepUp=", StringComparison.Ordinal) == true),
                "actor stamp rotation must invalidate and clear StepUp");

            actor.SecurityStamp = "task-10-restored-stamp";
            await database.SaveChangesAsync();
            var freshContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            service.Issue(freshContext, actor);
            var freshValue = freshContext.Response.Headers.SetCookie.Single()!.Split(';', 2)[0].Split('=', 2)[1];
            var adminRole = await database.Roles.SingleAsync(role => role.Name == "administrador");
            var operatorRole = await database.Roles.SingleAsync(role => role.Name == "operador");
            var membership = await database.UserRoles.SingleAsync(item => item.UserId == actor.Id);
            database.UserRoles.Remove(membership);
            database.UserRoles.Add(new IdentityUserRole<Guid> { UserId = actor.Id, RoleId = operatorRole.Id });
            await database.SaveChangesAsync();
            var roleContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = actorPrincipal };
            roleContext.Request.Headers.Cookie = $"GDS.AdminStepUp={freshValue}";
            Check(!(await service.ValidateAsync(roleContext, default)).IsValid,
                "StepUp must re-check the actor's exact current administrator role");
            database.UserRoles.Remove(await database.UserRoles.SingleAsync(item => item.UserId == actor.Id));
            database.UserRoles.Add(new IdentityUserRole<Guid> { UserId = actor.Id, RoleId = adminRole.Id });
            actor.IsActive = false;
            await database.SaveChangesAsync();
            var inactiveContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = actorPrincipal };
            inactiveContext.Request.Headers.Cookie = $"GDS.AdminStepUp={freshValue}";
            Check(!(await service.ValidateAsync(inactiveContext, default)).IsValid,
                "StepUp must re-check the actor's active status");
        }
        break;
    case "reauth-body-size":
        Check((await Login(browser, "administrador")).StatusCode == HttpStatusCode.OK, "administrator login");
        factory.PasswordHasher.Reset();
        factory.RateLimiter.Reset();
        using (var oversizedReauth = new HttpRequestMessage(HttpMethod.Post, "/api/auth/reauth"))
        {
            oversizedReauth.Headers.Add("X-XSRF-TOKEN", browser.Token);
            oversizedReauth.Content = new ByteArrayContent(Enumerable.Repeat((byte)'x', 4097).ToArray());
            oversizedReauth.Content.Headers.ContentType = new("application/json");
            await Error(await browser.Client.SendAsync(oversizedReauth), HttpStatusCode.RequestEntityTooLarge, "validation_failed");
            Check(factory.PasswordHasher.VerifyCalls == 0 && factory.RateLimiter.ConsumeCalls == 0,
                "4097-byte body must be rejected before quota and hasher");
        }
        break;
    case "reauth-rune-limit":
        Check((await Login(browser, "administrador")).StatusCode == HttpStatusCode.OK, "administrator login");
        factory.PasswordHasher.Reset();
        factory.RateLimiter.Reset();
        await Error(await browser.Send("POST", "/api/auth/reauth", new { password = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F6E1), 129)) }),
            HttpStatusCode.BadRequest, "reauthentication_failed");
        Check(factory.PasswordHasher.VerifyCalls == 0 && factory.RateLimiter.ConsumeCalls == 0,
            "129 Unicode runes must be rejected before quota and hasher");
        break;
    case "reauth-csrf":
        Check((await Login(browser, "administrador")).StatusCode == HttpStatusCode.OK, "administrator login");
        factory.PasswordHasher.Reset();
        factory.RateLimiter.Reset();
        await Error(await browser.Send("POST", "/api/auth/reauth", new { password }, csrf: false), HttpStatusCode.BadRequest, "csrf_invalid");
        await Error(await browser.Send("POST", "/api/auth/reauth", new { password }, token: "incorrect"), HttpStatusCode.BadRequest, "csrf_invalid");
        Check(factory.PasswordHasher.VerifyCalls == 0 && factory.RateLimiter.ConsumeCalls == 0,
            "CSRF rejection must occur before quota and hasher");
        Check(factory.SecurityAudit.Events.Count(item => item.Code == "csrf_invalid") == 2,
            "each CSRF rejection is audited exactly once");
        break;
    case "reauth-quota-order":
        Check((await Login(browser, "administrador")).StatusCode == HttpStatusCode.OK, "administrator login");
        factory.PasswordHasher.Reset();
        factory.Order.Clear();
        await Error(await browser.Send("POST", "/api/auth/reauth", new { password = password + " wrong" }),
            HttpStatusCode.BadRequest, "reauthentication_failed");
        Check(factory.Order.SequenceEqual(new[] { "quota", "hasher", "audit" }),
            "quota commit must be observed before hasher and rejection audit");
        factory.Order.Clear();
        factory.RateLimiter.DenyNext = true;
        await Error(await browser.Send("POST", "/api/auth/reauth", new { password }),
            HttpStatusCode.TooManyRequests, "rate_limited");
        Check(factory.Order.SequenceEqual(new[] { "quota", "audit" }) && factory.PasswordHasher.VerifyCalls == 1,
            "denied quota must be committed and audited without invoking hasher");
        break;
    case "reauth-session-preservation":
        Check((await Login(browser, "administrador")).StatusCode == HttpStatusCode.OK, "administrator login");
        var beforeReauthSession = browser.Cookies.GetCookies(browser.Client.BaseAddress!)["GDS.Session"]?.Value;
        var rejectedReauth = await browser.Send("POST", "/api/auth/reauth", new { password = password + " wrong" });
        await Error(rejectedReauth, HttpStatusCode.BadRequest, "reauthentication_failed");
        Check((await browser.Send("GET", "/api/auth/me")).StatusCode == HttpStatusCode.OK, "failed reauth preserves the normal session");
        var successfulReauth = await browser.Send("POST", "/api/auth/reauth", new { password });
        Check(successfulReauth.StatusCode == HttpStatusCode.NoContent && successfulReauth.Headers.CacheControl?.NoStore == true,
            "successful reauth is 204 no-store");
        Check(!successfulReauth.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("GDS.Session=", StringComparison.Ordinal)),
            "successful reauth must not emit GDS.Session");
        Check(browser.Cookies.GetCookies(browser.Client.BaseAddress!)["GDS.Session"]?.Value == beforeReauthSession,
            "reauth must not replace or renew GDS.Session");
        Check(factory.Audit.Events.Count(item => item.Action == "auth.admin.reauthenticate" && item.Outcome == "succeeded") == 1,
            "successful reauth must write the dedicated success audit without credential material");
        break;
    case "change-password-real-postgres":
    {
        const long barrierBase = 731300000;
        var adminId = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var secretSentinel = "Task13SecretSentinel-" + Guid.NewGuid().ToString("N");
        var diagnostics = new List<string>();
        var privacyArtifacts = new List<(string Label, string Value)>();
        void AddPrivacyArtifact(string label, string? value)
        {
            Check(!string.IsNullOrEmpty(value), label + " privacy artifact is available");
            if (!privacyArtifacts.Any(item => item.Value == value))
                privacyArtifacts.Add((label, value!));
        }
        AddPrivacyArtifact("password sentinel", secretSentinel);
        AddPrivacyArtifact("rate-limit HMAC secret", factory.RateLimitHmacSecretBase64);
        foreach (var (label, variable) in new[]
        {
            ("runtime", "GDS_TEST_PRIMARY_CONNECTION"),
            ("identity-admin", "GDS_TEST_IDENTITY_ADMIN_CONNECTION")
        })
        {
            var configured = Environment.GetEnvironmentVariable(variable)
                ?? throw new Exception("ASSERT: missing disposable " + label + " connection");
            var builder = new NpgsqlConnectionStringBuilder(configured);
            AddPrivacyArtifact(label + " Npgsql host", builder.Host);
            AddPrivacyArtifact(label + " Npgsql database", builder.Database);
            AddPrivacyArtifact(label + " Npgsql username", builder.Username);
            AddPrivacyArtifact(label + " Npgsql password", builder.Password);
        }
        var controlValue = Environment.GetEnvironmentVariable("GDS_TEST_CONTROL_CONNECTION")
            ?? throw new Exception("ASSERT: missing disposable control connection");
        await using var control = new NpgsqlConnection(controlValue);
        await control.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app.task13_test_barrier
                (target_id uuid PRIMARY KEY, mode text NOT NULL, lock_id bigint NOT NULL);
            CREATE TABLE IF NOT EXISTS app.task13_test_fail_audit
                (enabled boolean PRIMARY KEY DEFAULT TRUE);
            CREATE OR REPLACE FUNCTION app.task13_test_barrier_trigger()
            RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $fn$
            DECLARE selected_mode text; selected_lock bigint;
            BEGIN
                SELECT mode,lock_id INTO selected_mode,selected_lock
                FROM app.task13_test_barrier WHERE target_id=NEW."Id";
                IF FOUND AND ((selected_mode='password' AND NEW."PasswordHash" IS DISTINCT FROM OLD."PasswordHash")
                    OR (selected_mode='disable' AND OLD."IsActive" AND NOT NEW."IsActive")) THEN
                    PERFORM pg_catalog.pg_advisory_xact_lock(selected_lock);
                END IF;
                RETURN NEW;
            END;
            $fn$;
            CREATE OR REPLACE FUNCTION app.task13_test_audit_failure_trigger()
            RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $fn$
            BEGIN
                IF NEW.action='auth.password.change'
                   AND EXISTS (SELECT 1 FROM app.task13_test_fail_audit WHERE enabled) THEN
                    RAISE EXCEPTION 'task13_test_success_audit_failure' USING ERRCODE='55000';
                END IF;
                RETURN NEW;
            END;
            $fn$;
            DROP TRIGGER IF EXISTS aaa_task13_test_barrier ON public."AspNetUsers";
            CREATE TRIGGER aaa_task13_test_barrier BEFORE UPDATE ON public."AspNetUsers"
                FOR EACH ROW EXECUTE FUNCTION app.task13_test_barrier_trigger();
            DROP TRIGGER IF EXISTS aaa_task13_test_audit_failure ON app.audit_events;
            CREATE TRIGGER aaa_task13_test_audit_failure BEFORE INSERT ON app.audit_events
                FOR EACH ROW EXECUTE FUNCTION app.task13_test_audit_failure_trigger();
            """, control))
            await install.ExecuteNonQueryAsync();

        var store = factory.Services.GetRequiredService<IIdentityAdministrationStore>();
        async Task ExecuteControl(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, control);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            _ = await command.ExecuteNonQueryAsync();
        }
        async Task ResetActor(int index)
        {
            await ExecuteControl("DELETE FROM app.task13_test_barrier; DELETE FROM app.task13_test_fail_audit; DELETE FROM app.identity_rate_limits;");
            await ExecuteControl("""
                UPDATE public."AspNetUsers" SET
                    "PasswordHash"=@hash, "SecurityStamp"=@security,
                    "ConcurrencyStamp"=@concurrency, "IsActive"=TRUE,
                    "UpdatedAt"=now(), "MustChangePassword"=TRUE,
                    "PasswordChangedAt"=NULL, "ManagementVersion"=10
                WHERE "Id"=@id
                """, ("hash", hashes[index]), ("security", "task13-reset-security-" + index),
                ("concurrency", "task13-reset-concurrency-" + index), ("id", realActorIds[index]));
        }
        async Task SetBarrier(int index, string mode, long lockId)
        {
            await ExecuteControl("DELETE FROM app.task13_test_barrier; INSERT INTO app.task13_test_barrier(target_id,mode,lock_id) VALUES (@id,@mode,@lock);",
                ("id", realActorIds[index]), ("mode", mode), ("lock", lockId));
            await using var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock(@lock)", control);
            acquire.Parameters.AddWithValue("lock", lockId);
            _ = await acquire.ExecuteScalarAsync();
        }
        async Task ReleaseBarrier(long lockId)
        {
            await using var release = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock(@lock)", control);
            release.Parameters.AddWithValue("lock", lockId);
            Check(await release.ExecuteScalarAsync() is true, "control session releases its observable PostgreSQL barrier");
        }
        async Task WaitForDatabaseBarrier(int minimumActive, int minimumAdvisory, string label)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await using var inspect = new NpgsqlCommand("""
                    SELECT count(*) FILTER (WHERE state='active' AND wait_event_type='Lock'),
                           count(*) FILTER (WHERE state='active' AND wait_event='advisory'),
                           count(*) FILTER (WHERE state='active' AND wait_event_type='Lock'
                                                AND wait_event IS DISTINCT FROM 'advisory'),
                           coalesce(string_agg(query,E'\n'),'')
                    FROM pg_catalog.pg_stat_activity
                    WHERE application_name='gds-task13-admin' AND pid<>pg_backend_pid()
                    """, control);
                await using var reader = await inspect.ExecuteReaderAsync();
                _ = await reader.ReadAsync();
                var active = reader.GetInt64(0);
                var advisory = reader.GetInt64(1);
                var nonAdvisory = reader.GetInt64(2);
                diagnostics.Add(reader.GetString(3));
                if (active >= minimumActive && advisory >= minimumAdvisory
                    && nonAdvisory >= Math.Max(0, minimumActive - minimumAdvisory)) return;
                await Task.Delay(50);
            }
            throw new Exception("ASSERT: observable PostgreSQL barrier was not reached: " + label);
        }
        async Task<(bool Active, bool Mandatory, long Version, string Hash, string? Concurrency, string? Security,
            DateTimeOffset? PasswordChangedAt, DateTimeOffset UpdatedAt,
            long SuccessAudits, long CurrentPasswordAudits, long ConcurrencyAudits, long ServiceAudits,
            long AdminStatusAudits, long Quota)> ReadState(int index)
        {
            await using var command = new NpgsqlCommand("""
                SELECT u."IsActive",u."MustChangePassword",u."ManagementVersion",u."PasswordHash",
                       u."ConcurrencyStamp",u."SecurityStamp",u."PasswordChangedAt",u."UpdatedAt",
                       (SELECT count(*) FROM app.audit_events WHERE entity_id=@entity AND action='auth.password.change' AND outcome='succeeded'),
                       (SELECT count(*) FROM app.audit_events WHERE actor_user_id=@id
                            AND action='auth.validation.reject' AND outcome='rejected'
                            AND entity_type='http_route' AND entity_id='auth-change-password'
                            AND security_context->>'Code'='current_password_invalid'),
                       (SELECT count(*) FROM app.audit_events WHERE actor_user_id=@id AND action='identity.concurrency.reject'
                            AND security_context->>'Code'='credential_state_changed'),
                       (SELECT count(*) FROM app.audit_events WHERE actor_user_id=@id
                            AND security_context->>'Code'='service_unavailable'),
                       (SELECT count(*) FROM app.audit_events WHERE entity_id=@entity
                            AND action='admin.user.status.change' AND outcome='succeeded'),
                       (SELECT coalesce(sum(attempt_count),0) FROM app.identity_rate_limits)
                FROM public."AspNetUsers" u WHERE u."Id"=@id
                """, control);
            command.Parameters.AddWithValue("id", realActorIds[index]);
            command.Parameters.AddWithValue("entity", realActorIds[index].ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            Check(await reader.ReadAsync(), "durable Task 13 actor exists");
            return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt64(8), reader.GetInt64(9),
                reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13));
        }
        static bool HasCookieMutation(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues("Set-Cookie", out var values)
            && values.Any(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        void AssertSuccessCookies(HttpResponseMessage response, Browser client)
        {
            Check(response.StatusCode == HttpStatusCode.NoContent
                && response.Headers.CacheControl?.NoStore == true,
                "production password success is exact 204 no-store");
            Check(HasCookieMutation(response, "GDS.Session") && HasCookieMutation(response, "GDS.AdminStepUp"),
                "production success deletes both cookies only after commit");
            Check(client.Cookies.GetCookies(client.Client.BaseAddress!)["GDS.Session"] is null
                && client.Cookies.GetCookies(new Uri(client.Client.BaseAddress!, "/api/admin/users"))["GDS.AdminStepUp"] is null,
                "successful browser has neither normal nor StepUp cookie");
        }
        void AssertRejectionCookies(HttpResponseMessage response, Browser client, string stepValue)
        {
            Check(!HasCookieMutation(response, "GDS.Session") && !HasCookieMutation(response, "GDS.AdminStepUp"),
                "production rejection neither renews nor deletes cookies");
            Check(client.Cookies.GetCookies(client.Client.BaseAddress!)["GDS.Session"] is not null
                && client.Cookies.GetCookies(new Uri(client.Client.BaseAddress!, "/api/admin/users"))["GDS.AdminStepUp"]?.Value == stepValue,
                "production rejection preserves both browser cookies");
        }
        async Task AssertAuditPrivacy(int index, params string[] sentinels)
        {
            await using var command = new NpgsqlCommand("""
                SELECT changes::text,security_context::text
                FROM app.audit_events WHERE actor_user_id=@id OR entity_id=@entity
                """, control);
            command.Parameters.AddWithValue("id", realActorIds[index]);
            command.Parameters.AddWithValue("entity", realActorIds[index].ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var durable = reader.GetString(0) + reader.GetString(1);
                foreach (var sentinel in sentinels)
                    Check(!durable.Contains(sentinel, StringComparison.Ordinal), "audit JSON excludes every secret sentinel");
            }
        }
        void CaptureBrowserCookieArtifacts(string label, Browser browser)
        {
            var cookies = browser.Cookies.GetCookies(browser.Client.BaseAddress!);
            AddPrivacyArtifact(label + " GDS.Session", cookies["GDS.Session"]?.Value);
            AddPrivacyArtifact(label + " internal antiforgery", cookies["GDS.Antiforgery"]?.Value);
            AddPrivacyArtifact(label + " request antiforgery", browser.Token);
        }
        async Task<string> IssueRealStepUp(int index, string label, Browser browser)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
            var user = await database.Users.AsNoTracking()
                .SingleAsync(item => item.Id == realActorIds[index]);
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<AdminStepUpService>().Issue(context, user);
            var header = context.Response.Headers.SetCookie.Single(value =>
                value?.StartsWith("GDS.AdminStepUp=", StringComparison.Ordinal) == true)!;
            var cookiePair = header.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            var value = cookiePair[(cookiePair.IndexOf('=') + 1)..];
            AddPrivacyArtifact(label + " GDS.AdminStepUp", value);
            browser.Cookies.Add(new Uri(browser.Client.BaseAddress!, "/api/admin/users"),
                new Cookie("GDS.AdminStepUp", value, "/api/admin"));
            return value;
        }

        // Production rejection path: real quota + real best-effort audit, with
        // sentinel inspection across HTTP, logs and durable JSON.
        await ResetActor(3);
        using (var privacy = NewBrowser())
        {
            Check((await Login(privacy, realActorNames[3])).StatusCode == HttpStatusCode.OK, "privacy actor login");
            CaptureBrowserCookieArtifacts("privacy browser", privacy);
            var privacyStep = await IssueRealStepUp(3, "privacy browser", privacy);
            var wrongSecret = secretSentinel + "-wrong-current";
            var proposedSecret = secretSentinel + "-proposed";
            var rejected = await privacy.Send("POST", "/api/auth/change-password", new
            {
                currentPassword = wrongSecret, newPassword = proposedSecret, confirmation = proposedSecret
            });
            var body = (await Error(rejected, HttpStatusCode.BadRequest, "current_password_invalid")).GetRawText();
            Check(!body.Contains(wrongSecret, StringComparison.Ordinal) && !body.Contains(proposedSecret, StringComparison.Ordinal),
                "production rejection body excludes secret sentinels");
            AssertRejectionCookies(rejected, privacy, privacyStep);
            var state = await ReadState(3);
            Check(state.Version == 10 && state.Quota == 1 && state.SuccessAudits == 0
                && state.CurrentPasswordAudits == 1,
                "wrong-current rejection commits one quota attempt and exactly one endpoint-owned durable audit");
            await AssertAuditPrivacy(3, wrongSecret, proposedSecret, privacyStep);
        }

        // Null/empty observed stamp fails before the supplied counting hasher
        // or conditional SQL and reports the same generic state conflict.
        foreach (var invalidStamp in new string?[] { null, string.Empty })
        {
            await ResetActor(3);
            await using (var invalidate = new NpgsqlCommand(
                "UPDATE public.\"AspNetUsers\" SET \"ConcurrencyStamp\"=@stamp WHERE \"Id\"=@id", control))
            {
                invalidate.Parameters.Add("stamp", NpgsqlDbType.Text).Value = invalidStamp ?? (object)DBNull.Value;
                invalidate.Parameters.AddWithValue("id", realActorIds[3]);
                Check(await invalidate.ExecuteNonQueryAsync() == 1, "install invalid observed stamp fixture");
            }
            var zeroHasher = new TrackingPasswordHasher();
            var invalidStampOutcome = await store.ChangePasswordAsync(new(realActorIds[3], password,
                secretSentinel + "-invalid-stamp", DateTimeOffset.UtcNow, "task13-invalid-stamp"), zeroHasher, default);
            var state = await ReadState(3);
            Check(invalidStampOutcome == ChangePasswordOutcome.CredentialStateChanged
                && zeroHasher.TotalCalls == 0 && state.Version == 10 && state.Hash == hashes[3],
                "null/empty observed concurrency stamp fails before password work or UPDATE");
        }

        // A real success-audit failure must roll the production transaction
        // back while its already committed distributed quota remains durable.
        await ResetActor(3);
        using (var auditFailure = NewBrowser())
        {
            Check((await Login(auditFailure, realActorNames[3])).StatusCode == HttpStatusCode.OK, "audit rollback actor login");
            CaptureBrowserCookieArtifacts("audit-failure browser", auditFailure);
            var before = await ReadState(3);
            await ExecuteControl("INSERT INTO app.task13_test_fail_audit(enabled) VALUES (TRUE)");
            var auditFailureStep = await IssueRealStepUp(3, "audit-failure browser", auditFailure);
            var proposed = secretSentinel + "-audit-rollback";
            var response = await auditFailure.Send("POST", "/api/auth/change-password", new
            {
                currentPassword = password, newPassword = proposed, confirmation = proposed
            });
            await Error(response, HttpStatusCode.ServiceUnavailable, "service_unavailable");
            AssertRejectionCookies(response, auditFailure, auditFailureStep);
            await ExecuteControl("DELETE FROM app.task13_test_fail_audit;");
            var after = await ReadState(3);
            Check(after.Active == before.Active && after.Mandatory == before.Mandatory
                && after.Hash == before.Hash && after.Version == before.Version
                && after.Concurrency == before.Concurrency && after.Security == before.Security
                && after.PasswordChangedAt == before.PasswordChangedAt && after.UpdatedAt == before.UpdatedAt
                && after.Quota == 1
                && after.SuccessAudits == 0 && after.ServiceAudits == 1,
                "production audit failure rolls back every credential field but preserves quota and rejection audit");
            await AssertAuditPrivacy(3, proposed, auditFailureStep);
        }

        // Two real HTTP changes observe one credential version. The PostgreSQL
        // trigger barrier holds the first UPDATE; pg_stat_activity proves the
        // second protected transaction is waiting before release.
        await ResetActor(0);
        using (var first = NewBrowser())
        using (var second = NewBrowser())
        using (var copied = NewBrowser())
        {
            Check((await Login(first, realActorNames[0])).StatusCode == HttpStatusCode.OK, "first race login");
            Check((await Login(second, realActorNames[0])).StatusCode == HttpStatusCode.OK, "second race login");
            CaptureBrowserCookieArtifacts("first race browser", first);
            CaptureBrowserCookieArtifacts("second race browser", second);
            copied.CopyFrom(first);
            CaptureBrowserCookieArtifacts("copied race browser", copied);
            var firstStep = await IssueRealStepUp(0, "first race browser", first);
            var secondStep = await IssueRealStepUp(0, "second race browser", second);
            await SetBarrier(0, "password", barrierBase + 1);
            var proposedA = secretSentinel + "-race-a";
            var proposedB = secretSentinel + "-race-b";
            var firstTask = first.Send("POST", "/api/auth/change-password", new { currentPassword=password,newPassword=proposedA,confirmation=proposedA });
            await WaitForDatabaseBarrier(1, 1, "first password update");
            var secondTask = second.Send("POST", "/api/auth/change-password", new { currentPassword=password,newPassword=proposedB,confirmation=proposedB });
            await WaitForDatabaseBarrier(2, 1, "second password update");
            await ReleaseBarrier(barrierBase + 1);
            var responses = await Task.WhenAll(firstTask, secondTask);
            Check(responses.Select(item => item.StatusCode).Order().SequenceEqual(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }),
                "same observed state has exactly one HTTP 204 and one HTTP 409");
            var winner = responses[0].StatusCode == HttpStatusCode.NoContent ? (responses[0], first, proposedA) : (responses[1], second, proposedB);
            var loser = responses[0].StatusCode == HttpStatusCode.Conflict ? (responses[0], first, firstStep) : (responses[1], second, secondStep);
            AssertSuccessCookies(winner.Item1, winner.Item2);
            await Error(loser.Item1, HttpStatusCode.Conflict, "credential_state_changed");
            AssertRejectionCookies(loser.Item1, loser.Item2, loser.Item3);
            await Error(await copied.Send("GET", "/api/auth/me"), HttpStatusCode.Unauthorized, "authentication_required");
            var state = await ReadState(0);
            var verifier = new PasswordHasher<ApplicationUser>();
            Check(state.Active && !state.Mandatory && state.Version == 11
                && state.Concurrency != state.Security && state.Concurrency?.Length == 64 && state.Security?.Length == 64
                && state.PasswordChangedAt is not null && state.UpdatedAt == state.PasswordChangedAt
                && state.SuccessAudits == 1 && state.ConcurrencyAudits == 1 && state.Quota == 2
                && verifier.VerifyHashedPassword(new ApplicationUser(), state.Hash, winner.Item3) != PasswordVerificationResult.Failed,
                "real race persists one hash/stamp/version/audit winner and two quota attempts");
            AddPrivacyArtifact("committed race password", winner.Item3);
            AddPrivacyArtifact("committed race PasswordHash", state.Hash);
            AddPrivacyArtifact("committed race SecurityStamp", state.Security);
            AddPrivacyArtifact("committed race ConcurrencyStamp", state.Concurrency);
            await AssertAuditPrivacy(0, proposedA, proposedB, firstStep, secondStep);
        }

        // Administrative disable wins: its real store transaction is held in
        // the trigger after locking the row; password change consumes quota,
        // then returns 409 without reactivating or overwriting the account.
        await ResetActor(1);
        using (var disableClient = NewBrowser())
        {
            Check((await Login(disableClient, realActorNames[1])).StatusCode == HttpStatusCode.OK, "disable-race login");
            CaptureBrowserCookieArtifacts("disable-race browser", disableClient);
            var disableStep = await IssueRealStepUp(1, "disable-race browser", disableClient);
            await SetBarrier(1, "disable", barrierBase + 2);
            var disableTask = store.MutateUserAsync(new(adminId, AdminUserMutationKind.Status,
                new(realActorIds[1], 10, "10", null, false), DateTimeOffset.UtcNow, "task13-disable-wins"), default);
            await WaitForDatabaseBarrier(1, 1, "disable update");
            var proposed = secretSentinel + "-disable-loser";
            var passwordTask = disableClient.Send("POST", "/api/auth/change-password", new { currentPassword=password,newPassword=proposed,confirmation=proposed });
            await WaitForDatabaseBarrier(2, 1, "password waiting behind disable");
            await ReleaseBarrier(barrierBase + 2);
            Check((await disableTask).Kind == AdminUserMutationOutcomeKind.Updated, "production administrative disable wins");
            var rejected = await passwordTask;
            await Error(rejected, HttpStatusCode.Conflict, "credential_state_changed");
            AssertRejectionCookies(rejected, disableClient, disableStep);
            var state = await ReadState(1);
            Check(!state.Active && state.Mandatory && state.Version == 11 && state.Hash == hashes[1]
                && state.PasswordChangedAt is null
                && state.Concurrency is { Length: 64 } && state.Security is { Length: 64 }
                && state.Concurrency != state.Security
                && state.SuccessAudits == 0 && state.ConcurrencyAudits == 1
                && state.AdminStatusAudits == 1 && state.Quota == 1,
                "disable winner stays inactive and password loser writes no credential or success audit");
            await AssertAuditPrivacy(1, proposed, disableStep);
        }

        // Password wins: its real HTTP transaction holds the row in the
        // trigger; the stale real administrative disable waits, rereads version
        // 11, and returns stale without changing IsActive or writing an audit.
        await ResetActor(2);
        using (var passwordWinner = NewBrowser())
        {
            Check((await Login(passwordWinner, realActorNames[2])).StatusCode == HttpStatusCode.OK, "password-wins login");
            CaptureBrowserCookieArtifacts("password-winner browser", passwordWinner);
            var passwordWinnerStep = await IssueRealStepUp(2, "password-winner browser", passwordWinner);
            await SetBarrier(2, "password", barrierBase + 3);
            var proposed = secretSentinel + "-password-winner";
            var passwordTask = passwordWinner.Send("POST", "/api/auth/change-password", new { currentPassword=password,newPassword=proposed,confirmation=proposed });
            await WaitForDatabaseBarrier(1, 1, "password winner update");
            var disableTask = store.MutateUserAsync(new(adminId, AdminUserMutationKind.Status,
                new(realActorIds[2], 10, "10", null, false), DateTimeOffset.UtcNow, "task13-stale-disable"), default);
            await WaitForDatabaseBarrier(2, 1, "stale disable behind password");
            await ReleaseBarrier(barrierBase + 3);
            AssertSuccessCookies(await passwordTask, passwordWinner);
            Check((await disableTask).Kind == AdminUserMutationOutcomeKind.StaleManagementVersion,
                "stale administrative disable cannot overwrite the password winner");
            var state = await ReadState(2);
            var verifier = new PasswordHasher<ApplicationUser>();
            Check(state.Active && !state.Mandatory && state.Version == 11 && state.Hash != hashes[2]
                && state.PasswordChangedAt is not null && state.UpdatedAt == state.PasswordChangedAt
                && state.Concurrency is { Length: 64 } && state.Security is { Length: 64 }
                && state.Concurrency != state.Security
                && state.SuccessAudits == 1 && state.ConcurrencyAudits == 0
                && state.AdminStatusAudits == 0 && state.Quota == 1
                && verifier.VerifyHashedPassword(new ApplicationUser(), state.Hash, proposed) != PasswordVerificationResult.Failed,
                "password winner remains active with one exact credential audit and quota attempt");
            AddPrivacyArtifact("committed final password", proposed);
            AddPrivacyArtifact("committed final PasswordHash", state.Hash);
            AddPrivacyArtifact("committed final SecurityStamp", state.Security);
            AddPrivacyArtifact("committed final ConcurrencyStamp", state.Concurrency);
            await AssertAuditPrivacy(2, proposed, passwordWinnerStep);
        }

        foreach (var index in Enumerable.Range(0, realActorIds.Length))
            await AssertAuditPrivacy(index, privacyArtifacts.Select(item => item.Value).ToArray());

        var listenerAuthority = bootstrap.BaseAddress!.GetLeftPart(UriPartial.Authority);
        string NormalizeOperationalListener(string value) => value
            .Replace(listenerAuthority, "<kestrel-listener>", StringComparison.OrdinalIgnoreCase);
        var surfaces = new[]
        {
            (Label: "PostgreSQL diagnostics", Value: string.Join('\n', diagnostics)),
            (Label: "HTTP bodies", Value: string.Join('\n', Browser.ResponseBodies)),
            (Label: "Kestrel/harness logs", Value: factory.Logs.ToString())
        };
        foreach (var artifact in privacyArtifacts)
        {
            foreach (var surface in surfaces)
            {
                var normalized = NormalizeOperationalListener(surface.Value);
                Check(!normalized.Contains(artifact.Value, StringComparison.Ordinal),
                    surface.Label + " excludes " + artifact.Label);
            }
        }

        await ExecuteControl("""
            DELETE FROM app.task13_test_barrier;
            DELETE FROM app.task13_test_fail_audit;
            DROP TRIGGER IF EXISTS aaa_task13_test_barrier ON public."AspNetUsers";
            DROP TRIGGER IF EXISTS aaa_task13_test_audit_failure ON app.audit_events;
            DROP FUNCTION IF EXISTS app.task13_test_barrier_trigger();
            DROP FUNCTION IF EXISTS app.task13_test_audit_failure_trigger();
            DROP TABLE IF EXISTS app.task13_test_barrier;
            DROP TABLE IF EXISTS app.task13_test_fail_audit;
            """);
        break;
    }
    default: throw new Exception("Unknown scenario");
}
Check(!factory.Logs.ToString().Contains(password),"password absent from logs");
foreach(var hash in hashes) Check(!factory.Logs.ToString().Contains(hash),"hash absent from logs");
foreach(var body in Browser.ResponseBodies) {
    Check(!body.Contains(password) && !hashes.Any(body.Contains),"no password/hash in any response");
    Check(!body.Contains("Data Source=",StringComparison.OrdinalIgnoreCase) && !body.Contains("ConnectionStrings",StringComparison.OrdinalIgnoreCase),"no connection string in response");
}
Check(!factory.Logs.ToString().Contains("Data Source=",StringComparison.OrdinalIgnoreCase),"no connection string in logs");
Console.WriteLine("PASS " + scenario);

sealed class Browser:IDisposable {
    public static List<string> ResponseBodies {get;}=new();
    public CookieContainer Cookies {get;}=new();
    public HttpClient Client {get;}
    public string? Token=>Cookies.GetCookies(Client.BaseAddress!)["XSRF-TOKEN"]?.Value;
    public Browser(Uri url) {Client=new(new HttpClientHandler{CookieContainer=Cookies,AllowAutoRedirect=false,ServerCertificateCustomValidationCallback=HttpClientHandler.DangerousAcceptAnyServerCertificateValidator}){BaseAddress=url};}
    public async Task<HttpResponseMessage> Send(string method,string path,object? body=null,bool csrf=true,string? token=null) {
        var request=new HttpRequestMessage(new HttpMethod(method),path);
        if(body is not null) request.Content=JsonContent.Create(body);
        if(csrf && (token??Token) is {} value) request.Headers.Add("X-XSRF-TOKEN",value);
        var response=await Client.SendAsync(request);
        ResponseBodies.Add(await response.Content.ReadAsStringAsync());
        return response;
    }
    public async Task<HttpResponseMessage> SendRaw(string method,string path,string body,bool csrf=true,string? token=null,bool chunked=false) {
        var request=new HttpRequestMessage(new HttpMethod(method),path) {
            Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")
        };
        if(chunked){request.Content.Headers.ContentLength=null;request.Headers.TransferEncodingChunked=true;}
        if(csrf && (token??Token) is {} value) request.Headers.Add("X-XSRF-TOKEN",value);
        var response=await Client.SendAsync(request);
        ResponseBodies.Add(await response.Content.ReadAsStringAsync());
        return response;
    }
    public void CopyFrom(Browser other) {
        foreach(Cookie cookie in other.Cookies.GetCookies(other.Client.BaseAddress!))
            Cookies.Add(Client.BaseAddress!,new Cookie(cookie.Name,cookie.Value,cookie.Path) {Secure=cookie.Secure, HttpOnly=cookie.HttpOnly});
    }
    public void Dispose()=>Client.Dispose();
}
sealed class SessionFactory(string directory, string environment="Testing", string scenario=""):WebApplicationFactory<Note> {
    public System.Text.StringBuilder Logs {get;}=new();
    public MemoryAuthenticationAuditStore Audit {get;}=new();
    public MemoryIdentitySecurityAudit SecurityAudit {get;}=new();
    public UnavailableIdentityReadiness UnavailableReadiness {get;}=new();
    public List<string> Order {get;}=[];
    public TrackingPasswordHasher PasswordHasher {get;}=new();
    public MemoryIdentityRateLimiter RateLimiter {get;}=new();
    public SessionPasswordChangeStore PasswordChangeStore {get;}=new(directory);
    public string RateLimitHmacSecretBase64 {get;}=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        var useRealPasswordStore=scenario=="change-password-real-postgres";
        builder.UseEnvironment(environment);
        builder.UseContentRoot(Path.GetFullPath("backend"));
        var testConfiguration=new Dictionary<string,string?> {
            ["Notes:Provider"]=environment=="Testing"?"Json":"PostgreSql",["Catalogs:Provider"]=environment=="Testing"?"InMemory":"PostgreSql",["Notes:DataPath"]=Path.Combine(directory,"notes.json"),
            ["DataProtection:KeyRingPath"]=Path.Combine(directory,"keys"),["Authentication:EnableTestHeaders"]=environment=="Testing"?"false":"true",
            ["ConnectionStrings:Postgres"]="Host=localhost;Database=unused;Username=unused;Password=unused;Ssl Mode=VerifyFull",
            ["ConnectionStrings:IdentityAdmin"]="Host=localhost;Database=unused;Username=identity;Password=unused;Ssl Mode=VerifyFull",
            ["IdentitySecurity:RateLimitHmacKeys:0:KeyId"]="rate-limit-v1",
            ["IdentitySecurity:RateLimitHmacKeys:0:SecretBase64"]=RateLimitHmacSecretBase64
        };
        if(useRealPasswordStore) {
            testConfiguration["ConnectionStrings:Postgres"]=Environment.GetEnvironmentVariable("GDS_TEST_PRIMARY_CONNECTION")
                ?? throw new InvalidOperationException("Missing disposable primary connection.");
            testConfiguration["ConnectionStrings:Catalogs"]=testConfiguration["ConnectionStrings:Postgres"];
            testConfiguration["ConnectionStrings:IdentityAdmin"]=Environment.GetEnvironmentVariable("GDS_TEST_IDENTITY_ADMIN_CONNECTION")
                ?? throw new InvalidOperationException("Missing disposable IdentityAdmin connection.");
        }
        if(scenario=="forwarded-trusted") {
            testConfiguration["HttpSecurity:KnownProxies:0"]="127.0.0.1";
            testConfiguration["HttpSecurity:KnownProxies:1"]="::1";
            testConfiguration["HttpSecurity:KnownProxies:2"]="::ffff:127.0.0.1";
        }
        builder.ConfigureAppConfiguration((_,config)=>config.AddInMemoryCollection(testConfiguration));
        builder.ConfigureServices(services=> {
            if(!useRealPasswordStore) {
                var options=new DbContextOptionsBuilder<ApplicationIdentityDbContext>().UseSqlite("Data Source="+Path.Combine(directory,"identity.db")).Options;
                services.AddScoped(_=>new ApplicationIdentityDbContext(options));
                services.AddSingleton<IAuthenticationAuditStore>(Audit);
                services.AddSingleton<IIdentitySecurityAudit>(SecurityAudit);

                // The session contract suite runs against SQLite-backed identity state and must
                // not reach the PostgreSQL quota implementation. Only the PostgreSQL integration
                // runner should exercise the distributed limiter.
                PasswordHasher.Order = Order;
                RateLimiter.Order = Order;
                SecurityAudit.Order = Order;
                services.RemoveAll<IPasswordHasher<ApplicationUser>>();
                services.AddSingleton<IPasswordHasher<ApplicationUser>>(PasswordHasher);
                services.RemoveAll<IIdentityRateLimiter>();
                services.AddSingleton<IIdentityRateLimiter>(RateLimiter);
            }
            if(!useRealPasswordStore && scenario.StartsWith("change-password", StringComparison.Ordinal)) {
                PasswordChangeStore.Order = Order;
                services.RemoveAll<IIdentityAdministrationStore>();
                services.AddSingleton<IIdentityAdministrationStore>(PasswordChangeStore);
            }
            if(useRealPasswordStore) { }
            else if(scenario=="health-readiness-unavailable")
                services.AddSingleton<IIdentityReadiness>(UnavailableReadiness);
            else
                services.AddSingleton<IIdentityReadiness, ReadyIdentityReadiness>();
            if(scenario=="auth-signin-failure")
                services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme,
                    options=>options.Events.OnSignedIn=_=>throw new InvalidOperationException("test-only cookie handler failure"));
            if(scenario is "limited-read" or "exception-logs")
                services.AddSingleton<INoteRepository>(provider=>new LimitedTestRepository(provider.GetRequiredService<NoteStore>(),
                    provider.GetRequiredService<ILogger<LimitedTestRepository>>(), scenario=="exception-logs"));
        });
        builder.ConfigureLogging(log=>{log.ClearProviders();log.SetMinimumLevel(LogLevel.Information);log.AddProvider(new CaptureLogs(Logs));});
    }
}
sealed class MemoryIdentitySecurityAudit:IIdentitySecurityAudit {
    public List<IdentitySecurityRejection> Events {get;}=[];
    public bool ObservedBeforeResponse {get;private set;}
    public List<string>? Order {get;set;}
    public bool FailWrites {get;set;}
    public Task RecordRejectionAsync(IdentitySecurityRejection rejection,CancellationToken cancellationToken=default) {
        Events.Add(rejection);
        Order?.Add("audit");
        ObservedBeforeResponse=true;
        if(FailWrites) throw new InvalidOperationException("test-only rejection audit failure");
        return Task.CompletedTask;
    }
}
sealed class TrackingPasswordHasher:IPasswordHasher<ApplicationUser> {
    readonly PasswordHasher<ApplicationUser> inner=new();
    public int VerifyCalls {get;private set;}
    public int HashCalls {get;private set;}
    public int TotalCalls=>VerifyCalls+HashCalls;
    public List<string>? Order {get;set;}
    public string HashPassword(ApplicationUser user,string password) {
        HashCalls++;
        Order?.Add("hasher");
        return inner.HashPassword(user,password);
    }
    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user,string hashedPassword,string providedPassword) {
        VerifyCalls++;
        Order?.Add("hasher");
        return inner.VerifyHashedPassword(user,hashedPassword,providedPassword);
    }
    public void Reset(){VerifyCalls=0;HashCalls=0;}
}
sealed class MemoryIdentityRateLimiter:IIdentityRateLimiter {
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    public int ConsumeCalls {get;private set;}
    public bool DenyNext {get;set;}
    public bool FailNext {get;set;}
    public List<IdentityRateLimitRequest> Requests {get;}=[];
    public List<string>? Order {get;set;}
    public Task<IdentityRateLimitDecision> ConsumeAsync(IdentityRateLimitRequest request,CancellationToken cancellationToken) {
        ConsumeCalls++;
        Order?.Add("quota");
        Requests.Add(request);
        if(FailNext){FailNext=false;throw new IdentityRateLimitUnavailableException();}
        if (DenyNext)
        {
            DenyNext = false;
            return Task.FromResult(new IdentityRateLimitDecision(false, request.Now.AddMinutes(1), 0));
        }

        var key = string.Concat(request.Policy, "|", request.Subject, "|", request.NormalizedIp ?? "unknown");
        var attempts = _counts.TryGetValue(key, out var current) ? current : 0;
        var denied = request.Policy == "login" && attempts >= 3;
        _counts[key] = attempts + 1;
        return Task.FromResult(new IdentityRateLimitDecision(!denied, denied ? request.Now.AddMinutes(1) : null, denied ? 0 : Math.Max(0, 3 - attempts)));
    }
    public Task<int> DeleteExpiredBatchAsync(int batchSize,CancellationToken cancellationToken)=>Task.FromResult(0);
    public void Reset(){ConsumeCalls=0;DenyNext=false;FailNext=false;Requests.Clear();_counts.Clear();}
}
sealed class SessionPasswordChangeStore(string directory):IIdentityAdministrationStore {
    private int _calls;
    public int Calls=>_calls;
    public List<string>? Order {get;set;}
    public ChangePasswordOutcome? ForcedOutcome {get;set;}
    public bool Fail {get;set;}
    public Task<AdminUserDirectoryResponse> ListUsersAsync(AdminUserDirectoryQuery query,CancellationToken cancellationToken)=>throw new NotSupportedException();
    public Task RecordSuccessfulListAsync(AdminUserDirectoryAudit audit,CancellationToken cancellationToken)=>throw new NotSupportedException();
    public Task<AdminCreateUserOutcome> CreateUserAsync(AdminCreateUserCommand command,AdminTemporaryCredentialFactory credentialFactory,CancellationToken cancellationToken)=>throw new NotSupportedException();
    public Task<int> TombstoneExpiredReceiptsAsync(int batchSize,CancellationToken cancellationToken)=>throw new NotSupportedException();
    public Task<AdminUserMutationOutcome> MutateUserAsync(AdminUserMutationCommand command,CancellationToken cancellationToken)=>throw new NotSupportedException();
    public async Task<ChangePasswordOutcome> ChangePasswordAsync(ChangePasswordCommand command,IPasswordHasher<ApplicationUser> hasher,CancellationToken cancellationToken) {
        Interlocked.Increment(ref _calls);
        Order?.Add("store");
        if(Fail) throw new IdentityAdminUnavailableException();
        if(ForcedOutcome is {} forced) return forced;
        var options=new DbContextOptionsBuilder<ApplicationIdentityDbContext>()
            .UseSqlite("Data Source="+Path.Combine(directory,"identity.db")).Options;
        await using var database=new ApplicationIdentityDbContext(options);
        var user=await database.Users.SingleOrDefaultAsync(x=>x.Id==command.ActorUserId && x.IsActive,cancellationToken);
        if(user?.PasswordHash is null) return ChangePasswordOutcome.CredentialStateChanged;
        if(hasher.VerifyHashedPassword(user,user.PasswordHash,command.CurrentPassword)==PasswordVerificationResult.Failed)
            return ChangePasswordOutcome.CurrentPasswordInvalid;
        if(hasher.VerifyHashedPassword(user,user.PasswordHash,command.NewPassword)!=PasswordVerificationResult.Failed)
            return ChangePasswordOutcome.PasswordReuse;
        user.PasswordHash=hasher.HashPassword(user,command.NewPassword);
        user.PasswordChangedAt=command.Now;
        user.MustChangePassword=false;
        user.ManagementVersion=checked(user.ManagementVersion+1);
        user.UpdatedAt=command.Now;
        user.ConcurrencyStamp=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        user.SecurityStamp=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await database.SaveChangesAsync(cancellationToken);
        return ChangePasswordOutcome.Succeeded;
    }
}
sealed class ReadyIdentityReadiness:IIdentityReadiness {
    public Task<IdentityReadinessResult> CheckAsync(CancellationToken cancellationToken)=>
        Task.FromResult(new IdentityReadinessResult(true,"ready",Array.Empty<string>()));
}
sealed class UnavailableIdentityReadiness:IIdentityReadiness {
    public int Calls {get;private set;}
    public Task<IdentityReadinessResult> CheckAsync(CancellationToken cancellationToken) {
        Calls++;
        return Task.FromResult(new IdentityReadinessResult(false,"identity_rate_limit_key_missing",new[]{"secret-key-id"}));
    }
}
sealed class MemoryAuthenticationAuditStore:IAuthenticationAuditStore {
    public bool FailWrites {get;set;}
    public List<AuthenticationAuditRecord> Events {get;}=[];
    public Task WriteAsync(AuthenticationAuditRecord record,CancellationToken cancellationToken=default) {
        if(FailWrites) throw new AuthenticationAuditUnavailableException();
        Events.Add(record);
        return Task.CompletedTask;
    }
}
sealed class CaptureLogs(System.Text.StringBuilder text):ILoggerProvider {
    public ILogger CreateLogger(string categoryName)=>new Capture(text);
    public void Dispose(){}
    sealed class Capture(System.Text.StringBuilder text):ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level>=LogLevel.Information;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> format) {
            lock(text) {
                text.AppendLine(format(state,exception));
                if(exception is not null) text.AppendLine(exception.ToString());
            }
        }
    }
}

// Preserve the real JSON repository and HTTP pipeline, but expose a bounded listing
// like PostgreSQL. This isolates the route's erroneous dependency on list pagination.
sealed class LimitedTestRepository(NoteStore inner, ILogger<LimitedTestRepository> logger, bool fail):INoteRepository {
    public const string ExceptionSentinel="test-only-exception-sentinel-49b18";
    public const string LogMessage="Injected repository operation failed.";
    public async Task<IReadOnlyList<Note>> List(string userId, CancellationToken cancellationToken=default) {
        if(fail) {
            try {throw new InvalidOperationException(ExceptionSentinel);}
            catch(InvalidOperationException error) {logger.LogWarning(error, LogMessage); throw new NoteRepositoryUnavailableException(error);}
        }
        return (await inner.List(userId,cancellationToken)).Take(1).ToArray();
    }
    public Task<Note?> Get(string userId,string id,CancellationToken cancellationToken=default)=>inner.Get(userId,id,cancellationToken);
    public Task<Note> Create(string userId,NoteInput input,CancellationToken cancellationToken=default)=>inner.Create(userId,input,cancellationToken);
    public Task<Note?> Update(string userId,string id,NoteInput input,CancellationToken cancellationToken=default)=>inner.Update(userId,id,input,cancellationToken);
    public Task<bool> Delete(string userId,string id,int expectedRevision,CancellationToken cancellationToken=default)=>inner.Delete(userId,id,expectedRevision,cancellationToken);
    public Task<Note?> SavePhase(string userId,string id,string stage,SavePhaseRequest request,CancellationToken cancellationToken=default)=>inner.SavePhase(userId,id,stage,request,cancellationToken);
    public Task<Note?> ValidatePhase(string userId,string id,string stage,ValidatePhaseRequest request,CancellationToken cancellationToken=default)=>inner.ValidatePhase(userId,id,stage,request,cancellationToken);
}
