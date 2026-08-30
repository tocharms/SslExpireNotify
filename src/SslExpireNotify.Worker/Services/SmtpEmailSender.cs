using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

/// <summary>Fallback channel used when the Mail API is unusable. Same message, different transport.</summary>
public sealed class SmtpEmailSender : ISmtpEmailSender
{
    private readonly SmtpFallbackOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpFallbackOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Channel => EmailChannels.Smtp;

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _options.RetryCount + 1);
        string? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await SendOnceAsync(message, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "SMTP fallback delivered message to {To} (cc {Cc}) subject {Subject} on attempt {Attempt}",
                    message.To, string.IsNullOrEmpty(message.Cc) ? "-" : message.Cc, message.Subject, attempt);

                return EmailSendResult.Ok(Channel, attempt - 1, usedFallback: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "SMTP fallback attempt {Attempt}/{Attempts} failed for {To}", attempt, attempts, message.To);

                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return EmailSendResult.Fail(
            Channel,
            $"SMTP fallback failed after {attempts} attempts: {lastError}",
            SendFailureKind.Transient,
            attempts - 1);
    }

    private async Task SendOnceAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.SenderName, _options.SenderEmail));
        mime.To.Add(MailboxAddress.Parse(message.To));

        foreach (var cc in EmailAddressHelper.SplitValid(message.Cc))
        {
            mime.Cc.Add(MailboxAddress.Parse(cc));
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.IsHtml ? message.Body : null,
            TextBody = message.IsHtml ? null : message.Body
        }.ToMessageBody();

        using var client = new SmtpClient
        {
            Timeout = _options.TimeoutSeconds * 1000
        };

        var socketOptions = _options.UseStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }
}
