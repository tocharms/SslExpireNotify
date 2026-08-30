using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class NotificationTypeResolverTests
{
    private const int Threshold = 199;
    private static readonly DateTime Expiry = new(2026, 06, 30);

    [Fact]
    public void A_contract_that_outlasts_the_next_certificate_cycle_is_a_plain_certificate_renewal()
    {
        // Expiry + 199 days = 2027-01-15, well before the contract ends.
        var decision = NotificationTypeResolver.Resolve(Expiry, new DateTime(2028, 01, 01), Threshold);

        Assert.Equal(NotificationType.CertRenewal, decision.NotificationType);
        Assert.False(decision.OrderEndDateMissing);
    }

    [Fact]
    public void A_contract_that_runs_out_first_needs_a_contract_renewal()
    {
        var decision = NotificationTypeResolver.Resolve(Expiry, new DateTime(2026, 08, 01), Threshold);

        Assert.Equal(NotificationType.ContractRenewal, decision.NotificationType);
    }

    [Fact]
    public void The_boundary_day_counts_as_a_contract_renewal()
    {
        // >= is a contract renewal: exactly 199 days later leaves no room for a new certificate.
        var decision = NotificationTypeResolver.Resolve(Expiry, Expiry.AddDays(Threshold), Threshold);

        Assert.Equal(NotificationType.ContractRenewal, decision.NotificationType);
    }

    [Fact]
    public void One_day_past_the_boundary_is_a_certificate_renewal()
    {
        var decision = NotificationTypeResolver.Resolve(Expiry, Expiry.AddDays(Threshold + 1), Threshold);

        Assert.Equal(NotificationType.CertRenewal, decision.NotificationType);
    }

    [Fact]
    public void A_missing_order_end_date_falls_back_to_a_certificate_renewal_and_is_flagged()
    {
        var decision = NotificationTypeResolver.Resolve(Expiry, null, Threshold);

        Assert.Equal(NotificationType.CertRenewal, decision.NotificationType);
        Assert.True(decision.OrderEndDateMissing);
    }

    [Fact]
    public void The_threshold_comes_from_configuration()
    {
        var contractEnd = Expiry.AddDays(100);

        Assert.Equal(NotificationType.CertRenewal, NotificationTypeResolver.Resolve(Expiry, contractEnd, 50).NotificationType);
        Assert.Equal(NotificationType.ContractRenewal, NotificationTypeResolver.Resolve(Expiry, contractEnd, 199).NotificationType);
    }

    [Fact]
    public void The_time_component_of_the_dates_is_ignored()
    {
        var expiryWithTime = new DateTime(2026, 06, 30, 23, 59, 00);
        var contractEnd = new DateTime(2027, 01, 15, 00, 00, 01); // same day as expiry + 199

        Assert.Equal(NotificationType.ContractRenewal,
            NotificationTypeResolver.Resolve(expiryWithTime, contractEnd, Threshold).NotificationType);
    }
}
