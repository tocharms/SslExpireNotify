using Microsoft.Extensions.Logging.Abstractions;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

/// <summary>Renders against the templates that actually ship with the service.</summary>
public class EmailComposerTests
{
    private static EmailComposer Composer()
    {
        var templates = new EmailTemplateService(NullLogger<EmailTemplateService>.Instance, AppContext.BaseDirectory);

        var options = new EmailTemplateOptions
        {
            TemplateFiles = new(StringComparer.OrdinalIgnoreCase)
            {
                ["NOTICE"] = "Templates/ssl-expiry-notice.html",
                ["WARNING"] = "Templates/ssl-expiry-warning.html",
                ["URGENT"] = "Templates/ssl-expiry-urgent.html",
                ["EXPIRED"] = "Templates/ssl-expiry-notice-expired.html"
            },
            ContractTemplateFile = "Templates/ssl-contact-notice-expired.html",
            CustomerTemplateFiles = new(StringComparer.OrdinalIgnoreCase)
            {
                ["NOTICE"] = "Templates/ssl-expiry-notice.html",
                ["WARNING"] = "Templates/ssl-expiry-warning.html",
                ["URGENT"] = "Templates/ssl-expiry-urgent.html",
                ["EXPIRED"] = "Templates/ssl-expiry-notice-expired-customer.html"
            },
            Subjects = new(StringComparer.OrdinalIgnoreCase)
            {
                ["NOTICE"] = "[notice] {domain} expires in {days} days",
                ["WARNING"] = "[warning] {domain} expires in {days} days",
                ["URGENT"] = "[urgent] {domain} expires in {days} days",
                ["EXPIRED_GROUP"] = "[expired] {certCount} certificates need action",
                ["CONTRACT"] = "[contract] {domain} expires in {days} days",
                ["CONTRACT_EXPIRED"] = "[contract expired] {domain} overdue {daysOverdue} days",
                ["CONTRACT_EXPIRED_REPEAT"] = "[contract expired #{notifyCount}] {domain} overdue {daysOverdue} days",
                ["CUSTOMER_EXPIRED"] = "[customer expired] {domain} expired on {expireDate}",
                ["CUSTOMER_EXPIRED_REPEAT"] = "[customer expired #{notifyCount}] {domain} overdue {daysOverdue} days"
            }
        };

        return new EmailComposer(
            templates,
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new AckOptions { AckBaseUrl = "https://ack.test/ack" }),
            new FakeClock(TestData.Today));
    }

    private static NotificationGroup Group(IReadOnlyList<PendingNotification> items, bool grouped) => new()
    {
        Items = items,
        SalesId = items[0].Certificate.SalesId,
        IsGrouped = grouped
    };

    [Fact]
    public void The_ack_link_always_uses_the_token_list_form()
    {
        var composer = Composer();
        var token = Guid.NewGuid();

        Assert.Equal($"https://ack.test/ack?tokens={token}", composer.BuildAckLink([token]));
    }

    [Fact]
    public void A_notice_mail_to_sales_is_addressed_to_the_sales_person()
    {
        var item = TestData.Pending(TestData.Certificate(daysUntilExpiry: 25), level: "NOTICE", daysRemaining: 25);

        var rendered = Composer().RenderForSales(Group([item], grouped: false));

        Assert.Equal("[notice] www.example.co.th expires in 25 days", rendered.Subject);
        Assert.Contains("เรียน คุณSomchai Jaidee", rendered.Body);
        Assert.Contains("Dear Somchai Jaidee,", rendered.Body);
        Assert.Contains($"https://ack.test/ack?tokens={item.Alert.AckToken}", rendered.Body);
        Assert.DoesNotContain("{greetingThai}", rendered.Body);
        Assert.DoesNotContain("{ackLink}", rendered.Body);
    }

    [Fact]
    public void The_same_notice_template_greets_a_customer_differently()
    {
        var item = TestData.Pending(TestData.Certificate(daysUntilExpiry: 25), level: "NOTICE", daysRemaining: 25);

        var rendered = Composer().RenderForCustomer(item);

        Assert.Contains("เรียน ท่านลูกค้า", rendered.Body);
        Assert.Contains("Dear Customer,", rendered.Body);
        Assert.DoesNotContain("Somchai", rendered.Body);
    }

    [Fact]
    public void The_expired_digest_lists_every_certificate_and_links_all_tokens()
    {
        var first = TestData.Pending(TestData.Certificate(id: 1, domain: "a.co.th"), daysRemaining: -3);
        var second = TestData.Pending(TestData.Certificate(id: 2, domain: "b.co.th"), daysRemaining: -10);

        var rendered = Composer().RenderForSales(Group([first, second], grouped: true));

        Assert.Equal("[expired] 2 certificates need action", rendered.Subject);
        Assert.Contains("a.co.th", rendered.Body);
        Assert.Contains("b.co.th", rendered.Body);
        Assert.Contains($"tokens={second.Alert.AckToken},{first.Alert.AckToken}", rendered.Body); // oldest expiry first
        Assert.DoesNotContain("{certRows}", rendered.Body);
        Assert.DoesNotContain("ROW TEMPLATE", rendered.Body);
    }

    [Fact]
    public void A_contract_renewal_uses_one_template_for_every_level()
    {
        var composer = Composer();

        var early = TestData.Pending(
            TestData.Certificate(daysUntilExpiry: 20),
            level: "NOTICE", notificationType: NotificationType.ContractRenewal, daysRemaining: 20);

        var expired = TestData.Pending(
            TestData.Certificate(daysUntilExpiry: -5),
            level: "EXPIRED", notificationType: NotificationType.ContractRenewal, daysRemaining: -5);

        var earlyMail = composer.RenderForSales(Group([early], grouped: false));
        var expiredMail = composer.RenderForSales(Group([expired], grouped: false));

        Assert.Equal("[contract] www.example.co.th expires in 20 days", earlyMail.Subject);
        Assert.Equal("[contract expired] www.example.co.th overdue 5 days", expiredMail.Subject);

        // Same file, but the button text follows the level.
        Assert.Contains("Acknowledged – In Progress", earlyMail.Body);
        Assert.Contains("Acknowledge – Stop Alerts", expiredMail.Body);
        Assert.DoesNotContain("{ackButtonLabel}", earlyMail.Body);
    }

    [Fact]
    public void The_contract_template_states_the_certificate_status_in_both_languages()
    {
        var expired = TestData.Pending(
            TestData.Certificate(daysUntilExpiry: -5),
            level: "EXPIRED", notificationType: NotificationType.ContractRenewal, daysRemaining: -5);

        var body = Composer().RenderForSales(Group([expired], grouped: false)).Body;

        Assert.Contains("ได้หมดอายุไปแล้วเมื่อวันที่", body);
        Assert.Contains("expired on", body);
        Assert.Contains("5 days ago", body);
        Assert.DoesNotContain("{certStatusLineThai}", body);
        Assert.DoesNotContain("{certStatusLineEn}", body);
    }

    [Fact]
    public void A_repeat_contract_mail_switches_subject_and_adds_the_notification_count_line()
    {
        var repeat = TestData.Pending(
            TestData.Certificate(daysUntilExpiry: -12),
            level: "EXPIRED", notificationType: NotificationType.ContractRenewal,
            isFirstSend: false, notifyCount: 2, daysRemaining: -12);

        var mail = Composer().RenderForSales(Group([repeat], grouped: false));

        // NotifyCount is incremented for the mail that is about to go out.
        Assert.Equal("[contract expired #3] www.example.co.th overdue 12 days", mail.Subject);
        Assert.Contains("นี่คือการแจ้งเตือนครั้งที่ 3", mail.Body);
        Assert.Contains("This is notification number 3", mail.Body);
    }

    [Fact]
    public void A_first_contract_mail_leaves_the_notification_count_line_empty()
    {
        var first = TestData.Pending(
            TestData.Certificate(daysUntilExpiry: -2),
            level: "EXPIRED", notificationType: NotificationType.ContractRenewal, daysRemaining: -2);

        var body = Composer().RenderForSales(Group([first], grouped: false)).Body;

        Assert.DoesNotContain("การแจ้งเตือนครั้งที่", body);
        Assert.DoesNotContain("notification number", body);
        Assert.DoesNotContain("{notifyCountLineThai}", body);
    }

    [Fact]
    public void The_customer_expired_mail_uses_its_own_template_and_subject()
    {
        var item = TestData.Pending(TestData.Certificate(daysUntilExpiry: -4), daysRemaining: -4);

        var mail = Composer().RenderForCustomer(item);

        Assert.StartsWith("[customer expired]", mail.Subject);
        Assert.Contains("4", mail.Body);              // days overdue
        Assert.DoesNotContain("{daysOverdue}", mail.Body);
    }

    [Fact]
    public void A_repeat_customer_expired_mail_uses_the_repeat_subject()
    {
        var item = TestData.Pending(
            TestData.Certificate(daysUntilExpiry: -9),
            isFirstSend: false, notifyCount: 4, daysRemaining: -9);

        var mail = Composer().RenderForCustomer(item);

        Assert.Equal("[customer expired #5] www.example.co.th overdue 9 days", mail.Subject);
    }

    [Fact]
    public void No_placeholder_is_left_unresolved_in_any_shipped_template()
    {
        var composer = Composer();

        var mails = new List<RenderedEmail>
        {
            composer.RenderForSales(Group([TestData.Pending(TestData.Certificate(daysUntilExpiry: 25), level: "NOTICE", daysRemaining: 25)], false)),
            composer.RenderForSales(Group([TestData.Pending(TestData.Certificate(daysUntilExpiry: 12), level: "WARNING", daysRemaining: 12)], false)),
            composer.RenderForSales(Group([TestData.Pending(TestData.Certificate(daysUntilExpiry: 4), level: "URGENT", daysRemaining: 4)], false)),
            composer.RenderForSales(Group([TestData.Pending(TestData.Certificate(daysUntilExpiry: -4), daysRemaining: -4)], true)),
            composer.RenderForCustomer(TestData.Pending(TestData.Certificate(daysUntilExpiry: -4), daysRemaining: -4)),
            composer.RenderForSales(Group([TestData.Pending(TestData.Certificate(daysUntilExpiry: -4), level: "EXPIRED", notificationType: NotificationType.ContractRenewal, daysRemaining: -4)], false))
        };

        foreach (var mail in mails)
        {
            var leftover = System.Text.RegularExpressions.Regex.Match(mail.Body, @"\{[a-zA-Z][a-zA-Z0-9]*\}");
            Assert.False(leftover.Success, $"'{leftover.Value}' was not replaced in the mail with subject '{mail.Subject}'.");
        }
    }
}
