using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// Chooses the channel for each message according to MailApi:PreferredChannel and the health of the
/// Mail API. Switching to the fallback is always logged as a warning: a silent channel switch would hide
/// a broken Mail API for weeks.
/// </summary>
public sealed class CompositeEmailSender : ICompositeEmailSender
{
    private readonly IMailApiEmailSender _mailApi;
    private readonly ISmtpEmailSender _smtp;
    private readonly IOptions<MailApiOptions> _mailApiOptions;
    private readonly IOptions<SmtpFallbackOptions> _smtpOptions;
    private readonly ILogger<CompositeEmailSender> _logger;

    public CompositeEmailSender(
        IMailApiEmailSender mailApi,
        ISmtpEmailSender smtp,
        IOptions<MailApiOptions> mailApiOptions,
        IOptions<SmtpFallbackOptions> smtpOptions,
        ILogger<CompositeEmailSender> logger)
    {
        _mailApi = mailApi;
        _smtp = smtp;
        _mailApiOptions = mailApiOptions;
        _smtpOptions = smtpOptions;
        _logger = logger;
    }

    public string Channel => EmailChannels.MailApi;

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var preference = _mailApiOptions.Value.ResolvePreferredChannel();

        switch (preference)
        {
            case MailChannelPreference.SmtpOnly:
                return await _smtp.SendAsync(message, cancellationToken).ConfigureAwait(false);

            case MailChannelPreference.MailApiOnly:
                return await _mailApi.SendAsync(message, cancellationToken).ConfigureAwait(false);

            default:
                return await SendAutoAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<EmailSendResult> SendAutoAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var fallbackEnabled = _smtpOptions.Value.Enabled;

        // 1. Breaker already open: do not burn three retries on a channel known to be down.
        if (_mailApi.IsCircuitOpen)
        {
            if (!fallbackEnabled)
            {
                return EmailSendResult.Fail(
                    EmailChannels.MailApi,
                    "Mail API circuit breaker is open and the SMTP fallback is disabled.",
                    SendFailureKind.CircuitOpen);
            }

            _logger.LogWarning(
                "Mail API circuit breaker is open — switching to the SMTP fallback for {To} subject {Subject}",
                message.To, message.Subject);

            return await SendViaFallbackAsync(message, mailApiError: "Mail API circuit breaker is open.", cancellationToken)
                .ConfigureAwait(false);
        }

        // 2. Normal path.
        var apiResult = await _mailApi.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (apiResult.Success)
        {
            return apiResult;
        }

        // 4xx: bad data, not a broken channel. Another transport would be rejected the same way.
        if (apiResult.FailureKind == SendFailureKind.Permanent)
        {
            _logger.LogError(
                "Mail API rejected the message to {To} permanently, no fallback attempted: {Error}",
                message.To, apiResult.ErrorMessage);
            return apiResult;
        }

        if (!fallbackEnabled)
        {
            return apiResult;
        }

        var reason = apiResult.FailureKind == SendFailureKind.CircuitOpen
            ? "circuit open"
            : "retries exhausted";

        _logger.LogWarning(
            "Mail API unavailable ({Reason}) for {To} subject {Subject} — switching to the SMTP fallback. Mail API error: {Error}",
            reason, message.To, message.Subject, apiResult.ErrorMessage);

        return await SendViaFallbackAsync(message, apiResult.ErrorMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmailSendResult> SendViaFallbackAsync(EmailMessage message, string? mailApiError, CancellationToken cancellationToken)
    {
        var smtpResult = await _smtp.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (smtpResult.Success)
        {
            return new EmailSendResult
            {
                Success = true,
                Channel = EmailChannels.Smtp,
                RetryCount = smtpResult.RetryCount,
                UsedFallback = true
            };
        }

        // Both channels are down: keep both messages, the pair is what tells operations what happened.
        return new EmailSendResult
        {
            Success = false,
            Channel = EmailChannels.Smtp,
            RetryCount = smtpResult.RetryCount,
            FailureKind = SendFailureKind.Transient,
            ErrorMessage = $"MailApi: {mailApiError ?? "n/a"} | Smtp: {smtpResult.ErrorMessage ?? "n/a"}"
        };
    }
}
