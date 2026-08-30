using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SslExpireNotify.Worker.Repositories;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// Second line of defence against overlapping runs. [DisallowConcurrentExecution] only covers one process;
/// two service instances (a botched upgrade, a manual copy on another box) need a lock in the database.
/// </summary>
public interface IJobLockService
{
    /// <summary>Returns null when another instance already holds the lock.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}

public sealed class JobLockService : IJobLockService
{
    public const string LockResourceName = "SslExpireNotifyJob";
    private const int LockTimeoutMilliseconds = 5000;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<JobLockService> _logger;

    public JobLockService(IDbConnectionFactory connectionFactory, ILogger<JobLockService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        SqlConnection? connection = null;

        try
        {
            connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

            var parameters = new DynamicParameters();
            parameters.Add("@Resource", LockResourceName);
            parameters.Add("@LockMode", "Exclusive");
            parameters.Add("@LockOwner", "Session");
            parameters.Add("@LockTimeout", LockTimeoutMilliseconds);
            parameters.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

            await connection.ExecuteAsync(new CommandDefinition(
                "sp_getapplock",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var result = parameters.Get<int>("@Result");

            // 0 = granted, 1 = granted after waiting; anything negative means we did not get it.
            if (result >= 0)
            {
                _logger.LogDebug("Acquired application lock {Resource} (result {Result})", LockResourceName, result);
                return new AppLockHandle(connection, _logger);
            }

            _logger.LogWarning(
                "Could not acquire application lock {Resource} (sp_getapplock returned {Result}); another instance is running this job.",
                LockResourceName, result);

            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private sealed class AppLockHandle : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly ILogger _logger;

        public AppLockHandle(SqlConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var parameters = new DynamicParameters();
                parameters.Add("@Resource", LockResourceName);
                parameters.Add("@LockOwner", "Session");

                await _connection.ExecuteAsync(new CommandDefinition(
                    "sp_releaseapplock",
                    parameters,
                    commandType: CommandType.StoredProcedure)).ConfigureAwait(false);

                _logger.LogDebug("Released application lock {Resource}", LockResourceName);
            }
            catch (Exception ex)
            {
                // Closing the connection releases a session scoped lock anyway.
                _logger.LogWarning(ex, "Failed to release application lock {Resource} explicitly", LockResourceName);
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
