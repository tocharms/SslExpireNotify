using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Repositories;

namespace SslExpireNotify.Worker.Services;

public sealed class CertificateAlertService : ICertificateAlertService
{
    private readonly ISslCertificateRepository _certificates;
    private readonly IAlertRepository _alerts;
    private readonly IJobRunHistoryRepository _history;
    private readonly IEmailComposer _composer;
    private readonly IEmailTemplateService _templates;
    private readonly ICompositeEmailSender _sender;
    private readonly IAlertLevelResolver _levels;
    private readonly IJobClock _clock;
    private readonly JobOptions _jobOptions;
    private readonly RecipientsOptions _recipientOptions;
    private readonly ILogger<CertificateAlertService> _logger;

    private long _dryRunAlertId;

    public CertificateAlertService(
        ISslCertificateRepository certificates,
        IAlertRepository alerts,
        IJobRunHistoryRepository history,
        IEmailComposer composer,
        IEmailTemplateService templates,
        ICompositeEmailSender sender,
        IAlertLevelResolver levels,
        IJobClock clock,
        IOptions<JobOptions> jobOptions,
        IOptions<RecipientsOptions> recipientOptions,
        ILogger<CertificateAlertService> logger)
    {
        _certificates = certificates;
        _alerts = alerts;
        _history = history;
        _composer = composer;
        _templates = templates;
        _sender = sender;
        _levels = levels;
        _clock = clock;
        _jobOptions = jobOptions.Value;
        _recipientOptions = recipientOptions.Value;
        _logger = logger;
    }

    private bool DryRun => _jobOptions.DryRun;

    public async Task<JobRunSummary> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var summary = new JobRunSummary();
        var today = _clock.Today;

        _templates.ResetCache();

        if (DryRun)
        {
            _logger.LogWarning("DRY RUN is enabled: the job will render everything but write nothing and send nothing.");
        }

        await AutoResolveAsync(summary, cancellationToken).ConfigureAwait(false);

        var certificates = await _certificates
            .GetMonitoredCertificatesAsync(_jobOptions.ActiveSslStatusValues, cancellationToken)
            .ConfigureAwait(false);

        summary.CertificatesScanned = certificates.Count;
        _logger.LogInformation("STEP 2: scanned {Count} certificate(s) for {Today:yyyy-MM-dd}", certificates.Count, today);

        if (certificates.Count == 0)
        {
            await PurgeHistoryAsync(cancellationToken).ConfigureAwait(false);
            return summary;
        }

        var alertsByCertificate = await LoadAlertsAsync(certificates, cancellationToken).ConfigureAwait(false);

        var planner = new AlertPlanner(_levels, _jobOptions.ContractThresholdDays);
        var pending = new List<PendingNotification>();

        foreach (var certificate in certificates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var item = await PlanCertificateAsync(planner, certificate, alertsByCertificate, today, summary, cancellationToken)
                    .ConfigureAwait(false);

                if (item is not null)
                {
                    pending.Add(item);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken row must never stop the rest of the scan.
                summary.CertificateErrors++;
                _logger.LogError(ex,
                    "STEP 3 failed for certificate {SslCertId} ({Domain}); continuing with the next certificate",
                    certificate.SslCertId, certificate.Domain);
            }
        }

        WarnOnHighErrorRate(summary);

        _logger.LogInformation(
            "STEP 3: {Created} new alert(s), {Resent} repeat send(s), {Superseded} superseded, {Errors} certificate error(s)",
            summary.AlertsCreated, summary.AlertsResent, summary.AlertsSuperseded, summary.CertificateErrors);

