using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Notes.Api.Database;

if (args.Length != 1) throw new InvalidOperationException("A scenario is required.");

const string password = "do-not-disclose-this-password";
const string secure = $"Host=db.internal.example;Database=gds;Username=runtime;Password={password};Ssl Mode=VerifyFull;Trust Server Certificate=false";
const string insecure = $"Host=db.internal.example;Database=gds;Username=runtime;Password={password};Ssl Mode=Prefer;Trust Server Certificate=false";

switch (args[0])
{
    case "production-secure":
        await using (var sources = Create("Production", secure))
        {
            Check(ReferenceEquals(sources.Primary, sources.Catalogs), "the default catalog path must reuse the primary data source");
        }
        break;
    case "production-prefer":
        RejectWithoutDisclosure("Production", insecure, null);
        break;
    case "production-require":
        RejectWithoutDisclosure("Production", secure.Replace("VerifyFull", "Require", StringComparison.Ordinal), null);
        break;
    case "production-trust":
        RejectWithoutDisclosure("Production", secure.Replace("Trust Server Certificate=false", "Trust Server Certificate=true", StringComparison.Ordinal), null);
        break;
    case "production-trust-alias":
        RejectWithoutDisclosure("Production", secure.Replace("Trust Server Certificate=false", "TrustServerCertificate=true", StringComparison.Ordinal), null);
        break;
    case "production-insecure-catalog":
        RejectWithoutDisclosure("Production", secure, insecure);
        break;
    case "development-prefer":
        var warning = new CaptureLogger();
        await using (var sources = Create("Development", insecure, logger: warning))
        {
            Check(sources.HasDevelopmentTlsException, "Development must expose the active TLS exception for a fixed warning");
            Check(warning.Messages.SequenceEqual([PostgresDataSources.DevelopmentTlsWarning]), "Development must emit one fixed warning");
            Check(!warning.Messages[0].Contains(password, StringComparison.Ordinal), "the warning must not disclose credentials");
            Check(!warning.Messages[0].Contains("db.internal.example", StringComparison.Ordinal), "the warning must not disclose the host");
        }
        break;
    case "testing-prefer":
        await using (var sources = Create("Testing", insecure))
        {
            Check(sources.HasDevelopmentTlsException, "Testing must expose the active TLS exception for a fixed warning");
        }
        break;
    case "testing-catalog-only":
        await using (var sources = Create("Testing", null, insecure))
        {
            Check(sources.HasDevelopmentTlsException, "the catalog-only Testing path must retain the warning");
            Check(sources.Catalogs is not null, "the catalog-only Testing path must construct its requested pool");
            try
            {
                _ = sources.Primary;
                throw new Exception("ASSERT: an absent primary connection was accepted");
            }
            catch (PostgresTlsConfigurationException) { }
        }
        break;
    case "staging-prefer":
        RejectWithoutDisclosure("Staging", insecure, null);
        break;
    case "invalid":
        RejectWithoutDisclosure("Production", password, null);
        break;
    default:
        throw new InvalidOperationException("Unknown scenario.");
}

static PostgresDataSources Create(
    string environment,
    string? primary,
    string? catalogs = null,
    Microsoft.Extensions.Logging.ILogger<PostgresDataSources>? logger = null)
{
    var values = new Dictionary<string, string?>();
    if (primary is not null) values["ConnectionStrings:Postgres"] = primary;
    if (catalogs is not null) values["ConnectionStrings:Catalogs"] = catalogs;
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    return new PostgresDataSources(
        configuration,
        new NamedEnvironment(environment),
        logger ?? NullLogger<PostgresDataSources>.Instance);
}

static void RejectWithoutDisclosure(string environment, string? primary, string? catalogs)
{
    try
    {
        using var sources = Create(environment, primary, catalogs);
        throw new Exception("ASSERT: insecure configuration was accepted");
    }
    catch (PostgresTlsConfigurationException exception)
    {
        Check(!exception.ToString().Contains(password, StringComparison.Ordinal), "failure must not disclose credentials");
        Check(!exception.ToString().Contains("db.internal.example", StringComparison.Ordinal), "failure must not disclose the host");
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("ASSERT: " + message);
}

sealed class NamedEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "PostgresConfigurationHarness";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<PostgresDataSources>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            Messages.Add(formatter(state, exception));
    }
}
