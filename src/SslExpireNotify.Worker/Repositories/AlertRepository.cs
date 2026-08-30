using System.Data;
using Dapper;
using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Repositories;

public interface IAlertRepository
{
    /// <summary>STEP 1: closes every open alert whose certificate has been extended past the snapshot.</summary>
    Task<int> AutoResolveAsync(CancellationToken cancellationToken);

    /// <summary>Every alert (any status) that belongs to the given certificates.</summary>
    Task<IReadOnlyList<CertificateAlertRecord>> GetAlertsForCertificatesAsync(
        IReadOnlyCollection<int> certificateIds,
        CancellationToken cancellationToken);

    /// <summary>Inserts a new Pending alert and returns its identity.</summary>
    Task<long> InsertAlertAsync(CertificateAlertRecord alert, CancellationToken cancellationToken);

    /// <summary>Marks lower severity alerts of the same cycle as Superseded.</summary>
    Task<int> SupersedeAsync(IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the EmailLog rows and, for a delivered mail, stamps LastNotifiedAt / NotifyCount.
    /// Both happen in one transaction so an alert is never marked as notified without its audit row.
    /// </summary>
    Task RecordDeliveryAsync(
        IReadOnlyCollection<EmailLogRecord> logs,
        IReadOnlyCollection<long> alertIdsToStamp,
        IReadOnlyCollection<long> alertIdsToIncrement,
        DateTime notifiedAt,
        CancellationToken cancellationToken);
}

public sealed class AlertRepository : IAlertRepository
{
    private const int IdBatchSize = 1000;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IDbResilience _resilience;

    public AlertRepository(IDbConnectionFactory connectionFactory, IDbResilience resilience)
    {
        _connectionFactory = connectionFactory;
        _resilience = resilience;
    }

    public Task<int> AutoResolveAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE a
            SET a.AlertStatus   = 'Resolved',
                a.ResolvedAt    = SYSDATETIME(),
                a.NewExpireDate = CAST(c.SSLExpiredDate AS DATE)
            FROM CertificateAlert a
            JOIN SSL_Certificate c ON c.SSL_Cert_ID = a.CertificateId
            WHERE a.AlertStatus IN ('Pending','Noted','Acknowledged')
              AND CAST(c.SSLExpiredDate AS DATE) > a.ExpireDateSnapshot;
            """;

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            return await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<CertificateAlertRecord>> GetAlertsForCertificatesAsync(
        IReadOnlyCollection<int> certificateIds,
        CancellationToken cancellationToken)
    {
        if (certificateIds.Count == 0)
        {
            return [];
        }

        const string sql = """
            SELECT AlertId, CertificateId, AlertLevel, NotificationType, ExpireDateSnapshot, DaysRemaining,
                   AlertStatus, AckToken, AckTokenExpireAt, AcknowledgedAt, AcknowledgedBy, ResolvedAt,
                   NewExpireDate, LastNotifiedAt, NotifyCount, CreatedAt
            FROM CertificateAlert
            WHERE CertificateId IN @Ids
            """;

        var result = new List<CertificateAlertRecord>();

        foreach (var batch in certificateIds.Chunk(IdBatchSize))
        {
            var rows = await _resilience.ExecuteAsync(async ct =>
            {
                await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
                var command = new CommandDefinition(sql, new { Ids = batch }, cancellationToken: ct);
                return await connection.QueryAsync<CertificateAlertRecord>(command).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            result.AddRange(rows);
        }

        return result;
    }

    public Task<long> InsertAlertAsync(CertificateAlertRecord alert, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO CertificateAlert
                (CertificateId, AlertLevel, NotificationType, ExpireDateSnapshot, DaysRemaining,
                 AlertStatus, AckToken, AckTokenExpireAt, LastNotifiedAt, NotifyCount, CreatedAt)
            OUTPUT INSERTED.AlertId
            VALUES
                (@CertificateId, @AlertLevel, @NotificationType, @ExpireDateSnapshot, @DaysRemaining,
                 @AlertStatus, @AckToken, @AckTokenExpireAt, @LastNotifiedAt, @NotifyCount, @CreatedAt);
            """;

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            var command = new CommandDefinition(sql, alert, cancellationToken: ct);
            return await connection.ExecuteScalarAsync<long>(command).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<int> SupersedeAsync(IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken)
    {
        if (alertIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        const string sql = """
            UPDATE CertificateAlert
            SET AlertStatus = 'Superseded'
            WHERE AlertId IN @Ids
              AND AlertStatus IN ('Pending','Noted');
            """;

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            var command = new CommandDefinition(sql, new { Ids = alertIds }, cancellationToken: ct);
            return await connection.ExecuteAsync(command).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task RecordDeliveryAsync(
        IReadOnlyCollection<EmailLogRecord> logs,
        IReadOnlyCollection<long> alertIdsToStamp,
        IReadOnlyCollection<long> alertIdsToIncrement,
        DateTime notifiedAt,
        CancellationToken cancellationToken)
    {
        const string insertLogSql = """
            INSERT INTO EmailLog
                (AlertId, RecipientEmail, RecipientType, Subject, SendStatus, Channel, ErrorMessage, RetryCount, SentAt)
            VALUES
                (@AlertId, @RecipientEmail, @RecipientType, @Subject, @SendStatus, @Channel, @ErrorMessage, @RetryCount, @SentAt);
            """;

        const string stampSql = """
            UPDATE CertificateAlert
            SET LastNotifiedAt = @NotifiedAt
            WHERE AlertId IN @Ids;
            """;

        const string incrementSql = """
            UPDATE CertificateAlert
            SET NotifyCount = NotifyCount + 1
            WHERE AlertId IN @Ids;
            """;

        return _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

            try
            {
                if (logs.Count > 0)
                {
                    await connection.ExecuteAsync(new CommandDefinition(insertLogSql, logs, transaction, cancellationToken: ct))
                        .ConfigureAwait(false);
                }

                if (alertIdsToIncrement.Count > 0)
                {
                    await connection.ExecuteAsync(new CommandDefinition(incrementSql, new { Ids = alertIdsToIncrement }, transaction, cancellationToken: ct))
                        .ConfigureAwait(false);
                }

                if (alertIdsToStamp.Count > 0)
                {
                    await connection.ExecuteAsync(new CommandDefinition(stampSql, new { Ids = alertIdsToStamp, NotifiedAt = notifiedAt }, transaction, cancellationToken: ct))
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);
    }
}
