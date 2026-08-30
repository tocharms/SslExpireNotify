using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SslExpireNotify.Worker.Repositories;

public interface IDbConnectionFactory
{
    /// <summary>Opens a new connection to the SslNotifyDb database.</summary>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);

    string ConnectionString { get; }
}

public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    public const string ConnectionStringName = "SslNotifyDb";

    public SqlConnectionFactory(IConfiguration configuration)
    {
        ConnectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is missing from configuration.");
    }

    public string ConnectionString { get; }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
