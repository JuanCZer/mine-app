using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notes.Api.Auth;

// Execute real application authentication and SQL against SQLite. The interceptor
// rejects forbidden SET columns, modelling the grant boundary (not PostgreSQL ACLs).
internal static class RuntimeBoundaryScenarios
{
    public static async Task RunAsync(string path)
    {
        var audit = new AuthenticationWriteAudit();
        var services = new ServiceCollection().AddLogging();
        services.AddPersistedIdentity();
        services.AddScoped(_ => new ApplicationIdentityDbContext(new DbContextOptionsBuilder<ApplicationIdentityDbContext>()
            .UseSqlite("Data Source=" + path).AddInterceptors(audit).Options));
        using var provider = services.BuildServiceProvider();
        const string password = "a sufficiently long phrase";
        // This test isolates the UPDATE boundary from the independent password-policy tests.
        var seedPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        Guid id;
        string originalStamp;
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "Boundary.User", DisplayName = "Original name", Email = "original@example.invalid" };
            Check((await users.CreateAsync(user, seedPassword)).Succeeded, "seed user");
            user.PasswordHash = new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2
            })).HashPassword(user, password);
            Check((await users.UpdateAsync(user)).Succeeded, "seed legacy hash via administrative manager");
            id = user.Id;
            originalStamp = user.SecurityStamp!;
        }
        audit.Enforce = true;
        using (var scope = provider.CreateScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
            Check(await auth.AuthenticateAsync("Boundary.User", "wrong") is null, "wrong password fails");
            var loggedIn = await auth.AuthenticateAsync("Boundary.User", password);
            Check(loggedIn != null && loggedIn.AccessFailedCount == 0, "successful authentication resets failures");
            Check(Convert.FromBase64String(loggedIn!.PasswordHash!)[0] == 1, "legacy hash upgraded on login");
            // Administrative dirty tracked fields must never leak through runtime writes.
            loggedIn.IsActive = false;
            loggedIn.UserName = "changed";
            loggedIn.NormalizedUserName = "CHANGED";
            loggedIn.Email = "changed@example.invalid";
            loggedIn.NormalizedEmail = "CHANGED@EXAMPLE.INVALID";
            loggedIn.DisplayName = "changed";
            var runtimeType = typeof(ApplicationUser).Assembly.GetType("Notes.Api.Auth.RuntimeIdentityStore");
            Check(runtimeType != null, "limited runtime store must be available for Task 2");
            dynamic runtime = scope.ServiceProvider.GetRequiredService(runtimeType!);
            Check(await runtime.RotateSecurityStampAsync(loggedIn), "Task 2 stamp rotation succeeds through limited path");
        }
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
            var saved = await db.Users.SingleAsync(x => x.Id == id);
            Check(saved.IsActive && saved.UserName == "Boundary.User" && saved.NormalizedUserName == "BOUNDARY.USER", "runtime must preserve account activation and username");
            Check(saved.Email == "original@example.invalid" && saved.NormalizedEmail == "ORIGINAL@EXAMPLE.INVALID" && saved.DisplayName == "Original name", "runtime must preserve email and name");
            Check(saved.SecurityStamp != originalStamp, "global logout stamp persists");
            var auth = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
            for (var attempt = 0; attempt < 5; attempt++)
                Check(await auth.AuthenticateAsync("Boundary.User", "wrong") is null, "wrong password fails");
            Check(await auth.AuthenticateAsync("Boundary.User", password) is null, "lockout blocks correct password");
        }
        using (var scope = provider.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Users.SingleAsync(x => x.Id == id);
            Check(saved.LockoutEnd > DateTimeOffset.UtcNow && saved.AccessFailedCount == 0, "lockout persists");
        }
        Check(audit.Columns.Contains("PasswordHash") && audit.Columns.Contains("SecurityStamp") && audit.Columns.Contains("LockoutEnd") && audit.Columns.Contains("AccessFailedCount"), "exercise rehash, stamp and lockout SQL");
        Console.WriteLine("Runtime UPDATE columns: " + string.Join(", ", audit.Columns.Order()));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("ASSERT: " + message);
    }
}

internal sealed class AuthenticationWriteAudit : DbCommandInterceptor
{
    public bool Enforce { get; set; }
    public HashSet<string> Columns { get; } = [];
    private static readonly HashSet<string> Allowed = ["PasswordHash", "SecurityStamp", "ConcurrencyStamp", "AccessFailedCount", "LockoutEnd", "UpdatedAt"];

    private void Inspect(DbCommand command)
    {
        if (!Enforce || !Regex.IsMatch(command.CommandText, "UPDATE\\s+\"AspNetUsers\"", RegexOptions.IgnoreCase)) return;
        var set = Regex.Match(command.CommandText, "\\bSET\\b(.*?)\\bWHERE\\b", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
        var columns = Regex.Matches(set, "\"([^\"]+)\"\\s*=").Select(x => x.Groups[1].Value).ToArray();
        if (columns.Length == 0 || columns.Any(x => !Allowed.Contains(x)))
            throw new Exception("ASSERT: runtime UPDATE exceeds authentication grant: " + string.Join(", ", columns));
        Columns.UnionWith(columns);
    }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }
}
