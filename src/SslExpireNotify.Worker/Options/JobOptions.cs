namespace SslExpireNotify.Worker.Options;

/// <summary>Definition of one alert level. Everything about a level lives in configuration.</summary>
public sealed class AlertLevelOptions
{
    public string Level { get; set; } = string.Empty;

    /// <summary>Days-remaining threshold: the level matches when <c>daysRemaining &lt;= Days</c>.</summary>
    public int Days { get; set; }

    /// <summary>Higher value = more severe. Must be unique across levels.</summary>
    public int Severity { get; set; }

    /// <summary>Re-send frequency in days (7 = weekly, 1 = daily).</summary>
    public int RepeatEveryDays { get; set; } = 1;
}

public sealed class JobOptions
{
    public const string SectionName = "Job";

    public string CronSchedule { get; set; } = "0 30 0 * * ?";
    public string TimeZoneId { get; set; } = "SE Asia Standard Time";

    /// <summary>FireOnceNow | DoNothing</summary>
    public string MisfirePolicy { get; set; } = "FireOnceNow";

    public bool RunOnStartup { get; set; }
    public bool DryRun { get; set; }
    public int JobRunHistoryRetentionDays { get; set; } = 90;

    /// <summary>SSLStatus values treated as "must monitor". Empty array = do not filter on SSLStatus at all.</summary>
    public int[] ActiveSslStatusValues { get; set; } = [1];

    public int ContractThresholdDays { get; set; } = 199;

    public List<AlertLevelOptions> AlertLevels { get; set; } = [];

    /// <summary>Lifetime of the acknowledge token written to CertificateAlert.AckTokenExpireAt.</summary>
    public int AckTokenValidDays { get; set; } = 90;

    /// <summary>Ratio of per-certificate failures within a run above which a high level warning is logged.</summary>
    public double CertificateErrorWarningRatio { get; set; } = 0.5;
}
