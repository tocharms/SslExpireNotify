using Dapper;
using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Repositories;

public interface ISslCertificateRepository
{
    /// <summary>
    /// Certificates that have to be monitored, joined with their customer and sales owner.
    /// Read only: this application never writes to SSL_Certificate, CUSTOMER or KSC_USERS.
    /// </summary>
    Task<IReadOnlyList<SslCertificateRecord>> GetMonitoredCertificatesAsync(
        IReadOnlyCollection<int> activeStatusValues,
        CancellationToken cancellationToken);
}

public sealed class SslCertificateRepository : ISslCertificateRepository
{
    // EmailAlert is always selected, even when Recipients:SendToCustomer is off, so the feature can be
    // switched on in configuration alone.
    private const string BaseSql = """
        SELECT
            c.SSL_Cert_ID        AS SslCertId,
            c.CustomerId         AS CustomerId,
            c.CommonName         AS CommonName,
            c.DomainName         AS DomainName,
            c.OrderStartDate     AS OrderStartDate,
            c.OrderEndDate       AS OrderEndDate,
            c.SSLExpiredDate     AS SslExpiredDate,
            c.EmailAlert         AS EmailAlert,
            c.SalesID            AS SalesId,
            cu.DISPLAYNAME       AS CustomerDisplayName,
            cu.COMPANYNAME       AS CustomerCompanyName,
            u.EMAIL              AS SalesEmail,
            u.FIRST_NAME         AS SalesFirstName,
            u.LAST_NAME          AS SalesLastName
        FROM SSL_Certificate c
        LEFT JOIN CUSTOMER   cu ON cu.CUSTOMERID = c.CustomerId
        LEFT JOIN KSC_USERS  u  ON u.USERID      = c.SalesID
        WHERE c.SSLExpiredDate IS NOT NULL
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IDbResilience _resilience;

    public SslCertificateRepository(IDbConnectionFactory connectionFactory, IDbResilience resilience)
    {
        _connectionFactory = connectionFactory;
        _resilience = resilience;
    }

    public async Task<IReadOnlyList<SslCertificateRecord>> GetMonitoredCertificatesAsync(
        IReadOnlyCollection<int> activeStatusValues,
        CancellationToken cancellationToken)
    {
        // An empty ActiveSslStatusValues array means "do not filter on SSLStatus at all".
        var filterByStatus = activeStatusValues is { Count: > 0 };
        var sql = filterByStatus
            ? BaseSql + Environment.NewLine + "  AND c.SSLStatus IN @Statuses"
            : BaseSql;

        return await _resilience.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

            var command = new CommandDefinition(
                sql,
                filterByStatus ? new { Statuses = activeStatusValues.Select(v => (decimal)v).ToArray() } : null,
                cancellationToken: ct);

            var rows = await connection.QueryAsync<SslCertificateRecord>(command).ConfigureAwait(false);
            return (IReadOnlyList<SslCertificateRecord>)rows.ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
