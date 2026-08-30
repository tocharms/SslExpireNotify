namespace SslExpireNotify.Worker.Options;

public sealed class RecipientsOptions
{
    public const string SectionName = "Recipients";

    /// <summary>Cc applied to every email in the system. Comma separated, empty = no Cc.</summary>
    public string Cc { get; set; } = string.Empty;

    /// <summary>When true a separate email is also sent to SSL_Certificate.EmailAlert (per certificate only).</summary>
    public bool SendToCustomer { get; set; }
}
