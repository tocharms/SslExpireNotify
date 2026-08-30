using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class ResendPolicyTests
{
    private static readonly DateTime Today = TestData.Today;

    [Fact]
    public void Never_notified_alerts_are_always_due()
    {
        Assert.True(ResendPolicy.ShouldResend(AlertStatus.Pending, lastNotifiedAt: null, repeatEveryDays: 7, Today));
    }

    [Theory]
    [InlineData(6, false)]  // weekly cadence, six days ago is too soon
    [InlineData(7, true)]
    [InlineData(9, true)]
    public void Weekly_levels_wait_seven_days(int daysAgo, bool expected)
    {
        var result = ResendPolicy.ShouldResend(AlertStatus.Pending, Today.AddDays(-daysAgo), repeatEveryDays: 7, Today);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, false)] // already sent today
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Daily_levels_resend_the_next_day(int daysAgo, bool expected)
    {
        var result = ResendPolicy.ShouldResend(AlertStatus.Pending, Today.AddDays(-daysAgo), repeatEveryDays: 1, Today);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Noted_keeps_resending_because_it_only_records_that_someone_saw_the_mail()
    {
        Assert.True(ResendPolicy.ShouldResend(AlertStatus.Noted, Today.AddDays(-7), repeatEveryDays: 7, Today));
    }

    [Theory]
    [InlineData(AlertStatus.Acknowledged)]
    [InlineData(AlertStatus.Resolved)]
    [InlineData(AlertStatus.Superseded)]
    public void Closed_alerts_are_never_resent(string status)
    {
        Assert.False(ResendPolicy.ShouldResend(status, Today.AddDays(-30), repeatEveryDays: 1, Today));
    }

    [Fact]
    public void The_time_of_day_of_the_previous_send_does_not_matter()
    {
        // The job runs at 00:30; a send stamped late in the evening seven days ago is still due.
        var lastNotified = Today.AddDays(-7).AddHours(23).AddMinutes(59);

        Assert.True(ResendPolicy.ShouldResend(AlertStatus.Pending, lastNotified, repeatEveryDays: 7, Today));
    }
}
