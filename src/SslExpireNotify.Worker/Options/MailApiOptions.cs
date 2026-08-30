namespace SslExpireNotify.Worker.Options;

public enum MailChannelPreference
{
    Auto = 0,
    MailApiOnly = 1,
    SmtpOnly = 2
}

public sealed class MailApiOptions
{
    public const string SectionName = "MailApi";

    public string Url { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Skip TLS certificate validation for the Mail API endpoint. Never leave this on in production.</summary>
    public bool AllowInvalidCertificate { get; set; }

    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public int CircuitBreakerBreakSeconds { get; set; } = 300;

    /// <summary>Base delay of the exponential retry backoff, in seconds.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>Auto | MailApiOnly | SmtpOnly</summary>
    public string PreferredChannel { get; set; } = "Auto";

    public MailChannelPreference ResolvePreferredChannel() =>
        Enum.TryParse<MailChannelPreference>(PreferredChannel, ignoreCase: true, out var parsed)
            ? parsed
            : MailChannelPreference.Auto;
}
