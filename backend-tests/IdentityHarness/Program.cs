// SQLite is a test-only persistent store; this does not validate PostgreSQL DDL.
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notes.Api.Models;

var assembly = typeof(Note).Assembly;
Type Required(string name) => assembly.GetType("Notes.Api.Auth." + name)
    ?? throw new Exception("ASSERT: missing persisted identity component " + name);
void Check(bool value, string message) { if (!value) throw new Exception("ASSERT: " + message); }
var userType = Required("ApplicationUser");
var contextType = Required("ApplicationIdentityDbContext");
var authType = Required("AuthenticationService");
var managerType = typeof(UserManager<>).MakeGenericType(userType);
var dbPath = args[1];
var password = "Aa1!" + Guid.NewGuid().ToString("N");
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused_test_registration"
}).Build();
ServiceProvider Provider()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(configuration);
    Required("IdentityServiceCollectionExtensions").GetMethod("AddPersistedIdentity")!
        .Invoke(null, [services]);
    var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
    var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
    builder.UseSqlite("Data Source=" + dbPath);
    // Replace only the context inside this test process. Runtime registration remains Npgsql.
    services.AddScoped(contextType, _ => Activator.CreateInstance(contextType, builder.Options)!);
    return services.BuildServiceProvider();
}
dynamic Manager(IServiceProvider p) => p.GetRequiredService(managerType);
dynamic Auth(IServiceProvider p) => p.GetRequiredService(authType);
async Task<dynamic> Create(IServiceProvider p, bool active = true)
{
    dynamic user = Activator.CreateInstance(userType)!;
    user.UserName = "Operator.One";
    user.DisplayName = "Operador de prueba";
    user.IsActive = active;
    IdentityResult result = await Manager(p).CreateAsync(user, password);
    Check(result.Succeeded, "account creation: " + string.Join(",", result.Errors.Select(e => e.Code)));
    return user;
}
using (var provider = Provider())
using (var scope = provider.CreateScope())
    await ((DbContext)scope.ServiceProvider.GetRequiredService(contextType)).Database.EnsureCreatedAsync();

