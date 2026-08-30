namespace SslExpireNotify.Worker.Services;

public sealed record NotificationTypeDecision(string NotificationType, bool OrderEndDateMissing);

public static class NotificationTypeResolver
{
    /// <summary>
    /// Decides whether a certificate only needs a new certificate (CERT_RENEWAL) or whether the product
    /// contract itself has to be renewed first (CONTRACT_RENEWAL).
    ///
    /// Rule: SSLExpiredDate + ContractThresholdDays &lt; OrderEndDate  =&gt; the contract still has room for
    /// another certificate cycle, so a plain certificate renewal is enough. Otherwise the contract runs out
    /// before the next certificate would, and sales has to renew the contract.
    /// A missing OrderEndDate is treated as CERT_RENEWAL and flagged so the caller can log a warning.
    /// </summary>
    public static NotificationTypeDecision Resolve(DateTime sslExpiredDate, DateTime? orderEndDate, int contractThresholdDays)
    {
        if (orderEndDate is null)
        {
            return new NotificationTypeDecision(Models.NotificationType.CertRenewal, OrderEndDateMissing: true);
        }

        var projectedEnd = sslExpiredDate.Date.AddDays(contractThresholdDays);
        var type = projectedEnd < orderEndDate.Value.Date
            ? Models.NotificationType.CertRenewal
            : Models.NotificationType.ContractRenewal;

        return new NotificationTypeDecision(type, OrderEndDateMissing: false);
    }
}
