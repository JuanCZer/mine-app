using Npgsql;
using Notes.Api.Database;

namespace Notes.Api.IdentityAdministration;

public sealed class IdentityAdminUnavailableException(Exception? inner = null)
    : InvalidOperationException("El servicio administrativo de identidad no está disponible.", inner);

public sealed class IdentityAdminConnection(PostgresDataSources dataSources)
{
    public async Task<NpgsqlConnection> OpenVerifiedAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        try
        {
            connection = await dataSources.IdentityAdmin.OpenConnectionAsync(cancellationToken);
            await using (var setRole = new NpgsqlCommand("SET ROLE gds_identity_admin", connection))
                await setRole.ExecuteNonQueryAsync(cancellationToken);
            await using var verify = new NpgsqlCommand("SELECT current_role::text", connection);
            var currentRole = (string?)await verify.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(currentRole, "gds_identity_admin", StringComparison.Ordinal))
                throw new IdentityAdminUnavailableException();
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (connection is not null) await connection.DisposeAsync();
            throw;
        }
        catch (Exception exception)
        {
            if (connection is not null) await connection.DisposeAsync();
            throw new IdentityAdminUnavailableException(exception);
        }
    }
}
