using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// Controls how often an alert of a given level is mailed again. The job runs daily, so this comparison
/// (not the cron expression) is what actually paces the notifications.
/// </summary>
public static class ResendPolicy
{
    /// <summary>
    /// True when an existing alert is due for another email.
    /// Noted still resends: it only records that the recipient saw the mail, it does not stop the alerts.
    /// </summary>
    public static bool ShouldResend(string alertStatus, DateTime? lastNotifiedAt, int repeatEveryDays, DateTime today)
    {
        if (!AlertStatus.Sendable.Contains(alertStatus, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (lastNotifiedAt is null)
        {
            // The previous attempt never reached anyone, try again on this run.
            return true;
        }

        var dueOnOrBefore = today.Date.AddDays(-Math.Max(1, repeatEveryDays));
        return lastNotifiedAt.Value.Date <= dueOnOrBefore;
    }
}
