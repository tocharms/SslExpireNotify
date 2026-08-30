using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class CompositeEmailSenderTests
{
    private static readonly EmailMessage Message = new()
    {
        To = "somchai.j@ksc.net",
        Cc = "boss@ksc.net",
        Subject = "subject",
        Body = "<html></html>"
    };

    private static CompositeEmailSender Build(
        FakeMailApiSender mailApi,
        FakeSmtpSender smtp,
        string preferredChannel = "Auto",
        bool fallbackEnabled = true) =>
        new(
            mailApi,
            smtp,
            Microsoft.Extensions.Options.Options.Create(new MailApiOptions { PreferredChannel = preferredChannel }),
            Microsoft.Extensions.Options.Options.Create(new SmtpFallbackOptions { Enabled = fallbackEnabled }),
            NullLogger<CompositeEmailSender>.Instance);

    [Fact]
    public async Task MailApiOnly_never_touches_the_fallback()
    {
        var mailApi = new FakeMailApiSender().Returns(
            EmailSendResult.Fail(EmailChannels.MailApi, "boom", SendFailureKind.Transient));
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp, "MailApiOnly").SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, mailApi.Calls);
        Assert.Equal(0, smtp.Calls);
    }

    [Fact]
    public async Task SmtpOnly_skips_the_mail_api_entirely()
    {
        var mailApi = new FakeMailApiSender();
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp, "SmtpOnly").SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, mailApi.Calls);
        Assert.Equal(1, smtp.Calls);
    }

    [Fact]
    public async Task Auto_uses_the_mail_api_while_it_works()
    {
        var mailApi = new FakeMailApiSender().Returns(EmailSendResult.Ok(EmailChannels.MailApi));
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp).SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(EmailChannels.MailApi, result.Channel);
        Assert.False(result.UsedFallback);
        Assert.Equal(0, smtp.Calls);
    }

    [Fact]
    public async Task A_4xx_does_not_fall_back_because_the_data_is_the_problem()
    {
        var mailApi = new FakeMailApiSender().Returns(
            EmailSendResult.Fail(EmailChannels.MailApi, "HTTP 400 bad address", SendFailureKind.Permanent));
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp).SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, smtp.Calls);
        Assert.Contains("400", result.ErrorMessage);
    }

    [Fact]
    public async Task An_exhausted_retry_budget_falls_back_to_smtp()
    {
        var mailApi = new FakeMailApiSender().Returns(
            EmailSendResult.Fail(EmailChannels.MailApi, "HTTP 503 after 3 retries", SendFailureKind.Transient, retryCount: 3));
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp).SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(EmailChannels.Smtp, result.Channel);
        Assert.True(result.UsedFallback);
        Assert.Equal(1, smtp.Calls);
    }

    [Fact]
    public async Task An_open_circuit_goes_straight_to_smtp_without_calling_the_mail_api()
    {
        var mailApi = new FakeMailApiSender { IsCircuitOpen = true };
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp).SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(EmailChannels.Smtp, result.Channel);
        Assert.Equal(0, mailApi.Calls);   // no wasted retries
        Assert.Equal(1, smtp.Calls);
    }

    [Fact]
    public async Task An_open_circuit_with_the_fallback_disabled_fails_immediately()
    {
        var mailApi = new FakeMailApiSender { IsCircuitOpen = true };
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp, fallbackEnabled: false).SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SendFailureKind.CircuitOpen, result.FailureKind);
        Assert.Equal(0, smtp.Calls);
    }

    [Fact]
    public async Task When_both_channels_fail_the_error_carries_both_messages()
    {
        var mailApi = new FakeMailApiSender().Returns(
            EmailSendResult.Fail(EmailChannels.MailApi, "mail api exploded", SendFailureKind.Transient));
        var smtp = new FakeSmtpSender().Returns(
            EmailSendResult.Fail(EmailChannels.Smtp, "smtp refused", SendFailureKind.Transient));

        var result = await Build(mailApi, smtp).SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(EmailChannels.Smtp, result.Channel);   // the last channel tried
        Assert.Contains("mail api exploded", result.ErrorMessage);
        Assert.Contains("smtp refused", result.ErrorMessage);
    }

    [Fact]
    public async Task A_transient_failure_with_the_fallback_disabled_keeps_the_mail_api_result()
    {
        var mailApi = new FakeMailApiSender().Returns(
            EmailSendResult.Fail(EmailChannels.MailApi, "HTTP 500", SendFailureKind.Transient));
        var smtp = new FakeSmtpSender();

        var result = await Build(mailApi, smtp, fallbackEnabled: false).SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(EmailChannels.MailApi, result.Channel);
        Assert.Equal(0, smtp.Calls);
    }
}
