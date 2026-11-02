using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notes.Api.Auth;
using Notes.Api.IdentityAdministration;

internal static class AuthenticationSecurityScenarios
{
    public static async Task RunAsync(string scenario, string path)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddPersistedIdentity();
        services.AddScoped(_ => new ApplicationIdentityDbContext(new DbContextOptionsBuilder<ApplicationIdentityDbContext>()
            .UseSqlite("Data Source=" + path).Options));
        using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Database.EnsureCreatedAsync();

        switch (scenario)
        {
            case "credential-cost":
                await VerifyCredentialCostAsync(provider);
                break;
            case "password-maximum":
                await VerifyPasswordMaximumAsync(provider);
                break;
            case "concurrent-lockout":
                await VerifyConcurrentLockoutAsync(provider);
                break;
            case "provision-password-maximum":
                await VerifyProvisioningPasswordMaximumAsync(provider);
                break;
            case "session-role-cardinality":
                await VerifySessionRoleCardinalityAsync(provider);
                break;
            case "change-password-policy-runes":
                VerifyChangePasswordPolicyRunes();
                break;
            default:
                throw new InvalidOperationException("Unknown authentication security scenario.");
        }
    }

    private static void VerifyChangePasswordPolicyRunes()
    {
        var fourteen = string.Concat(Enumerable.Repeat("😀", 14));
        var fifteen = string.Concat(Enumerable.Repeat("😀", 15));
        var oneHundredTwentyEight = string.Concat(Enumerable.Repeat("😀", 128));
        var oneHundredTwentyNine = string.Concat(Enumerable.Repeat("😀", 129));
        Check(!AuthEndpoints.HasValidNewPasswordLength(fourteen), "14 supplementary runes must fail");
        Check(AuthEndpoints.HasValidNewPasswordLength(fifteen), "15 supplementary runes must pass");
        Check(AuthEndpoints.HasValidNewPasswordLength(oneHundredTwentyEight), "128 supplementary runes must pass");
        Check(!AuthEndpoints.HasValidNewPasswordLength(oneHundredTwentyNine), "129 supplementary runes must fail");
        var composed = string.Concat(Enumerable.Repeat("á", 15));
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        Check(AuthEndpoints.HasValidNewPasswordLength(composed), "composed input remains valid");
        Check(AuthEndpoints.HasValidNewPasswordLength(decomposed), "decomposed scalars are counted without normalization");
        Check(!string.Equals(composed, decomposed, StringComparison.Ordinal), "test values must remain byte-distinct");

        var classifier = typeof(IdentityAdministrationStore).GetMethod(
            "ClassifyPasswordUpdateCount",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("ASSERT: password update-count classifier must be testable");
        Check((ChangePasswordOutcome?)classifier.Invoke(null, [0])
            == ChangePasswordOutcome.CredentialStateChanged,
            "zero updated rows must map to credential_state_changed");
        Check(classifier.Invoke(null, [1]) is null,
            "exactly one updated row must permit commit");
        try
        {
            _ = classifier.Invoke(null, [2]);
            throw new Exception("ASSERT: multiple updated rows must fail closed");
        }
        catch (System.Reflection.TargetInvocationException error)
            when (error.InnerException is IdentityAdminUnavailableException)
        {
        }
    }

    private static async Task VerifySessionRoleCardinalityAsync(ServiceProvider provider)
    {
        const string password = "a valid security phrase";
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        var user = new ApplicationUser { UserName = "Role.Cardinality", DisplayName = "Role Cardinality" };
        Check((await users.CreateAsync(user, password)).Succeeded, "seed cardinality user");

        async Task MustReject(string state)
        {
            try
            {
                await SessionAuthentication.PrincipalAsync(user, database);
                throw new Exception("ASSERT: " + state + " role state must fail closed");
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        await MustReject("zero");
        Check((await roles.CreateAsync(new IdentityRole<Guid>("custom") { Id = Guid.NewGuid() })).Succeeded, "seed noncanonical role");
        Check((await users.AddToRoleAsync(user, "custom")).Succeeded, "assign noncanonical role");
        await MustReject("noncanonical");
        Check((await users.RemoveFromRoleAsync(user, "custom")).Succeeded, "remove noncanonical role");
        foreach (var role in new[] { "operador", "validador" })
            Check((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded, "seed " + role);
        Check((await users.AddToRoleAsync(user, "operador")).Succeeded, "assign canonical role");
        var principal = await SessionAuthentication.PrincipalAsync(user, database);
        Check(principal.IsInRole("operador"), "one canonical role must create the principal");
        Check((await users.AddToRoleAsync(user, "validador")).Succeeded, "assign second canonical role");
        await MustReject("multiple");
    }

    private static async Task VerifyCredentialCostAsync(ServiceProvider provider)
    {
        const string password = "a valid security phrase";
        await using (var seed = provider.CreateAsyncScope())
        {
            var users = seed.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Check((await users.CreateAsync(new ApplicationUser { UserName = "Active", DisplayName = "Active" }, password)).Succeeded, "seed active");
            Check((await users.CreateAsync(new ApplicationUser { UserName = "Inactive", DisplayName = "Inactive", IsActive = false }, password)).Succeeded, "seed inactive");
            var locked = new ApplicationUser { UserName = "Locked", DisplayName = "Locked", LockoutEnabled = true, LockoutEnd = DateTimeOffset.UtcNow.AddHours(1) };
            Check((await users.CreateAsync(locked, password)).Succeeded, "seed locked");
        }

        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        var tracker = new TrackingPasswordHasher(scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>());
        var authentication = new AuthenticationService(
            scope.ServiceProvider.GetRequiredService<RuntimeIdentityStore>(),
            scope.ServiceProvider.GetRequiredService<ILookupNormalizer>(), tracker);
        var originalCount = await database.Users.CountAsync();

        foreach (var username in new[] { "Missing", "Inactive", "Locked" })
        {
            tracker.Reset();
            Check(await authentication.AuthenticateAsync(username, password) is null, username + " must not authenticate");
            Check(tracker.CostOperations == 1, username + " must execute exactly one password work-factor operation");
        }
        Check(await database.Users.CountAsync() == originalCount, "unknown-user work must not persist a dummy account");
    }

    private static async Task VerifyPasswordMaximumAsync(ServiceProvider provider)
    {
        var accepted = new string('á', 128);
        var acceptedSupplementaryUnicode = string.Concat(Enumerable.Repeat("😀", 128));
        await using (var seed = provider.CreateAsyncScope())
        {
            var users = seed.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Check((await users.CreateAsync(new ApplicationUser { UserName = "Maximum", DisplayName = "Maximum" }, accepted)).Succeeded, "seed 128-character password");
            Check((await users.CreateAsync(new ApplicationUser { UserName = "Maximum.Unicode", DisplayName = "Maximum Unicode" }, acceptedSupplementaryUnicode)).Succeeded, "seed 128-scalar Unicode password");
        }

        await using var scope = provider.CreateAsyncScope();
        var tracker = new TrackingPasswordHasher(scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>());
        var authentication = new AuthenticationService(
            scope.ServiceProvider.GetRequiredService<RuntimeIdentityStore>(),
            scope.ServiceProvider.GetRequiredService<ILookupNormalizer>(), tracker);

        Check(await authentication.AuthenticateAsync("Maximum", accepted) is not null, "128 Unicode characters must remain valid without rewriting");
        Check(tracker.CostOperations == 1, "accepted password must be verified once");
        tracker.Reset();
        Check(await authentication.AuthenticateAsync("Maximum.Unicode", acceptedSupplementaryUnicode) is not null, "128 supplementary Unicode characters must remain valid without rewriting");
        Check(tracker.CostOperations == 1, "accepted supplementary Unicode password must be verified once");
        foreach (var rejected in new[] { new string('á', 129), new string('x', 8 * 1024 * 1024) })
        {
            tracker.Reset();
            Check(await authentication.AuthenticateAsync("Maximum", rejected) is null, "oversized password must fail");
            Check(tracker.CostOperations == 0, "oversized password must be rejected before the password hasher");
        }
    }

    private static async Task VerifyConcurrentLockoutAsync(ServiceProvider provider)
    {
        const string password = "a valid security phrase";
        await using (var seed = provider.CreateAsyncScope())
        {
            var users = seed.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Check((await users.CreateAsync(new ApplicationUser { UserName = "Concurrent", DisplayName = "Concurrent" }, password)).Succeeded, "seed concurrent user");
        }

        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<RuntimeIdentityStore>();
        var snapshots = new List<ApplicationUser>();
        for (var index = 0; index < 8; index++)
            snapshots.Add((await store.FindByNameAsync("CONCURRENT"))!);
        var successfulUpdates = 0;
        foreach (var snapshot in snapshots)
            if (await store.RecordFailedAccessAsync(snapshot)) successfulUpdates++;

        var saved = await scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Users.SingleAsync(x => x.NormalizedUserName == "CONCURRENT");
        Check(successfulUpdates == 5, "five accepted failures must be atomically counted before lockout");
        Check(saved.LockoutEnd > DateTimeOffset.UtcNow && saved.AccessFailedCount == 0, "stale concurrent snapshots must trigger lockout at five failures");
    }

    private static async Task VerifyProvisioningPasswordMaximumAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var command = scope.ServiceProvider.GetRequiredService<ProvisionUserCommand>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        async Task<int> Provision(string username, string secret, bool fromEnvironment = false)
        {
            var output = new StringWriter();
            if (fromEnvironment) Environment.SetEnvironmentVariable("GDS_PROVISION_PASSWORD", secret);
            var result = await command.RunAsync(
                ["--username", username, "--display-name", username, "--role", "operador"],
                fromEnvironment ? new StringReader(string.Empty) : new StringReader(secret + "\n"), output);
            Check(!output.ToString().Contains(secret, StringComparison.Ordinal), "provisioning output must never disclose the password");
            return result;
        }

        var tooLong = new string('x', 129);
        Check(await Provision("Too.Long", tooLong) != 0, "129 characters must be rejected");
        Check(await users.FindByNameAsync("Too.Long") is null, "129-character rejection creates no user");
        Check(!await roles.RoleExistsAsync("operador"), "129-character rejection occurs before UserManager and role writes");

        var oversized = new string('x', 8 * 1024 * 1024);
        Check(await Provision("Oversized", oversized, fromEnvironment: true) != 0, "8 MiB password must be rejected");
        Check(Environment.GetEnvironmentVariable("GDS_PROVISION_PASSWORD") is null, "password environment variable must be cleared after reading");
        Check(await users.FindByNameAsync("Oversized") is null, "oversized rejection creates no user");
        Check(!await roles.RoleExistsAsync("operador"), "oversized rejection occurs before UserManager and role writes");

        var ascii = new string('a', 128);
        Check(await Provision("Maximum.Ascii", ascii) == 0, "128 ASCII characters must be accepted");
        var asciiUser = await users.FindByNameAsync("Maximum.Ascii");
        Check(asciiUser is not null && await users.CheckPasswordAsync(asciiUser, ascii), "128 ASCII characters must be preserved");

        var unicode = string.Concat(Enumerable.Repeat("😀", 128));
        Check(await Provision("Maximum.Unicode.Provision", unicode) == 0, "128 supplementary Unicode characters must be accepted");
        var unicodeUser = await users.FindByNameAsync("Maximum.Unicode.Provision");
        Check(unicodeUser is not null && await users.CheckPasswordAsync(unicodeUser, unicode), "128 supplementary Unicode characters must be preserved");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("ASSERT: " + message);
    }

    private sealed class TrackingPasswordHasher(IPasswordHasher<ApplicationUser> inner) : IPasswordHasher<ApplicationUser>
    {
        public int CostOperations { get; private set; }
        public string HashPassword(ApplicationUser user, string password)
        {
            CostOperations++;
            return inner.HashPassword(user, password);
        }
        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        {
            CostOperations++;
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
        public void Reset() => CostOperations = 0;
    }
}
