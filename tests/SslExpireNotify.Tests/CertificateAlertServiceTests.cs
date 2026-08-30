using Microsoft.Extensions.Logging.Abstractions;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class CertificateAlertServiceTests
{
    private sealed record Harness(
        CertificateAlertService Service,
        FakeAlertRepository Alerts,
        FakeJobRunHistoryRepository History,
        RecordingEmailSender Sender,
        StubEmailComposer Composer);

    private static Harness Build(
        IReadOnlyList<SslCertificateRecord> certificates,
        bool dryRun = false,
        bool sendToCustomer = false,
        string cc = "",
        Action<FakeAlertRepository>? configureAlerts = null)
    {
        var alerts = new FakeAlertRepository();
        configureAlerts?.Invoke(alerts);

        var history = new FakeJobRunHistoryRepository();
        var sender = new RecordingEmailSender();
        var composer = new StubEmailComposer();

        var jobOptions = new JobOptions
        {
            DryRun = dryRun,
            AlertLevels = TestData.DefaultLevels(),
            ContractThresholdDays = 199,
            ActiveSslStatusValues = [1]
        };

        var service = new CertificateAlertService(
            new FakeCertificateRepository(certificates),
            alerts,
            history,
            composer,
            new EmailTemplateService(NullLogger<EmailTemplateService>.Instance, AppContext.BaseDirectory),
            sender,
            TestData.DefaultResolver(),
            new FakeClock(TestData.Today.AddMinutes(30)),
            Microsoft.Extensions.Options.Options.Create(jobOptions),
            Microsoft.Extensions.Options.Options.Create(new RecipientsOptions { SendToCustomer = sendToCustomer, Cc = cc }),
            NullLogger<CertificateAlertService>.Instance);

        return new Harness(service, alerts, history, sender, composer);
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_and_sends_nothing()
    {
        var harness = Build(
        [
            TestData.Certificate(id: 1, daysUntilExpiry: 25),
            TestData.Certificate(id: 2, daysUntilExpiry: -3)
        ], dryRun: true, sendToCustomer: true);

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(harness.Alerts.Inserted);
        Assert.Empty(harness.Alerts.Logs);
        Assert.Empty(harness.Alerts.Stamped);
        Assert.Empty(harness.Alerts.Incremented);
        Assert.Empty(harness.Alerts.Superseded);
        Assert.Equal(0, harness.Alerts.AutoResolveCalls);   // STEP 1 is an UPDATE, so it is skipped too
        Assert.Empty(harness.Sender.Sent);

        // The work was still planned and reported, which is the point of a dry run.
        Assert.Equal(2, summary.CertificatesScanned);
        Assert.Equal(2, summary.AlertsCreated);
    }

    [Fact]
    public async Task One_broken_certificate_does_not_stop_the_rest_of_the_run()
    {
        var harness = Build(
        [
            TestData.Certificate(id: 1, daysUntilExpiry: 25),
            TestData.Certificate(id: 2, daysUntilExpiry: 12),
            TestData.Certificate(id: 3, daysUntilExpiry: 4)
        ], configureAlerts: alerts => alerts.ThrowOnCertificateId = 2);

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(3, summary.CertificatesScanned);
        Assert.Equal(1, summary.CertificateErrors);
        Assert.Equal(2, summary.AlertsCreated);
        Assert.Equal([1, 3], harness.Alerts.Inserted.Select(a => a.CertificateId).OrderBy(id => id));
        Assert.Equal(2, harness.Sender.Sent.Count);
    }

    [Fact]
    public async Task A_new_alert_produces_one_mail_to_sales_and_a_success_log()
    {
        var harness = Build([TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "somchai.j@ksc.net")], cc: "boss@ksc.net");

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        var message = Assert.Single(harness.Sender.Sent);
        Assert.Equal("somchai.j@ksc.net", message.To);
        Assert.Equal("boss@ksc.net", message.Cc);

        var log = Assert.Single(harness.Alerts.Logs);
        Assert.Equal(EmailSendStatus.Success, log.SendStatus);
        Assert.Equal("somchai.j@ksc.net", log.RecipientEmail);

        Assert.Single(harness.Alerts.Stamped);        // LastNotifiedAt set
        Assert.Empty(harness.Alerts.Incremented);     // first send keeps NotifyCount = 1
        Assert.Equal(1, summary.EmailsSent);
    }

    [Fact]
    public async Task A_certificate_without_a_usable_sales_email_still_gets_its_alert()
    {
        var harness = Build([TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "   ")]);

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(harness.Alerts.Inserted);
        Assert.Empty(harness.Sender.Sent);

        var log = Assert.Single(harness.Alerts.Logs);
        Assert.Equal(EmailSendStatus.Failed, log.SendStatus);
        Assert.Null(log.RecipientEmail);
        Assert.Equal(RecipientResolver.NoRecipientError, log.ErrorMessage);

        // LastNotifiedAt stays null so the next run retries once the data is fixed.
        Assert.Empty(harness.Alerts.Stamped);
        Assert.Equal(1, summary.EmailsFailed);
    }

    [Fact]
    public async Task A_failed_send_leaves_the_alert_unstamped_so_the_next_run_retries()
    {
        var harness = Build([TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "somchai.j@ksc.net")]);
        harness.Sender.FailFor.Add("somchai.j@ksc.net");

        await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(harness.Alerts.Inserted);
        Assert.Empty(harness.Alerts.Stamped);
        Assert.Equal(EmailSendStatus.Failed, Assert.Single(harness.Alerts.Logs).SendStatus);
    }

    [Fact]
    public async Task Expired_certificates_of_one_sales_owner_are_sent_as_a_single_digest()
    {
        var harness = Build(
        [
            TestData.Certificate(id: 1, daysUntilExpiry: -3, salesId: 1001, salesEmail: "somchai.j@ksc.net"),
            TestData.Certificate(id: 2, daysUntilExpiry: -9, salesId: 1001, salesEmail: "somchai.j@ksc.net"),
            TestData.Certificate(id: 3, daysUntilExpiry: -1, salesId: 1002, salesEmail: "wanida.s@ksc.net")
        ]);

        await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, harness.Sender.Sent.Count);

        // One EmailLog row per alert, so a digest stays traceable per certificate.
        Assert.Equal(3, harness.Alerts.Logs.Count);
        Assert.Equal(3, harness.Alerts.Stamped.Count);
    }

    [Fact]
    public async Task The_customer_copy_is_a_separate_mail_and_only_when_the_flag_is_on()
    {
        var certificate = TestData.Certificate(id: 1, daysUntilExpiry: 25,
            salesEmail: "somchai.j@ksc.net", customerEmail: "it@example.com");

        var off = Build([certificate]);
        await off.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Single(off.Sender.Sent);

        var on = Build([certificate], sendToCustomer: true);
        await on.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, on.Sender.Sent.Count);
        Assert.Contains(on.Sender.Sent, m => m.To == "somchai.j@ksc.net");
        Assert.Contains(on.Sender.Sent, m => m.To == "it@example.com");
        Assert.Single(on.Composer.CustomerRenders);
    }

    [Fact]
    public async Task A_failing_customer_copy_does_not_affect_the_sales_mail()
    {
        var harness = Build(
            [TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "somchai.j@ksc.net", customerEmail: "it@example.com")],
            sendToCustomer: true);

        harness.Sender.FailFor.Add("it@example.com");

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, summary.EmailsSent);
        Assert.Equal(1, summary.EmailsFailed);

        var salesLog = harness.Alerts.Logs.Single(l => l.RecipientEmail == "somchai.j@ksc.net");
        var customerLog = harness.Alerts.Logs.Single(l => l.RecipientEmail == "it@example.com");

        Assert.Equal(EmailSendStatus.Success, salesLog.SendStatus);
        Assert.Equal(EmailSendStatus.Failed, customerLog.SendStatus);

        // The sales mail still paced the alert.
        Assert.Single(harness.Alerts.Stamped);
    }

    [Fact]
    public async Task A_missing_customer_address_is_logged_without_touching_the_sales_mail()
    {
        var harness = Build(
            [TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "somchai.j@ksc.net", customerEmail: null)],
            sendToCustomer: true);

        await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(harness.Sender.Sent);
        Assert.Equal(RecipientResolver.NoCustomerEmailError,
            harness.Alerts.Logs.Single(l => l.RecipientEmail is null).ErrorMessage);
        Assert.Equal(EmailSendStatus.Success,
            harness.Alerts.Logs.Single(l => l.RecipientEmail == "somchai.j@ksc.net").SendStatus);
    }

    [Fact]
    public async Task The_grouped_digest_is_never_sent_to_the_customer()
    {
        var harness = Build(
            [TestData.Certificate(id: 1, daysUntilExpiry: -3, customerEmail: "it@example.com")],
            sendToCustomer: true);

        await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(harness.Sender.Sent);
        Assert.Empty(harness.Composer.CustomerRenders);
    }

    [Fact]
    public async Task A_repeat_send_increments_the_notify_count()
    {
        var certificate = TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "somchai.j@ksc.net");

        var harness = Build([certificate], configureAlerts: alerts => alerts.Existing.Add(
            TestData.Alert(alertId: 77, certificateId: 1, level: "NOTICE",
                snapshot: certificate.SslExpiredDate!.Value.Date,
                lastNotifiedAt: TestData.Today.AddDays(-7))));

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(harness.Alerts.Inserted);
        Assert.Equal(1, summary.AlertsResent);
        Assert.Equal([77L], harness.Alerts.Incremented);
        Assert.Equal([77L], harness.Alerts.Stamped);
    }

    [Fact]
    public async Task Moving_up_a_level_supersedes_the_previous_one_and_mails_the_new_one()
    {
        var certificate = TestData.Certificate(id: 1, daysUntilExpiry: 12, salesEmail: "somchai.j@ksc.net");

        var harness = Build([certificate], configureAlerts: alerts => alerts.Existing.Add(
            TestData.Alert(alertId: 88, certificateId: 1, level: "NOTICE",
                snapshot: certificate.SslExpiredDate!.Value.Date,
                lastNotifiedAt: TestData.Today.AddDays(-2))));

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([88L], harness.Alerts.Superseded);
        Assert.Equal("WARNING", Assert.Single(harness.Alerts.Inserted).AlertLevel);
        Assert.Equal(1, summary.AlertsCreated);
    }

    [Fact]
    public async Task An_acknowledged_cycle_produces_no_mail_at_all()
    {
        var certificate = TestData.Certificate(id: 1, daysUntilExpiry: -3, salesEmail: "somchai.j@ksc.net");

        var harness = Build([certificate], configureAlerts: alerts => alerts.Existing.Add(
            TestData.Alert(alertId: 99, certificateId: 1, level: "EXPIRED", status: AlertStatus.Acknowledged,
                snapshot: certificate.SslExpiredDate!.Value.Date)));

        await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(harness.Sender.Sent);
        Assert.Empty(harness.Alerts.Inserted);
        Assert.Empty(harness.Alerts.Logs);
    }

    [Fact]
    public async Task Fallback_deliveries_are_counted_so_the_run_summary_can_flag_them()
    {
        var harness = Build([TestData.Certificate(id: 1, daysUntilExpiry: 25, salesEmail: "somchai.j@ksc.net")]);
        harness.Sender.UseFallback = true;

        var summary = await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, summary.EmailsSent);
        Assert.Equal(1, summary.EmailsSentViaFallback);
        Assert.Equal(EmailChannels.Smtp, Assert.Single(harness.Alerts.Logs).Channel);
    }

    [Fact]
    public async Task Old_run_history_is_purged_on_every_run()
    {
        var harness = Build([TestData.Certificate(id: 1, daysUntilExpiry: 25)]);

        await harness.Service.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(1, harness.History.PurgeCalls);
    }
}
