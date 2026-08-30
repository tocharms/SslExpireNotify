using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

public enum AlertDecisionKind
{
    /// <summary>Nothing to do for this certificate in this run.</summary>
    Skip = 0,

    /// <summary>No alert exists for the current level yet (case A).</summary>
    Create = 1,

    /// <summary>An alert exists and is due for another email (case B).</summary>
    Resend = 2
}

public sealed record AlertDecision
{
    public required AlertDecisionKind Kind { get; init; }
    public AlertLevelOptions? Level { get; init; }
    public string NotificationType { get; init; } = Models.NotificationType.CertRenewal;
    public DateTime ExpireDateSnapshot { get; init; }
    public int DaysRemaining { get; init; }

    /// <summary>Alerts of the same cycle at a lower severity that have to be marked Superseded first.</summary>
    public IReadOnlyList<long> SupersedeAlertIds { get; init; } = [];

    /// <summary>The alert to resend (case B).</summary>
    public CertificateAlertRecord? ExistingAlert { get; init; }

    /// <summary>True when OrderEndDate was null and the notification type had to be assumed.</summary>
    public bool OrderEndDateMissing { get; init; }

    public string? SkipReason { get; init; }
}

/// <summary>
/// Pure decision logic of STEP 2 and STEP 3: given a certificate and the alerts already stored for it,
/// what should this run do? Kept free of I/O so the rules can be unit tested exhaustively.
/// </summary>
public sealed class AlertPlanner
{
    private readonly IAlertLevelResolver _levels;
    private readonly int _contractThresholdDays;

    public AlertPlanner(IAlertLevelResolver levels, int contractThresholdDays)
    {
        _levels = levels;
        _contractThresholdDays = contractThresholdDays;
    }

    public AlertDecision Plan(
        SslCertificateRecord certificate,
        IReadOnlyCollection<CertificateAlertRecord> alertsForCertificate,
        DateTime today)
    {
        if (certificate.SslExpiredDate is null)
        {
            return Skip("SSLExpiredDate is null");
        }

        var snapshot = certificate.SslExpiredDate.Value.Date;
        var daysRemaining = (snapshot - today.Date).Days;

        var level = _levels.Resolve(daysRemaining);
        if (level is null)
        {
            return Skip($"{daysRemaining} days remaining is outside every configured alert level")
                with { ExpireDateSnapshot = snapshot, DaysRemaining = daysRemaining };
        }

        var typeDecision = NotificationTypeResolver.Resolve(snapshot, certificate.OrderEndDate, _contractThresholdDays);

        // The cycle is identified by the certificate plus the expiry date it had when the alert was raised.
        var cycle = alertsForCertificate
            .Where(a => a.ExpireDateSnapshot.Date == snapshot)
            .ToList();

        // 3.1 Someone already closed this cycle: stop touching the certificate entirely.
        if (cycle.Any(a => AlertStatus.StopsCycle.Contains(a.AlertStatus, StringComparer.OrdinalIgnoreCase)))
        {
            return Skip("the cycle is already Acknowledged or Resolved")
                with { ExpireDateSnapshot = snapshot, DaysRemaining = daysRemaining, Level = level, NotificationType = typeDecision.NotificationType };
        }

        // 3.2 Anything less severe in this cycle stops on its own, no acknowledgement needed.
        var supersedeIds = cycle
            .Where(a => AlertStatus.Sendable.Contains(a.AlertStatus, StringComparer.OrdinalIgnoreCase))
            .Where(a => _levels.SeverityOf(a.AlertLevel) < level.Severity)
            .Select(a => a.AlertId)
            .ToList();

        var existing = cycle.FirstOrDefault(a =>
            string.Equals(a.AlertLevel, level.Level, StringComparison.OrdinalIgnoreCase));

        var baseDecision = new AlertDecision
        {
            Kind = AlertDecisionKind.Skip,
            Level = level,
            NotificationType = typeDecision.NotificationType,
            ExpireDateSnapshot = snapshot,
            DaysRemaining = daysRemaining,
            SupersedeAlertIds = supersedeIds,
            OrderEndDateMissing = typeDecision.OrderEndDateMissing
        };

        // 3.3 Case A: first time this certificate reaches this level in this cycle.
        if (existing is null)
        {
            return baseDecision with { Kind = AlertDecisionKind.Create };
        }

        // 3.4 Case B: the alert exists, is it due again?
        if (ResendPolicy.ShouldResend(existing.AlertStatus, existing.LastNotifiedAt, level.RepeatEveryDays, today))
        {
            return baseDecision with { Kind = AlertDecisionKind.Resend, ExistingAlert = existing };
        }

        // 3.5 Case C.
        return baseDecision with
        {
            ExistingAlert = existing,
            SkipReason = $"alert {existing.AlertId} is not due again yet (status {existing.AlertStatus}, last notified {existing.LastNotifiedAt:yyyy-MM-dd})"
        };
    }

    private static AlertDecision Skip(string reason) => new()
    {
        Kind = AlertDecisionKind.Skip,
        SkipReason = reason
    };
}
