using Dapper;
using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Repositories;

public interface IJobRunHistoryRepository
{
    Task StartAsync(Guid runId, DateTime startedAt, bool isDryRun, CancellationToken cancellationToken);

    Task FinishAsync(Guid runId, DateTime finishedAt, string status, JobRunSummary summary, string? errorSummary, CancellationToken cancellationToken);

    /// <summary>Deletes run history older than the retention window; returns the number of rows removed.</summary>
    Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken);
}

public sealed class JobRunHistoryRepository : IJobRunHistoryRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IDbResilience _resilience;

    public JobRunHistoryRepository(IDbConnectionFactory connectionFactory, IDbResilience resilience)
    {
        _connectionFactory = connectionFactory;
        _resilience = resilience;
    }

    public Task StartAsync(Guid runId, DateTime startedAt, bool isDryRun, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO JobRunHistory (RunId, StartedAt, Status, IsDryRun)
            VALUES (@RunId, @StartedAt, 'Running', @IsDryRun);
            """;

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, new { RunId = runId, StartedAt = startedAt, IsDryRun = isDryRun }, cancellationToken: ct))
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task FinishAsync(Guid runId, DateTime finishedAt, string status, JobRunSummary summary, string? errorSummary, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE JobRunHistory
            SET FinishedAt            = @FinishedAt,
                Status                = @Status,
                CertificatesScanned   = @CertificatesScanned,
                AlertsCreated         = @AlertsCreated,
                EmailsSent            = @EmailsSent,
                EmailsFailed          = @EmailsFailed,
                EmailsSentViaFallback = @EmailsSentViaFallback,
                ErrorSummary          = @ErrorSummary
            WHERE RunId = @RunId;
            """;

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                RunId = runId,
                FinishedAt = finishedAt,
                Status = status,
                summary.CertificatesScanned,
                summary.AlertsCreated,
                summary.EmailsSent,
                summary.EmailsFailed,
                summary.EmailsSentViaFallback,
                ErrorSummary = errorSummary
            }, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM JobRunHistory WHERE StartedAt < @OlderThan;";

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            return await connection.ExecuteAsync(new CommandDefinition(sql, new { OlderThan = olderThan }, cancellationToken: ct))
                .ConfigureAwait(false);
        }, cancellationToken);
    }
}
