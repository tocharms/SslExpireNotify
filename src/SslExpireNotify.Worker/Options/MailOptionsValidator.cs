using Microsoft.Extensions.Options;

namespace SslExpireNotify.Worker.Options;

public sealed class MailApiOptionsValidator : IValidateOptions<MailApiOptions>
{
    public ValidateOptionsResult Validate(string? name, MailApiOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            errors.Add("MailApi:Url must not be empty.");
        }
        else if (!Uri.TryCreate(options.Url, UriKind.Absolute, out _))
        {
            errors.Add($"MailApi:Url '{options.Url}' is not an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(options.From))
        {
            errors.Add("MailApi:From must not be empty.");
        }

        if (options.TimeoutSeconds < 1)
        {
            errors.Add("MailApi:TimeoutSeconds must be at least 1.");
        }

        if (options.CircuitBreakerFailureThreshold < 2)
        {
            errors.Add("MailApi:CircuitBreakerFailureThreshold must be at least 2.");
        }

        if (options.CircuitBreakerBreakSeconds < 1)
        {
            errors.Add("MailApi:CircuitBreakerBreakSeconds must be at least 1.");
        }

        if (!Enum.TryParse<MailChannelPreference>(options.PreferredChannel, ignoreCase: true, out _))
        {
            errors.Add($"MailApi:PreferredChannel '{options.PreferredChannel}' is invalid. Use 'Auto', 'MailApiOnly' or 'SmtpOnly'.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class SmtpFallbackOptionsValidator : IValidateOptions<SmtpFallbackOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpFallbackOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            errors.Add("SmtpFallback:Host must not be empty when SmtpFallback:Enabled is true.");
        }

        if (options.Port is < 1 or > 65535)
        {
            errors.Add("SmtpFallback:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.SenderEmail))
        {
            errors.Add("SmtpFallback:SenderEmail must not be empty when SmtpFallback:Enabled is true.");
        }

        if (options.TimeoutSeconds < 1)
        {
            errors.Add("SmtpFallback:TimeoutSeconds must be at least 1.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class EmailTemplateOptionsValidator : IValidateOptions<EmailTemplateOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailTemplateOptions options)
    {
        var errors = new List<string>();

        if (options.TemplateFiles.Count == 0)
        {
            errors.Add("EmailTemplates:TemplateFiles must map every alert level to an HTML file.");
        }

        if (string.IsNullOrWhiteSpace(options.ContractTemplateFile))
        {
            errors.Add("EmailTemplates:ContractTemplateFile must not be empty.");
        }

        if (options.Subjects.Count == 0)
        {
            errors.Add("EmailTemplates:Subjects must not be empty.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class AckOptionsValidator : IValidateOptions<AckOptions>
{
    public ValidateOptionsResult Validate(string? name, AckOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AckBaseUrl))
        {
            return ValidateOptionsResult.Fail("AckBaseUrl must not be empty; the acknowledge button link is built from it.");
        }

        return Uri.TryCreate(options.AckBaseUrl, UriKind.Absolute, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"AckBaseUrl '{options.AckBaseUrl}' is not an absolute URL.");
    }
}
