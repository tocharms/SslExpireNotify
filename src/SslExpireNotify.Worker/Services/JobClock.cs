using Microsoft.Extensions.Options;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// All date arithmetic goes through the job time zone, never through the OS time zone: a production box
/// left on UTC must still calculate "today" the way Bangkok sees it.
/// </summary>
public interface IJobClock
{
    DateTime Now { get; }
    DateTime Today { get; }
    TimeZoneInfo TimeZone { get; }
}

public sealed class JobClock : IJobClock
{
    public JobClock(IOptions<JobOptions> options)
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZoneId);
    }

    public TimeZoneInfo TimeZone { get; }

    public DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone).DateTime;

    public DateTime Today => Now.Date;
}
