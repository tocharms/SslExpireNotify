namespace SslExpireNotify.Worker.Models;

/// <summary>
/// Alert status stored in CertificateAlert.AlertStatus. Kept as string constants because the level
/// vocabulary itself is configuration driven; only the status vocabulary is fixed by the CHECK constraint.
/// </summary>
public static class AlertStatus
{
    public const string Pending = "Pending";
    public const string Noted = "Noted";
    public const string Acknowledged = "Acknowledged";
    public const string Resolved = "Resolved";
    public const string Superseded = "Superseded";

    /// <summary>Statuses that mean "this cycle is closed, stop notifying the certificate entirely".</summary>
    public static readonly string[] StopsCycle = [Acknowledged, Resolved];

    /// <summary>Statuses that are still eligible for a (re)send.</summary>
    public static readonly string[] Sendable = [Pending, Noted];
}

public static class NotificationType
{
    /// <summary>Normal certificate renewal.</summary>
    public const string CertRenewal = "CERT_RENEWAL";

    /// <summary>The product contract itself has to be renewed before a new certificate can be issued.</summary>
    public const string ContractRenewal = "CONTRACT_RENEWAL";
}

public static class EmailSendStatus
{
    public const string Success = "Success";
    public const string Failed = "Failed";
}

public static class EmailChannels
{
    public const string MailApi = "MailApi";
    public const string Smtp = "Smtp";
}

public static class RecipientTypes
{
    public const string To = "To";
    public const string Cc = "Cc";
}

public static class JobRunStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>Well known level name. Only EXPIRED needs special handling in code.</summary>
public static class WellKnownAlertLevels
{
    public const string Expired = "EXPIRED";

    public static bool IsExpired(string? level) =>
        string.Equals(level, Expired, StringComparison.OrdinalIgnoreCase);
}
