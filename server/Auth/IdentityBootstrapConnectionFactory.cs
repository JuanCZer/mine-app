using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Notes.Api.Auth;

public static class IdentityBootstrapConnectionFactory
{
    public static NpgsqlDataSource CreateFromEnvironment(IHostEnvironment? environment = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_DEPLOY_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("POSTGRES_DEPLOY_URL es obligatorio para el bootstrap administrativo.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.TryGetValue("Trust Server Certificate", out var trust) && trust is true)
            throw new InvalidOperationException("POSTGRES_DEPLOY_URL no puede aceptar Trust Server Certificate para el bootstrap.");

        var permitsDevelopmentException = environment is { } host && (host.IsDevelopment() || host.IsEnvironment("Testing"));
        if (!permitsDevelopmentException && builder.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("POSTGRES_DEPLOY_URL debe forzar sslmode=verify-full fuera de Development/Testing.");

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }
}
