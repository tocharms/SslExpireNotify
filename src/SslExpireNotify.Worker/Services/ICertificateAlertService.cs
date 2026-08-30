using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Services;

public interface ICertificateAlertService
{
    /// <summary>Runs STEP 1 to STEP 4 of one job cycle and returns the counters for JobRunHistory.</summary>
    Task<JobRunSummary> RunAsync(Guid runId, CancellationToken cancellationToken);
}