switch (args[0])
{
    case "persistence":
        Guid id;
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            dynamic user = await Create(scope.ServiceProvider);
            id = user.Id;
            Check(id != Guid.Empty, "UUID must be generated");
        }
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            dynamic manager = Manager(scope.ServiceProvider);
            dynamic user = await manager.FindByNameAsync("OPERATOR.ONE");
            Check(user != null && user.Id == id, "account must survive provider recreation");
            Check(user.DisplayName == "Operador de prueba" && user.IsActive, "custom fields persisted");
            Check(user.CreatedAt <= user.UpdatedAt, "timestamps persisted");
            Check(await manager.CheckPasswordAsync(user, password), "Identity hash verifies after reopen");
            Check(Convert.FromBase64String((string)user.PasswordHash)[0] == 1, "Identity V3 versioned hash");
            Check(((IList<string>)await manager.GetRolesAsync(user)).Count == 0, "no default roles");
            dynamic loggedIn = await Auth(scope.ServiceProvider).AuthenticateAsync(" operator.one ", password);
            Check(loggedIn != null && loggedIn.Id == id, "normalized successful authentication");
        }
        break;
    case "inactive":
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            await Create(scope.ServiceProvider, false);
            var result = await Auth(scope.ServiceProvider).AuthenticateAsync("Operator.One", password);
            Check(result == null, "inactive account must fail authentication with correct password");
        }
        break;
    case "uniform":
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            await Create(scope.ServiceProvider);
            var wrong = await Auth(scope.ServiceProvider).AuthenticateAsync("Operator.One", password + "wrong");
            var missing = await Auth(scope.ServiceProvider).AuthenticateAsync("unknown", password);
            Check(wrong == null && missing == null, "wrong and unknown share the public failure result");
        }
        break;
    case "lockout":
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            await Create(scope.ServiceProvider);
            for (var attempt = 0; attempt < 5; attempt++)
                Check(await Auth(scope.ServiceProvider).AuthenticateAsync("Operator.One", password + "wrong") == null, "invalid password fails");
        }
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            dynamic manager = Manager(scope.ServiceProvider);
            dynamic user = await manager.FindByNameAsync("Operator.One");
            Check(await manager.IsLockedOutAsync(user), "lockout persists across providers");
            Check(await Auth(scope.ServiceProvider).AuthenticateAsync("Operator.One", password) == null, "locked account cannot authenticate");
        }
        break;
    case "provision":
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            var output = new StringWriter();
            var commandType = Required("ProvisionUserCommand");
            dynamic command = ActivatorUtilities.CreateInstance(scope.ServiceProvider, commandType);
            var exitCode = await command.RunAsync(new[] {"--username", "Admin.One", "--display-name", "Administrador", "--role", "administrador"}, new StringReader(password + "\n"), output);
            Check(exitCode == 0, "provision succeeds via stdin");
            Check(!output.ToString().Contains(password), "no password in output");
            dynamic manager = Manager(scope.ServiceProvider);
            dynamic user = await manager.FindByNameAsync("Admin.One");
            Check(user != null && await manager.CheckPasswordAsync(user, password), "provisioned Identity account");
            var roles = (IList<string>)await manager.GetRolesAsync(user);
            Check(roles.SequenceEqual(new[] { "administrador" }), "only explicitly selected role assigned");
            foreach (var role in new[] { "operador", "validador", "administrador" })
                Check(await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>().RoleExistsAsync(role), "minimum role exists");
            Check(await command.RunAsync(new[] {"--username", "NoRole", "--display-name", "No role"}, new StringReader(password + "\n"), output) != 0, "explicit role required");
            Check(await manager.FindByNameAsync("NoRole") == null, "invalid command creates no user");
        }
        break;
    case "password-short":
    case "password-phrase":
    case "password-unicode":
        using (var provider = Provider())
        using (var scope = provider.CreateScope())
        {
            var secret = args[0] switch {
                "password-short" => "Aa1!abcdefghij", // exactly 14, satisfies the old composition rules
                "password-phrase" => "una frase larga",
                _ => "  árbol azul en otoño  "
            };
            var output = new StringWriter();
            dynamic command = ActivatorUtilities.CreateInstance(scope.ServiceProvider, Required("ProvisionUserCommand"));
            var exitCode = await command.RunAsync(new[] { "--username", "Phrase.User", "--display-name", "Phrase", "--role", "operador" }, new StringReader(secret + "\n"), output);
            if (args[0] == "password-short")
            {
                Check(exitCode != 0, "14 characters must be rejected even with every composition category");
                Check(await Manager(scope.ServiceProvider).FindByNameAsync("Phrase.User") == null, "rejected password creates no account");
            }
            else
            {
                Check(exitCode == 0, "15+ character phrase without arbitrary composition must be accepted: " + output);
                Check(await Auth(scope.ServiceProvider).AuthenticateAsync("Phrase.User", secret) != null, "exact spaces and Unicode must authenticate");
                if (args[0] == "password-unicode")
                {
                    Check(await Auth(scope.ServiceProvider).AuthenticateAsync("Phrase.User", secret.Trim()) == null, "spaces must not be trimmed");
                    Check(await Auth(scope.ServiceProvider).AuthenticateAsync("Phrase.User", secret.Normalize(System.Text.NormalizationForm.FormD)) == null, "Unicode must not be rewritten");
                }
            }
        }
        break;
    case "runtime-boundary":
        await RuntimeBoundaryScenarios.RunAsync(dbPath);
        break;
    case "credential-cost":
    case "password-maximum":
    case "concurrent-lockout":
    case "provision-password-maximum":
    case "session-role-cardinality":
    case "change-password-policy-runes":
        await AuthenticationSecurityScenarios.RunAsync(args[0], dbPath);
        break;
    case "bootstrap-config":
        var factoryType = Required("IdentityBootstrapConnectionFactory");
        var createMethod = factoryType.GetMethod("CreateFromEnvironment", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!;
        var environmentType = typeof(IHostEnvironment);
        var local = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused_runtime;Username=runtime_user;Password=secret;SslMode=VerifyFull"
        }).Build();
        var environment = new HostBuilder().Build().Services.GetRequiredService<IHostEnvironment>();
        try
        {
            createMethod.Invoke(null, [environment]);
            throw new Exception("bootstrap factory should reject missing POSTGRES_DEPLOY_URL");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Environment.SetEnvironmentVariable("POSTGRES_DEPLOY_URL", "Host=localhost;Database=deploy_db;Username=deployer;Password=secret;SslMode=Disable");
        try
        {
            createMethod.Invoke(null, [environment]);
            throw new Exception("bootstrap factory should reject non-VerifyFull TLS");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Environment.SetEnvironmentVariable("POSTGRES_DEPLOY_URL", "Host=localhost;Database=deploy_db;Username=deployer;Password=secret;SslMode=VerifyFull");
        var ds = createMethod.Invoke(null, [environment]);
        Check(ds != null, "bootstrap data source must be created for valid deploy URL");
        (ds as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Environment.SetEnvironmentVariable("POSTGRES_DEPLOY_URL", null);
        break;
    default: throw new Exception("unknown test case");
}
Console.WriteLine("PASS " + args[0]);
