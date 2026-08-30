using SslExpireNotify.Worker.Options;
using Xunit;

namespace SslExpireNotify.Tests;

public class OptionsValidationTests
{
    private static JobOptions ValidJob() => new()
    {
        CronSchedule = "0 30 0 * * ?",
        TimeZoneId = TimeZoneInfo.Local.Id,
        MisfirePolicy = "FireOnceNow",
        JobRunHistoryRetentionDays = 90,
        ContractThresholdDays = 199,
        AlertLevels = TestData.DefaultLevels()
    };

    [Fact]
    public void The_shipped_alert_level_table_is_valid()
    {
        Assert.Empty(JobOptionsValidator.ValidateAlertLevels(TestData.DefaultLevels()));
    }

    [Fact]
    public void An_empty_alert_level_table_is_rejected()
    {
        var errors = JobOptionsValidator.ValidateAlertLevels([]);

        Assert.Contains(errors, e => e.Contains("at least one level"));
    }

    [Fact]
    public void Duplicate_severities_are_rejected_because_the_match_would_be_ambiguous()
    {
        var levels = TestData.DefaultLevels();
        levels[1].Severity = levels[0].Severity;

        Assert.Contains(JobOptionsValidator.ValidateAlertLevels(levels), e => e.Contains("duplicate Severity"));
    }

    [Fact]
    public void Duplicate_level_names_are_rejected()
    {
        var levels = TestData.DefaultLevels();
        levels[1].Level = levels[0].Level;

        Assert.Contains(JobOptionsValidator.ValidateAlertLevels(levels), e => e.Contains("duplicate Level name"));
    }

    [Fact]
    public void A_severity_that_does_not_follow_the_days_ordering_is_rejected()
    {
        // URGENT is more severe than WARNING but would trigger earlier - a silent mis-classification.
        var levels = TestData.DefaultLevels();
        levels.Single(l => l.Level == "URGENT").Days = 20;

        var errors = JobOptionsValidator.ValidateAlertLevels(levels);

        Assert.Contains(errors, e => e.Contains("inconsistent"));
    }

    [Fact]
    public void A_repeat_interval_below_one_day_is_rejected()
    {
        var levels = TestData.DefaultLevels();
        levels[0].RepeatEveryDays = 0;

        Assert.Contains(JobOptionsValidator.ValidateAlertLevels(levels), e => e.Contains("RepeatEveryDays"));
    }

    [Fact]
    public void A_valid_job_section_passes_validation()
    {
        var result = new JobOptionsValidator().Validate(null, ValidJob());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void An_unknown_time_zone_stops_the_service_from_starting()
    {
        var options = ValidJob();
        options.TimeZoneId = "Mars Standard Time";

        var result = new JobOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("time zone"));
    }

    [Fact]
    public void An_unknown_misfire_policy_stops_the_service_from_starting()
    {
        var options = ValidJob();
        options.MisfirePolicy = "Whatever";

        Assert.True(new JobOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    public void An_unusable_mail_api_url_stops_the_service_from_starting(string url)
    {
        var result = new MailApiOptionsValidator().Validate(null, new MailApiOptions
        {
            Url = url,
            From = "noreplay@ksc.net",
            PreferredChannel = "Auto"
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void An_unknown_preferred_channel_stops_the_service_from_starting()
    {
        var result = new MailApiOptionsValidator().Validate(null, new MailApiOptions
        {
            Url = "https://mail.test/send",
            From = "noreplay@ksc.net",
            PreferredChannel = "Carrier Pigeon"
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("PreferredChannel"));
    }

    [Fact]
    public void An_empty_ack_base_url_stops_the_service_from_starting()
    {
        Assert.True(new AckOptionsValidator().Validate(null, new AckOptions { AckBaseUrl = "" }).Failed);
        Assert.True(new AckOptionsValidator().Validate(null, new AckOptions { AckBaseUrl = "https://app/ack" }).Succeeded);
    }

    [Fact]
    public void An_enabled_smtp_fallback_without_a_host_is_rejected()
    {
        var result = new SmtpFallbackOptionsValidator().Validate(null, new SmtpFallbackOptions
        {
            Enabled = true,
            Host = "",
            SenderEmail = "noreplay@ksc.net"
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_disabled_smtp_fallback_needs_no_settings_at_all()
    {
        var result = new SmtpFallbackOptionsValidator().Validate(null, new SmtpFallbackOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }
}
