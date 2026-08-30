using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Repositories;
using SslExpireNotify.Worker.Services;

namespace SslExpireNotify.Tests;

internal sealed class FakeClock(DateTime now) : IJobClock
{
    public DateTime Now { get; } = now;
    public DateTime Today => Now.Date;
    public TimeZoneInfo TimeZone { get; } = TimeZoneInfo.Utc;
}

internal sealed class FakeCertificateRepository(IReadOnlyList<SslCertificateRecord> certificates) : ISslCertificateRepository
{
    public IReadOnlyCollection<int>? RequestedStatuses { get; private set; }

    public Task<IReadOnlyList<SslCertificateRecord>> GetMonitoredCertificatesAsync(
        IReadOnlyCollection<int> activeStatusValues,
        CancellationToken cancellationToken)
    {
        RequestedStatuses = activeStatusValues;
        return Task.FromResult(certificates);
    }
}

internal sealed class FakeAlertRepository : IAlertRepository
{
    private long _nextId = 1;

    public List<CertificateAlertRecord> Existing { get; } = [];
    public List<CertificateAlertRecord> Inserted { get; } = [];
    public List<EmailLogRecord> Logs { get; } = [];
    public List<long> Superseded { get; } = [];
    public List<long> Stamped { get; } = [];
    public List<long> Incremented { get; } = [];
    public int AutoResolveCalls { get; private set; }

    /// <summary>Certificate id that makes InsertAlertAsync blow up, to prove one bad row is isolated.</summary>
    public int? ThrowOnCertificateId { get; set; }

    public Task<int> AutoResolveAsync(CancellationToken cancellationToken)
    {
        AutoResolveCalls++;
        return Task.FromResult(0);
    }

    public Task<IReadOnlyList<CertificateAlertRecord>> GetAlertsForCertificatesAsync(
        IReadOnlyCollection<int> certificateIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CertificateAlertRecord>>(
            Existing.Where(a => certificateIds.Contains(a.CertificateId)).ToList());

    public Task<long> InsertAlertAsync(CertificateAlertRecord alert, CancellationToken cancellationToken)
    {
        if (ThrowOnCertificateId == alert.CertificateId)
        {
            throw new InvalidOperationException($"Simulated database failure for certificate {alert.CertificateId}.");
        }

        alert.AlertId = _nextId++;
        Inserted.Add(alert);
        return Task.FromResult(alert.AlertId);
    }

    public Task<int> SupersedeAsync(IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken)
    {
        Superseded.AddRange(alertIds);
        return Task.FromResult(alertIds.Count);
    }

    public Task RecordDeliveryAsync(
        IReadOnlyCollection<EmailLogRecord> logs,
        IReadOnlyCollection<long> alertIdsToStamp,
        IReadOnlyCollection<long> alertIdsToIncrement,
        DateTime notifiedAt,
        CancellationToken cancellationToken)
    {
        Logs.AddRange(logs);
        Stamped.AddRange(alertIdsToStamp);
        Incremented.AddRange(alertIdsToIncrement);
        return Task.CompletedTask;
    }
}

internal sealed class FakeJobRunHistoryRepository : IJobRunHistoryRepository
{
    public List<Guid> Started { get; } = [];
    public List<(Guid RunId, string Status)> Finished { get; } = [];
    public int PurgeCalls { get; private set; }

    public Task StartAsync(Guid runId, DateTime startedAt, bool isDryRun, CancellationToken cancellationToken)
    {
        Started.Add(runId);
        return Task.CompletedTask;
    }

    public Task FinishAsync(Guid runId, DateTime finishedAt, string status, JobRunSummary summary, string? errorSummary, CancellationToken cancellationToken)
    {
        Finished.Add((runId, status));
        return Task.CompletedTask;
    }

    public Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken)
    {
        PurgeCalls++;
        return Task.FromResult(0);
    }
}

internal sealed class RecordingEmailSender : ICompositeEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    /// <summary>Addresses whose delivery should fail, to prove the mails are independent.</summary>
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool UseFallback { get; set; }

    public string Channel => EmailChannels.MailApi;

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);

        if (FailFor.Contains(message.To))
        {
            return Task.FromResult(EmailSendResult.Fail(EmailChannels.MailApi, "simulated failure", SendFailureKind.Transient));
        }

        return Task.FromResult(UseFallback
            ? EmailSendResult.Ok(EmailChannels.Smtp, usedFallback: true)
            : EmailSendResult.Ok(EmailChannels.MailApi));
    }
}

internal sealed class StubEmailComposer : IEmailComposer
{
    public List<NotificationGroup> SalesRenders { get; } = [];
    public List<PendingNotification> CustomerRenders { get; } = [];

    public RenderedEmail RenderForSales(NotificationGroup group)
    {
        SalesRenders.Add(group);
        return new RenderedEmail($"sales subject for {group.Items.Count} cert(s)", "<html>sales</html>");
    }

    public RenderedEmail RenderForCustomer(PendingNotification item)
    {
        CustomerRenders.Add(item);
        return new RenderedEmail($"customer subject for {item.Certificate.Domain}", "<html>customer</html>");
    }

    public string BuildAckLink(IEnumerable<Guid> tokens) =>
        "https://ack.test/ack?tokens=" + string.Join(',', tokens);
}

internal sealed class FakeMailApiSender : IMailApiEmailSender
{
    private readonly Queue<EmailSendResult> _results = new();

    public int Calls { get; private set; }
    public bool IsCircuitOpen { get; set; }
    public string Channel => EmailChannels.MailApi;

    public FakeMailApiSender Returns(params EmailSendResult[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }

        return this;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_results.Count > 0
            ? _results.Dequeue()
            : EmailSendResult.Ok(EmailChannels.MailApi));
    }
}

internal sealed class FakeSmtpSender : ISmtpEmailSender
{
    private EmailSendResult _result = EmailSendResult.Ok(EmailChannels.Smtp, usedFallback: true);

    public int Calls { get; private set; }
    public string Channel => EmailChannels.Smtp;

    public FakeSmtpSender Returns(EmailSendResult result)
    {
        _result = result;
        return this;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_result);
    }
}
