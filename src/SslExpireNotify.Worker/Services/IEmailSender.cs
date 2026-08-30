using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Services;

public interface IEmailSender
{
    /// <summary>Channel name written to EmailLog.Channel.</summary>
    string Channel { get; }

    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The primary channel: the KSC Mail API over HTTP.</summary>
public interface IMailApiEmailSender : IEmailSender
{
    /// <summary>True while the Mail API circuit breaker is open, i.e. calling it would only waste time.</summary>
    bool IsCircuitOpen { get; }
}

/// <summary>The fallback channel: a plain SMTP relay via MailKit.</summary>
public interface ISmtpEmailSender : IEmailSender
{
}

/// <summary>Decides between the two channels; this is what the job actually calls.</summary>
public interface ICompositeEmailSender : IEmailSender
{
}
