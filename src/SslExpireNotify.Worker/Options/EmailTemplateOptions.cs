namespace SslExpireNotify.Worker.Options;

public sealed class EmailTemplateOptions
{
    public const string SectionName = "EmailTemplates";

    /// <summary>AlertLevel -&gt; HTML file used for the email to Sales (CERT_RENEWAL).</summary>
    public Dictionary<string, string> TemplateFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Single file used for every level of CONTRACT_RENEWAL.</summary>
    public string ContractTemplateFile { get; set; } = string.Empty;

    /// <summary>AlertLevel -&gt; HTML file used for the email to the customer.</summary>
    public Dictionary<string, string> CustomerTemplateFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Subject key -&gt; subject template (level name, EXPIRED_GROUP, CONTRACT*, CUSTOMER_*).</summary>
    public Dictionary<string, string> Subjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
