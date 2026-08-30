namespace SslExpireNotify.Worker.Options;

public sealed class SmtpFallbackOptions
{
    public const string SectionName = "SmtpFallback";

    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Retry attempts for the fallback channel (lighter than the Mail API policy on purpose).</summary>
    public int RetryCount { get; set; } = 2;
}