        var groups = NotificationGrouper.Group(pending);
        _logger.LogInformation("STEP 4: {GroupCount} email group(s) queued from {ItemCount} alert(s)", groups.Count, pending.Count);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await SendGroupAsync(group, summary, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.CertificateErrors++;
                _logger.LogError(ex,
                    "STEP 4 failed for the group owned by SalesID {SalesId} ({Count} alert(s)); continuing with the next group",
                    group.SalesId, group.Items.Count);
            }
        }

        _logger.LogInformation(
            "STEP 4 finished: {Sent} email(s) sent, {Failed} failed, {Fallback} delivered by the SMTP fallback",
            summary.EmailsSent, summary.EmailsFailed, summary.EmailsSentViaFallback);

        if (summary.EmailsSentViaFallback > 0)
        {
            _logger.LogWarning(
                "{Fallback} email(s) had to use the SMTP fallback in this run — the KSC Mail API needs to be checked.",
                summary.EmailsSentViaFallback);
        }

        await PurgeHistoryAsync(cancellationToken).ConfigureAwait(false);

        return summary;
    }

    private async Task AutoResolveAsync(JobRunSummary summary, CancellationToken cancellationToken)
    {
        if (DryRun)
        {
            _logger.LogInformation("STEP 1: auto-resolve skipped because DryRun is enabled.");
            return;
        }

        summary.AlertsResolved = await _alerts.AutoResolveAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("STEP 1: auto-resolved {Count} alert(s) whose certificate was extended", summary.AlertsResolved);
    }

    private async Task<Dictionary<int, List<CertificateAlertRecord>>> LoadAlertsAsync(
        IReadOnlyList<SslCertificateRecord> certificates,
        CancellationToken cancellationToken)
    {
        var ids = certificates.Select(c => c.SslCertId).Distinct().ToList();
        var alerts = await _alerts.GetAlertsForCertificatesAsync(ids, cancellationToken).ConfigureAwait(false);

        return alerts
            .GroupBy(a => a.CertificateId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private async Task<PendingNotification?> PlanCertificateAsync(
        AlertPlanner planner,
        SslCertificateRecord certificate,
        IReadOnlyDictionary<int, List<CertificateAlertRecord>> alertsByCertificate,
        DateTime today,
        JobRunSummary summary,
        CancellationToken cancellationToken)
    {
        var existing = alertsByCertificate.TryGetValue(certificate.SslCertId, out var list)
            ? list
            : [];

        var decision = planner.Plan(certificate, existing, today);

        if (decision.OrderEndDateMissing)
        {
            _logger.LogWarning(
                "Certificate {SslCertId} ({Domain}) has no OrderEndDate; treating it as {NotificationType}",
                certificate.SslCertId, certificate.Domain, decision.NotificationType);
        }

        if (decision.SupersedeAlertIds.Count > 0)
        {
            if (!DryRun)
            {
                var superseded = await _alerts.SupersedeAsync(decision.SupersedeAlertIds, cancellationToken).ConfigureAwait(false);
                summary.AlertsSuperseded += superseded;
            }
            else
            {
                summary.AlertsSuperseded += decision.SupersedeAlertIds.Count;
            }

            _logger.LogInformation(
                "Certificate {SslCertId} ({Domain}) moved up to {Level}; superseded alert(s) {AlertIds}",
                certificate.SslCertId, certificate.Domain, decision.Level?.Level, string.Join(",", decision.SupersedeAlertIds));
        }

        switch (decision.Kind)
        {
            case AlertDecisionKind.Create:
            {
                var alert = await CreateAlertAsync(certificate, decision, cancellationToken).ConfigureAwait(false);
                summary.AlertsCreated++;

                _logger.LogInformation(
                    "New {Level} alert {AlertId} ({NotificationType}) for certificate {SslCertId} ({Domain}), {Days} day(s) remaining",
                    decision.Level!.Level, alert.AlertId, alert.NotificationType, certificate.SslCertId, certificate.Domain, decision.DaysRemaining);

                return new PendingNotification
                {
                    Alert = alert,
                    Certificate = certificate,
                    Level = decision.Level!,
                    IsFirstSend = true,
                    DaysRemaining = decision.DaysRemaining
                };
            }

            case AlertDecisionKind.Resend:
            {
                summary.AlertsResent++;
                var alert = decision.ExistingAlert!;

                _logger.LogInformation(
                    "Repeat {Level} notification for alert {AlertId} (certificate {SslCertId}, {Domain}), previous send {LastNotifiedAt:yyyy-MM-dd}",
                    decision.Level!.Level, alert.AlertId, certificate.SslCertId, certificate.Domain, alert.LastNotifiedAt);

                return new PendingNotification
                {
                    Alert = alert,
                    Certificate = certificate,
                    Level = decision.Level!,
                    IsFirstSend = false,
                    DaysRemaining = decision.DaysRemaining
                };
            }

            default:
                if (decision.SkipReason is not null)
                {
                    _logger.LogDebug(
                        "Certificate {SslCertId} ({Domain}) skipped: {Reason}",
                        certificate.SslCertId, certificate.Domain, decision.SkipReason);
                }

                return null;
        }
    }

    private async Task<CertificateAlertRecord> CreateAlertAsync(
        SslCertificateRecord certificate,
        AlertDecision decision,
        CancellationToken cancellationToken)
    {
        var now = _clock.Now;

        var alert = new CertificateAlertRecord
        {
            CertificateId = certificate.SslCertId,
            AlertLevel = decision.Level!.Level,
            NotificationType = decision.NotificationType,
            ExpireDateSnapshot = decision.ExpireDateSnapshot,
            DaysRemaining = decision.DaysRemaining,
            AlertStatus = AlertStatus.Pending,
            AckToken = Guid.NewGuid(),
            AckTokenExpireAt = now.AddDays(_jobOptions.AckTokenValidDays),
            LastNotifiedAt = null,
            NotifyCount = 1,
            CreatedAt = now
        };

        if (DryRun)
        {
            // Negative ids make it obvious in the log that nothing was written.
            alert.AlertId = Interlocked.Decrement(ref _dryRunAlertId);
            return alert;
        }

        alert.AlertId = await _alerts.InsertAlertAsync(alert, cancellationToken).ConfigureAwait(false);
        return alert;
    }

    private async Task SendGroupAsync(NotificationGroup group, JobRunSummary summary, CancellationToken cancellationToken)
    {
        var plan = RecipientResolver.Resolve(group, _recipientOptions);
        var rendered = _composer.RenderForSales(group);

        await SendSalesMailAsync(group, plan, rendered, summary, cancellationToken).ConfigureAwait(false);

        if (plan.CustomerMailRequested)
        {
            await SendCustomerMailAsync(group.First, plan, summary, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendSalesMailAsync(
        NotificationGroup group,
        RecipientPlan plan,
        RenderedEmail rendered,
        JobRunSummary summary,
        CancellationToken cancellationToken)
    {
        var now = _clock.Now;

        if (plan.SalesTo is null)
        {
            _logger.LogWarning(
                "No usable sales email for SalesID {SalesId}; certificates {Certificates} keep their alert but stay unnotified",
                group.SalesId,
                string.Join(",", group.Items.Select(i => $"{i.Certificate.SslCertId}/{i.Certificate.Domain}")));

            summary.EmailsFailed++;

            await RecordAsync(
                group,
                recipient: null,
                subject: rendered.Subject,
                status: EmailSendStatus.Failed,
                channel: EmailChannels.MailApi,
                error: RecipientPlan_SalesError(plan),
                retryCount: 0,
                stamp: false,
                now,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        if (DryRun)
        {
            _logger.LogInformation(
                "DRY RUN: would send to {To} (cc {Cc}) subject {Subject} covering alert(s) {AlertIds}",
                plan.SalesTo, string.IsNullOrEmpty(plan.Cc) ? "-" : plan.Cc, rendered.Subject,
                string.Join(",", group.Items.Select(i => i.Alert.AlertId)));

            summary.EmailsSent++;
            return;
        }

        var result = await _sender.SendAsync(new EmailMessage
        {
            To = plan.SalesTo,
            Cc = plan.Cc,
            Subject = rendered.Subject,
            Body = rendered.Body
        }, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            summary.EmailsSent++;
            if (result.UsedFallback)
            {
                summary.EmailsSentViaFallback++;
            }
        }
        else
        {
            summary.EmailsFailed++;
            _logger.LogError(
                "Failed to notify {To} about alert(s) {AlertIds}: {Error}",
                plan.SalesTo, string.Join(",", group.Items.Select(i => i.Alert.AlertId)), result.ErrorMessage);
        }

        await RecordAsync(
            group,
            plan.SalesTo,
            rendered.Subject,
            result.Success ? EmailSendStatus.Success : EmailSendStatus.Failed,
            result.Channel,
            result.ErrorMessage,
            result.RetryCount,
            stamp: result.Success,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCustomerMailAsync(
        PendingNotification item,
        RecipientPlan plan,
        JobRunSummary summary,
        CancellationToken cancellationToken)
    {
        var now = _clock.Now;

        RenderedEmail rendered;
        try
        {
            rendered = _composer.RenderForCustomer(item);
        }
        catch (Exception ex)
        {
            // A broken customer template must not take the sales mail down with it.
            summary.EmailsFailed++;
            _logger.LogError(ex, "Could not render the customer email for alert {AlertId}", item.Alert.AlertId);
            return;
        }

        if (plan.CustomerTo is null)
        {
            _logger.LogWarning(
                "Certificate {SslCertId} ({Domain}) has no usable EmailAlert; the customer copy is skipped, the sales mail is unaffected",
                item.Certificate.SslCertId, item.Certificate.Domain);

            summary.EmailsFailed++;

            await RecordSingleAsync(
                item,
                recipient: null,
                rendered.Subject,
                EmailSendStatus.Failed,
                EmailChannels.MailApi,
                plan.CustomerError ?? RecipientResolver.NoCustomerEmailError,
                retryCount: 0,
                now,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        if (DryRun)
        {
            _logger.LogInformation(
                "DRY RUN: would send the customer copy to {To} (cc {Cc}) subject {Subject} for alert {AlertId}",
                plan.CustomerTo, string.IsNullOrEmpty(plan.Cc) ? "-" : plan.Cc, rendered.Subject, item.Alert.AlertId);

            summary.EmailsSent++;
            return;
        }

        var result = await _sender.SendAsync(new EmailMessage
        {
            To = plan.CustomerTo,
            Cc = plan.Cc,
            Subject = rendered.Subject,
            Body = rendered.Body
        }, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            summary.EmailsSent++;
            if (result.UsedFallback)
            {
                summary.EmailsSentViaFallback++;
            }
        }
        else
        {
            summary.EmailsFailed++;
            _logger.LogError(
                "Failed to send the customer copy to {To} for alert {AlertId}: {Error}",
                plan.CustomerTo, item.Alert.AlertId, result.ErrorMessage);
        }

        await RecordSingleAsync(
            item,
            plan.CustomerTo,
            rendered.Subject,
            result.Success ? EmailSendStatus.Success : EmailSendStatus.Failed,
            result.Channel,
            result.ErrorMessage,
            result.RetryCount,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One EmailLog row per alert in the group, so a digest can be traced back to each certificate.</summary>
    private Task RecordAsync(
        NotificationGroup group,
        string? recipient,
        string subject,
        string status,
        string channel,
        string? error,
        int retryCount,
        bool stamp,
        DateTime sentAt,
        CancellationToken cancellationToken)
    {
        if (DryRun)
        {
            return Task.CompletedTask;
        }

        var logs = group.Items.Select(i => new EmailLogRecord
        {
            AlertId = i.Alert.AlertId,
            RecipientEmail = recipient,
            RecipientType = RecipientTypes.To,
            Subject = subject,
            SendStatus = status,
            Channel = channel,
            ErrorMessage = error,
            RetryCount = retryCount,
            SentAt = sentAt
        }).ToList();

        var stampIds = stamp ? group.Items.Select(i => i.Alert.AlertId).ToList() : [];
        var incrementIds = stamp
            ? group.Items.Where(i => !i.IsFirstSend).Select(i => i.Alert.AlertId).ToList()
            : new List<long>();

        return _alerts.RecordDeliveryAsync(logs, stampIds, incrementIds, sentAt, cancellationToken);
    }

    /// <summary>
    /// EmailLog row for the customer copy. It never stamps LastNotifiedAt: the alert cycle is paced by the
    /// sales mail, and a missing customer address must not stop the next retry.
    /// </summary>
    private Task RecordSingleAsync(
        PendingNotification item,
        string? recipient,
        string subject,
        string status,
        string channel,
        string? error,
        int retryCount,
        DateTime sentAt,
        CancellationToken cancellationToken)
    {
        if (DryRun)
        {
            return Task.CompletedTask;
        }

        var log = new EmailLogRecord
        {
            AlertId = item.Alert.AlertId,
            RecipientEmail = recipient,
            RecipientType = RecipientTypes.To,
            Subject = subject,
            SendStatus = status,
            Channel = channel,
            ErrorMessage = error,
            RetryCount = retryCount,
            SentAt = sentAt
        };

        return _alerts.RecordDeliveryAsync([log], [], [], sentAt, cancellationToken);
    }

    private void WarnOnHighErrorRate(JobRunSummary summary)
    {
        if (summary.CertificatesScanned == 0 || summary.CertificateErrors == 0)
        {
            return;
        }

        var ratio = (double)summary.CertificateErrors / summary.CertificatesScanned;
        if (ratio >= _jobOptions.CertificateErrorWarningRatio)
        {
            _logger.LogWarning(
                "{Errors} of {Scanned} certificates failed to process ({Ratio:P0}). This looks systemic (database or configuration), not like bad rows.",
                summary.CertificateErrors, summary.CertificatesScanned, ratio);
        }
    }

    private async Task PurgeHistoryAsync(CancellationToken cancellationToken)
    {
        var cutoff = _clock.Now.AddDays(-_jobOptions.JobRunHistoryRetentionDays);
        var removed = await _history.PurgeAsync(cutoff, cancellationToken).ConfigureAwait(false);

        if (removed > 0)
        {
            _logger.LogInformation("Purged {Count} JobRunHistory row(s) older than {Cutoff:yyyy-MM-dd}", removed, cutoff);
        }
    }

    private static string RecipientPlan_SalesError(RecipientPlan plan) =>
        plan.SalesError ?? RecipientResolver.NoRecipientError;
}
