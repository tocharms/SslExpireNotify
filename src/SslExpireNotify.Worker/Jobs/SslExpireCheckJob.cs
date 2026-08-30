using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Serilog.Context;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Repositories;
using SslExpireNotify.Worker.Services;

namespace SslExpireNotify.Worker.Jobs;

[DisallowConcurrentExecution]
public sealed class SslExpireCheckJob : IJob
{
    public static readonly JobKey Key = new("SslExpireCheckJob", "ssl-expire-notify");

    private readonly ICertificateAlertService _alertService;
    private readonly IJobLockService _jobLock;
    private readonly IJobRunHistoryRepository _history;
    private readonly IJobClock _clock;
    private readonly JobOptions _options;
    private readonly ILogger<SslExpireCheckJob> _logger;

    public SslExpireCheckJob(
        ICertificateAlertService alertService,
        IJobLockService jobLock,
        IJobRunHistoryRepository history,
        IJobClock clock,
        IOptions<JobOptions> options,
        ILogger<SslExpireCheckJob> logger)
    {
        _alertService = alertService;
        _jobLock = jobLock;
        _history = history;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var runId = Guid.NewGuid();
        var cancellationToken = context.CancellationToken;

        // Every log line of this run carries the RunId so a run can be followed end to end.
        using var runScope = LogContext.PushProperty("RunId", runId);

        await using var jobLock = await _jobLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            _logger.LogWarning("Another instance holds the job lock; this run is skipped entirely.");
            return;
        }

        var startedAt = _clock.Now;
        await _history.StartAsync(runId, startedAt, _options.DryRun, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "SSL expiry check started at {StartedAt:yyyy-MM-dd HH:mm:ss} ({TimeZone}), DryRun={DryRun}",
            startedAt, _clock.TimeZone.Id, _options.DryRun);

        JobRunSummary summary;

        try
        {
            summary = await _alertService.RunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSL expiry check failed and did not complete");

            await SafeFinishAsync(runId, JobRunStatus.Failed, new JobRunSummary(), ex.ToString(), cancellationToken)
                .ConfigureAwait(false);

            // Never swallow it: Quartz has to see the failure too.
            throw new JobExecutionException(ex, refireImmediately: false);
        }

        await SafeFinishAsync(runId, JobRunStatus.Completed, summary, null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "SSL expiry check completed: scanned {Scanned}, resolved {Resolved}, created {Created}, resent {Resent}, superseded {Superseded}, sent {Sent}, failed {Failed}, fallback {Fallback}",
            summary.CertificatesScanned, summary.AlertsResolved, summary.AlertsCreated, summary.AlertsResent,
            summary.AlertsSuperseded, summary.EmailsSent, summary.EmailsFailed, summary.EmailsSentViaFallback);
    }

    private async Task SafeFinishAsync(Guid runId, string status, JobRunSummary summary, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await _history.FinishAsync(runId, _clock.Now, status, summary, error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Losing the history row must not mask the original outcome.
            _logger.LogError(ex, "Could not update JobRunHistory for run {RunId}", runId);
        }
    }
}
