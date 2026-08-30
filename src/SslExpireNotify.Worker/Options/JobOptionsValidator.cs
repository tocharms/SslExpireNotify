using Microsoft.Extensions.Options;

namespace SslExpireNotify.Worker.Options;

/// <summary>
/// Fail fast on a broken Job section: a silently wrong AlertLevels table would make the whole job
/// produce plausible looking but wrong notifications.
/// </summary>
public sealed class JobOptionsValidator : IValidateOptions<JobOptions>
{
    public ValidateOptionsResult Validate(string? name, JobOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.CronSchedule))
        {
            errors.Add("Job:CronSchedule must not be empty (expected a Quartz cron expression, e.g. '0 30 0 * * ?').");
        }

        if (string.IsNullOrWhiteSpace(options.TimeZoneId))
        {
            errors.Add("Job:TimeZoneId must not be empty (e.g. 'SE Asia Standard Time').");
        }
        else
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                errors.Add($"Job:TimeZoneId '{options.TimeZoneId}' is not a time zone known to this machine.");
            }
        }

        if (!string.Equals(options.MisfirePolicy, "FireOnceNow", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.MisfirePolicy, "DoNothing", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Job:MisfirePolicy '{options.MisfirePolicy}' is invalid. Use 'FireOnceNow' or 'DoNothing'.");
        }

        if (options.JobRunHistoryRetentionDays < 1)
        {
            errors.Add("Job:JobRunHistoryRetentionDays must be at least 1.");
        }

        if (options.ContractThresholdDays < 0)
        {
            errors.Add("Job:ContractThresholdDays must not be negative.");
        }

        errors.AddRange(ValidateAlertLevels(options.AlertLevels));

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Validates the AlertLevels table on its own so the rules can be unit tested directly.
    /// </summary>
    public static List<string> ValidateAlertLevels(IReadOnlyList<AlertLevelOptions>? levels)
    {
        var errors = new List<string>();

        if (levels is null || levels.Count == 0)
        {
            errors.Add("Job:AlertLevels must contain at least one level.");
            return errors;
        }

        foreach (var level in levels)
        {
            if (string.IsNullOrWhiteSpace(level.Level))
            {
                errors.Add("Job:AlertLevels contains an entry with an empty 'Level' name.");
            }

            if (level.RepeatEveryDays < 1)
            {
                errors.Add($"Job:AlertLevels['{level.Level}'].RepeatEveryDays must be at least 1.");
            }
        }

        var duplicateNames = levels
            .Where(l => !string.IsNullOrWhiteSpace(l.Level))
            .GroupBy(l => l.Level, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicate in duplicateNames)
        {
            errors.Add($"Job:AlertLevels has duplicate Level name '{duplicate}'. Level names must be unique.");
        }

        var duplicateSeverities = levels
            .GroupBy(l => l.Severity)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicate in duplicateSeverities)
        {
            errors.Add($"Job:AlertLevels has duplicate Severity {duplicate}. Severity must be unique so the most severe matching level is unambiguous.");
        }

        var duplicateDays = levels
            .GroupBy(l => l.Days)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicate in duplicateDays)
        {
            errors.Add($"Job:AlertLevels has duplicate Days threshold {duplicate}. Two levels cannot share the same threshold.");
        }

        // Consistency: ordered by ascending severity the Days thresholds must strictly decrease,
        // i.e. the most severe level is also the one closest to (or past) the expiry date.
        var ordered = levels.OrderBy(l => l.Severity).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (current.Days >= previous.Days)
            {
                errors.Add(
                    $"Job:AlertLevels is inconsistent: '{current.Level}' (Severity {current.Severity}, Days {current.Days}) " +
                    $"is more severe than '{previous.Level}' (Severity {previous.Severity}, Days {previous.Days}) " +
                    "but does not have a smaller Days threshold. A higher Severity must always mean fewer remaining days.");
            }
        }

        return errors;
    }
}
