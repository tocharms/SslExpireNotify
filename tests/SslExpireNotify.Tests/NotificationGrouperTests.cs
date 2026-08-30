using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class NotificationGrouperTests
{
    [Fact]
    public void Expired_certificate_renewals_are_digested_per_sales_owner()
    {
        var items = new[]
        {
            TestData.Pending(TestData.Certificate(id: 1, salesId: 1001, domain: "a.co.th"), daysRemaining: -3),
            TestData.Pending(TestData.Certificate(id: 2, salesId: 1001, domain: "b.co.th"), daysRemaining: -10),
            TestData.Pending(TestData.Certificate(id: 3, salesId: 1002, domain: "c.co.th"), daysRemaining: -1)
        };

        var groups = NotificationGrouper.Group(items);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.True(g.IsGrouped));

        var somchai = groups.Single(g => g.SalesId == 1001);
        Assert.Equal(2, somchai.Items.Count);

        // Oldest expiry first inside the mail.
        Assert.Equal("b.co.th", somchai.Items[0].Certificate.Domain);
    }

    [Fact]
    public void A_single_expired_certificate_still_uses_the_list_mail()
    {
        var items = new[] { TestData.Pending(TestData.Certificate(id: 1, salesId: 1001), daysRemaining: -2) };

        var group = Assert.Single(NotificationGrouper.Group(items));

        Assert.True(group.IsGrouped);
        Assert.Single(group.Items);
    }

    [Fact]
    public void Expired_certificates_without_a_sales_owner_fall_back_to_one_mail_each()
    {
        var items = new[]
        {
            TestData.Pending(TestData.Certificate(id: 1, salesId: null, salesEmail: null, domain: "a.co.th"), daysRemaining: -3),
            TestData.Pending(TestData.Certificate(id: 2, salesId: null, salesEmail: null, domain: "b.co.th"), daysRemaining: -4)
        };

        var groups = NotificationGrouper.Group(items);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Items));
        Assert.All(groups, g => Assert.Null(g.SalesId));
    }

    [Theory]
    [InlineData("NOTICE")]
    [InlineData("WARNING")]
    [InlineData("URGENT")]
    public void Levels_before_expiry_stay_one_mail_per_certificate(string level)
    {
        var items = new[]
        {
            TestData.Pending(TestData.Certificate(id: 1, salesId: 1001), level: level, daysRemaining: 5),
            TestData.Pending(TestData.Certificate(id: 2, salesId: 1001), level: level, daysRemaining: 5)
        };

        var groups = NotificationGrouper.Group(items);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsGrouped));
    }

    [Fact]
    public void Contract_renewals_are_never_grouped_even_when_expired()
    {
        var items = new[]
        {
            TestData.Pending(TestData.Certificate(id: 1, salesId: 1001), notificationType: NotificationType.ContractRenewal, daysRemaining: -3),
            TestData.Pending(TestData.Certificate(id: 2, salesId: 1001), notificationType: NotificationType.ContractRenewal, daysRemaining: -3)
        };

        var groups = NotificationGrouper.Group(items);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsGrouped));
    }

    [Fact]
    public void Digests_and_per_certificate_mails_can_come_out_of_the_same_run()
    {
        var items = new[]
        {
            TestData.Pending(TestData.Certificate(id: 1, salesId: 1001), level: "URGENT", daysRemaining: 3),
            TestData.Pending(TestData.Certificate(id: 2, salesId: 1001), daysRemaining: -3),
            TestData.Pending(TestData.Certificate(id: 3, salesId: 1001), daysRemaining: -5)
        };

        var groups = NotificationGrouper.Group(items);

        Assert.Equal(2, groups.Count);
        Assert.Single(groups, g => !g.IsGrouped);
        Assert.Equal(2, groups.Single(g => g.IsGrouped).Items.Count);
    }
}
