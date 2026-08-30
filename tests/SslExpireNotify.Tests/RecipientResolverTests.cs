using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class RecipientResolverTests
{
    private static NotificationGroup SingleGroup(SslCertificateRecord certificate, bool grouped = false) => new()
    {
        Items = [TestData.Pending(certificate, level: grouped ? "EXPIRED" : "NOTICE", daysRemaining: grouped ? -3 : 25)],
        SalesId = certificate.SalesId,
        IsGrouped = grouped
    };

    [Fact]
    public void Sales_is_the_To_address_and_the_configured_Cc_is_applied()
    {
        var group = SingleGroup(TestData.Certificate(salesEmail: "somchai.j@ksc.net"));

        var plan = RecipientResolver.Resolve(group, new RecipientsOptions { Cc = "boss@ksc.net, team@ksc.net" });

        Assert.Equal("somchai.j@ksc.net", plan.SalesTo);
        Assert.Equal("boss@ksc.net,team@ksc.net", plan.Cc);
        Assert.Null(plan.SalesError);
    }

    [Fact]
    public void An_empty_Cc_setting_produces_no_Cc()
    {
        var plan = RecipientResolver.Resolve(SingleGroup(TestData.Certificate()), new RecipientsOptions { Cc = "" });

        Assert.Equal(string.Empty, plan.Cc);
    }

    [Fact]
    public void Malformed_Cc_entries_are_dropped_and_the_good_ones_kept()
    {
        var plan = RecipientResolver.Resolve(
            SingleGroup(TestData.Certificate()),
            new RecipientsOptions { Cc = "boss@ksc.net, rubbish, ,team@ksc.net" });

        Assert.Equal("boss@ksc.net,team@ksc.net", plan.Cc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("two@addresses.com,another@x.com")]
    public void An_unusable_sales_email_is_reported_as_no_recipient(string? email)
    {
        var plan = RecipientResolver.Resolve(SingleGroup(TestData.Certificate(salesEmail: email)), new RecipientsOptions());

        Assert.Null(plan.SalesTo);
        Assert.Equal(RecipientResolver.NoRecipientError, plan.SalesError);
    }

    [Fact]
    public void No_customer_mail_is_prepared_while_the_flag_is_off()
    {
        var plan = RecipientResolver.Resolve(
            SingleGroup(TestData.Certificate(customerEmail: "it@example.com")),
            new RecipientsOptions { SendToCustomer = false });

        Assert.False(plan.CustomerMailRequested);
        Assert.Null(plan.CustomerTo);
    }

    [Fact]
    public void Turning_the_flag_on_adds_a_separate_customer_mail_with_the_same_Cc()
    {
        var plan = RecipientResolver.Resolve(
            SingleGroup(TestData.Certificate(customerEmail: "it@example.com")),
            new RecipientsOptions { SendToCustomer = true, Cc = "boss@ksc.net" });

        Assert.True(plan.CustomerMailRequested);
        Assert.Equal("it@example.com", plan.CustomerTo);
        Assert.Equal("boss@ksc.net", plan.Cc);
        Assert.Null(plan.CustomerError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad@@example")]
    public void A_broken_customer_address_only_fails_the_customer_copy(string? emailAlert)
    {
        var plan = RecipientResolver.Resolve(
            SingleGroup(TestData.Certificate(customerEmail: emailAlert, salesEmail: "somchai.j@ksc.net")),
            new RecipientsOptions { SendToCustomer = true });

        Assert.Null(plan.CustomerTo);
        Assert.Equal(RecipientResolver.NoCustomerEmailError, plan.CustomerError);

        // The sales mail is untouched.
        Assert.Equal("somchai.j@ksc.net", plan.SalesTo);
        Assert.Null(plan.SalesError);
    }

    [Fact]
    public void The_grouped_expired_digest_is_never_exploded_to_customers()
    {
        var group = SingleGroup(TestData.Certificate(customerEmail: "it@example.com"), grouped: true);

        var plan = RecipientResolver.Resolve(group, new RecipientsOptions { SendToCustomer = true });

        Assert.False(plan.CustomerMailRequested);
        Assert.Null(plan.CustomerTo);
    }
}
