using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Models;

/// <summary>
/// An alert that STEP 3 decided has to be mailed in this run, together with everything STEP 4 needs
/// so it does not have to go back to the database.
/// </summary>
public sealed class PendingNotification
{
    public required CertificateAlertRecord Alert { get; init; }

    public required SslCertificateRecord Certificate { get; init; }

    public required AlertLevelOptions Level { get; init; }

    /// <summary>True when the alert row was created in this run (case A), false for a repeat send (case B).</summary>
    public required bool IsFirstSend { get; init; }

    /// <summary>Days remaining recomputed at send time.</summary>
    public required int DaysRemaining { get; init; }

    public bool IsExpiredLevel => WellKnownAlertLevels.IsExpired(Alert.AlertLevel);

    public bool IsContractRenewal =>
        string.Equals(Alert.NotificationType, NotificationType.ContractRenewal, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NotifyCount as it will be stored once this send is recorded: a repeat send increments it.
    /// Subjects use this value so "notification number N" matches what the recipient is holding.
    /// </summary>
    public int EffectiveNotifyCount => IsFirstSend ? Alert.NotifyCount : Alert.NotifyCount + 1;
}

/// <summary>A set of alerts delivered by one email (one item for per-certificate mails).</summary>
public sealed class NotificationGroup
{
    public required IReadOnlyList<PendingNotification> Items { get; init; }

    /// <summary>SalesID the group belongs to, null when the certificate has no sales owner.</summary>
    public decimal? SalesId { get; init; }

    /// <summary>True for the grouped EXPIRED digest sent to a sales person.</summary>
    public required bool IsGrouped { get; init; }

    public PendingNotification First => Items[0];
}
