namespace SslExpireNotify.Worker.Models;

/// <summary>One row of CertificateAlert.</summary>
public sealed class CertificateAlertRecord
{
    public long AlertId { get; set; }
    public int CertificateId { get; set; }
    public string AlertLevel { get; set; } = string.Empty;
    public string NotificationType { get; set; } = Models.NotificationType.CertRenewal;
    public DateTime ExpireDateSnapshot { get; set; }
    public int DaysRemaining { get; set; }
    public string AlertStatus { get; set; } = Models.AlertStatus.Pending;
    public Guid AckToken { get; set; }
    public DateTime? AckTokenExpireAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? NewExpireDate { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public int NotifyCount { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
}

/// <summary>One row of EmailLog.</summary>
public sealed class EmailLogRecord
{
    public long AlertId { get; set; }
    public string? RecipientEmail { get; set; }
    public string RecipientType { get; set; } = RecipientTypes.To;
    public string? Subject { get; set; }
    public string SendStatus { get; set; } = EmailSendStatus.Failed;
    public string Channel { get; set; } = EmailChannels.MailApi;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTime SentAt { get; set; }
}

/// <summary>Counters written back to JobRunHistory at the end of a run.</summary>
public sealed class JobRunSummary
{
    public int CertificatesScanned { get; set; }
    public int AlertsCreated { get; set; }
    public int EmailsSent { get; set; }
    public int EmailsFailed { get; set; }
    public int EmailsSentViaFallback { get; set; }
    public int AlertsResolved { get; set; }
    public int AlertsSuperseded { get; set; }
    public int AlertsResent { get; set; }
    public int CertificateErrors { get; set; }
}
