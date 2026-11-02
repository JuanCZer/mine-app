using Npgsql;

namespace Notes.Api.Database;

public sealed class PostgresTlsConfigurationException()
    : InvalidOperationException("La configuración TLS de PostgreSQL no cumple la política requerida.");

/// <summary>
/// Validates PostgreSQL transport policy once and owns the application's shared connection pools.
/// It never opens a physical connection during construction.
/// </summary>
public sealed class PostgresDataSources : IDisposable, IAsyncDisposable
{
    public const string DevelopmentTlsWarning =
        "PostgreSQL usa temporalmente una excepción TLS de Development/Testing; la conexión puede viajar en claro hasta corregir el servidor.";

    private readonly NpgsqlDataSource? _primary;
    private readonly NpgsqlDataSource _catalogs;
    private readonly NpgsqlDataSource? _identityAdmin;

    public NpgsqlDataSource Primary => _primary ?? throw new PostgresTlsConfigurationException();
    public NpgsqlDataSource Catalogs => _catalogs;
    public NpgsqlDataSource IdentityAdmin => _identityAdmin ?? throw new PostgresTlsConfigurationException();
    public bool HasDevelopmentTlsException { get; }

    public PostgresDataSources(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<PostgresDataSources> logger)
    {
        var permitsDevelopmentException = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var primaryValue = configuration.GetConnectionString("Postgres");
        var catalogValue = configuration.GetConnectionString("Catalogs");
        var identityAdminValue = configuration.GetConnectionString("IdentityAdmin");
        if (string.IsNullOrWhiteSpace(primaryValue) && !permitsDevelopmentException)
            throw new PostgresTlsConfigurationException();

        var primary = string.IsNullOrWhiteSpace(primaryValue) ? null : ParseRequired(primaryValue);
        var catalogs = string.IsNullOrWhiteSpace(catalogValue)
            ? primary ?? throw new PostgresTlsConfigurationException()
            : ParseRequired(catalogValue);
        var identityAdmin = string.IsNullOrWhiteSpace(identityAdminValue) ? null : ParseRequired(identityAdminValue);

        HasDevelopmentTlsException = (primary is not null && Validate(primary, permitsDevelopmentException))
            | Validate(catalogs, permitsDevelopmentException)
            | (identityAdmin is not null && Validate(identityAdmin, permitsDevelopmentException));
        if (HasDevelopmentTlsException)
            logger.LogWarning("{Warning}", DevelopmentTlsWarning);

        NpgsqlDataSource? createdPrimary = null;
        NpgsqlDataSource? createdCatalogs = null;
        NpgsqlDataSource? createdIdentityAdmin = null;
        try
        {
            if (primary is not null) createdPrimary = NpgsqlDataSource.Create(primary);
            createdCatalogs = createdPrimary is not null && string.Equals(primary!.ConnectionString, catalogs.ConnectionString, StringComparison.Ordinal)
                ? createdPrimary
                : NpgsqlDataSource.Create(catalogs);
            // Identity administration always owns a distinct pool, even when a
            // deployment accidentally supplies the same physical endpoint.
            createdIdentityAdmin = identityAdmin is null ? null : NpgsqlDataSource.Create(identityAdmin);
            _primary = createdPrimary;
            _catalogs = createdCatalogs;
            _identityAdmin = createdIdentityAdmin;
        }
        catch
        {
            createdIdentityAdmin?.Dispose();
            if (!ReferenceEquals(createdCatalogs, createdPrimary)) createdCatalogs?.Dispose();
            createdPrimary?.Dispose();
            throw new PostgresTlsConfigurationException();
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_catalogs, _primary)) _catalogs.Dispose();
        _identityAdmin?.Dispose();
        _primary?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!ReferenceEquals(_catalogs, _primary)) await _catalogs.DisposeAsync();
        if (_identityAdmin is not null) await _identityAdmin.DisposeAsync();
        if (_primary is not null) await _primary.DisposeAsync();
    }

    private static NpgsqlConnectionStringBuilder ParseRequired(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new PostgresTlsConfigurationException();
        try
        {
            return new NpgsqlConnectionStringBuilder(value);
        }
        catch
        {
            // Configuration failures are deliberately opaque: never echo a connection string.
            throw new PostgresTlsConfigurationException();
        }
    }

    private static bool Validate(NpgsqlConnectionStringBuilder value, bool permitsDevelopmentException)
    {
        // Npgsql 10 ignores this legacy switch, but accepting an explicit bypass
        // would make the deployment intent ambiguous and violates this policy.
        if (value.TryGetValue("Trust Server Certificate", out var trust) && trust is true)
            throw new PostgresTlsConfigurationException();
        if (value.SslMode == SslMode.VerifyFull) return false;
        if (!permitsDevelopmentException) throw new PostgresTlsConfigurationException();
        return true;
    }
}
