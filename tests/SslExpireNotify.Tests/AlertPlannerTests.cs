using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class AlertPlannerTests
{
    private static readonly DateTime Today = TestData.Today;

    [Fact]
    public void A_certificate_with_no_alert_yet_creates_one()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: 25);

        var decision = TestData.Planner().Plan(certificate, [], Today);

        Assert.Equal(AlertDecisionKind.Create, decision.Kind);
        Assert.Equal("NOTICE", decision.Level!.Level);
        Assert.Equal(25, decision.DaysRemaining);
        Assert.Equal(NotificationType.CertRenewal, decision.NotificationType);
    }

    [Fact]
    public void A_certificate_outside_every_threshold_is_skipped()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: 120);

        var decision = TestData.Planner().Plan(certificate, [], Today);

        Assert.Equal(AlertDecisionKind.Skip, decision.Kind);
        Assert.Null(decision.Level);
    }

    [Fact]
    public void Moving_up_a_level_supersedes_the_lower_ones_without_waiting_for_an_acknowledgement()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: 12); // now WARNING
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 10, level: "NOTICE", status: AlertStatus.Pending, snapshot: snapshot),
            TestData.Alert(alertId: 11, level: "WARNING", status: AlertStatus.Pending, snapshot: snapshot, lastNotifiedAt: Today)
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal([10L], decision.SupersedeAlertIds);
        Assert.Equal("WARNING", decision.Level!.Level);
    }

    [Fact]
    public void A_noted_lower_level_alert_is_superseded_too()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: 5); // URGENT
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 20, level: "NOTICE", status: AlertStatus.Noted, snapshot: snapshot),
            TestData.Alert(alertId: 21, level: "WARNING", status: AlertStatus.Noted, snapshot: snapshot)
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Create, decision.Kind);
        Assert.Equal([20L, 21L], decision.SupersedeAlertIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void An_acknowledged_cycle_stops_the_certificate_completely()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: -4); // EXPIRED
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 30, level: "EXPIRED", status: AlertStatus.Acknowledged, snapshot: snapshot)
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Skip, decision.Kind);
        Assert.Empty(decision.SupersedeAlertIds);
    }

    [Fact]
    public void A_resolved_cycle_stops_the_certificate_completely()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: 3);
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 31, level: "URGENT", status: AlertStatus.Resolved, snapshot: snapshot)
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Skip, decision.Kind);
    }

    [Fact]
    public void A_noted_alert_of_the_current_level_still_gets_resent_when_it_is_due()
    {
        // Pressing "acknowledged" on NOTICE/WARNING/URGENT only records that the mail was seen.
        var certificate = TestData.Certificate(daysUntilExpiry: 25);
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 40, level: "NOTICE", status: AlertStatus.Noted, snapshot: snapshot,
                lastNotifiedAt: Today.AddDays(-7))
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Resend, decision.Kind);
        Assert.Equal(40, decision.ExistingAlert!.AlertId);
    }

    [Fact]
    public void An_alert_that_is_not_due_yet_is_skipped()
    {
        var certificate = TestData.Certificate(daysUntilExpiry: 25);
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 41, level: "NOTICE", status: AlertStatus.Pending, snapshot: snapshot,
                lastNotifiedAt: Today.AddDays(-2))
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Skip, decision.Kind);
        Assert.NotNull(decision.SkipReason);
    }

    [Fact]
    public void A_previous_cycle_does_not_block_a_new_expiry_date()
    {
        // The certificate was renewed, so the old cycle (different snapshot) is irrelevant.
        var certificate = TestData.Certificate(daysUntilExpiry: 20);

        var existing = new[]
        {
            TestData.Alert(alertId: 50, level: "EXPIRED", status: AlertStatus.Acknowledged,
                snapshot: Today.AddDays(-400))
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Create, decision.Kind);
        Assert.Equal("NOTICE", decision.Level!.Level);
    }

    [Fact]
    public void A_failed_first_send_is_retried_on_the_next_run()
    {
        // LastNotifiedAt stays NULL when no mail got out, so the alert is due again immediately.
        var certificate = TestData.Certificate(daysUntilExpiry: 25);
        var snapshot = certificate.SslExpiredDate!.Value.Date;

        var existing = new[]
        {
            TestData.Alert(alertId: 60, level: "NOTICE", status: AlertStatus.Pending, snapshot: snapshot, lastNotifiedAt: null)
        };

        var decision = TestData.Planner().Plan(certificate, existing, Today);

        Assert.Equal(AlertDecisionKind.Resend, decision.Kind);
    }
}
