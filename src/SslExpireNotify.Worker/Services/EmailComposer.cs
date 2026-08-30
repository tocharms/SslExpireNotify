using Microsoft.Extensions.Options;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

public sealed record RenderedEmail(string Subject, string Body);

public interface IEmailComposer
{
    /// <summary>Mail to the sales owner: one certificate, or the grouped EXPIRED digest.</summary>
    RenderedEmail RenderForSales(NotificationGroup group);

    /// <summary>Mail to the customer: always a single certificate.</summary>
    RenderedEmail RenderForCustomer(PendingNotification item);

    /// <summary>{AckBaseUrl}?tokens=t1,t2,... — always the list form, even for a single alert.</summary>
    string BuildAckLink(IEnumerable<Guid> tokens);
}

public sealed class EmailComposer : IEmailComposer
{
    private const string AckLabelInProgress = "✓ รับทราบ กำลังดำเนินการ / Acknowledged – In Progress";
    private const string AckLabelStopAlerts = "✓ รับทราบ — หยุดการแจ้งเตือน / Acknowledge – Stop Alerts";

    private readonly IEmailTemplateService _templates;
    private readonly EmailTemplateOptions _templateOptions;
    private readonly AckOptions _ackOptions;
    private readonly IJobClock _clock;

    public EmailComposer(
        IEmailTemplateService templates,
        IOptions<EmailTemplateOptions> templateOptions,
        IOptions<AckOptions> ackOptions,
        IJobClock clock)
    {
        _templates = templates;
        _templateOptions = templateOptions.Value;
        _ackOptions = ackOptions.Value;
        _clock = clock;
    }

    public string BuildAckLink(IEnumerable<Guid> tokens)
    {
        var joined = string.Join(',', tokens.Select(t => t.ToString()));
        var separator = _ackOptions.AckBaseUrl.Contains('?') ? '&' : '?';
        return $"{_ackOptions.AckBaseUrl}{separator}tokens={joined}";
    }

    public RenderedEmail RenderForSales(NotificationGroup group)
    {
        return group.IsGrouped
            ? RenderGroupedExpired(group)
            : RenderSingle(group.First, isCustomerMail: false);
    }

    public RenderedEmail RenderForCustomer(PendingNotification item) =>
        RenderSingle(item, isCustomerMail: true);

    private RenderedEmail RenderSingle(PendingNotification item, bool isCustomerMail)
    {
        var templateFile = ResolveTemplateFile(item, isCustomerMail);
        var template = _templates.StripRowTemplate(_templates.Load(templateFile));

        var values = BuildSingleValues(item, isCustomerMail);
        var body = _templates.Render(template, values);

        var subjectKey = SubjectKeySelector.Select(
            isCustomerMail,
            item.IsContractRenewal,
            isGrouped: false,
            item.Alert.AlertLevel,
            item.EffectiveNotifyCount);

        var subject = RenderSubject(subjectKey, BuildSubjectValues(item));

        return new RenderedEmail(subject, body);
    }

    private RenderedEmail RenderGroupedExpired(NotificationGroup group)
    {
        var templateFile = ResolveLevelTemplate(_templateOptions.TemplateFiles, WellKnownAlertLevels.Expired, "EmailTemplates:TemplateFiles");
        var raw = _templates.Load(templateFile);

        var ordered = group.Items
            .OrderBy(i => i.Alert.ExpireDateSnapshot)
            .ThenBy(i => i.Certificate.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var certRows = _templates.BuildCertRows(raw, ordered.Select(i => new CertRow(
            i.Certificate.CustomerName,
            i.Certificate.Domain,
            DateFormatter.ShortEnglish(i.Alert.ExpireDateSnapshot))));

        var template = _templates.StripRowTemplate(raw);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["certRows"] = certRows,
            ["certCount"] = ordered.Count.ToString(),
            ["saleName"] = HtmlText.Encode(ordered[0].Certificate.SalesName),
            ["ackLink"] = BuildAckLink(ordered.Select(i => i.Alert.AckToken)),
            ["ackButtonLabel"] = AckLabelStopAlerts
        };

        var body = _templates.Render(template, values);

        var subject = RenderSubject(SubjectKeySelector.ExpiredGroup, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["certCount"] = ordered.Count.ToString(),
            ["domain"] = ordered[0].Certificate.Domain
        });

        return new RenderedEmail(subject, body);
    }

    private string ResolveTemplateFile(PendingNotification item, bool isCustomerMail)
    {
        if (item.IsContractRenewal)
        {
            return _templateOptions.ContractTemplateFile;
        }

        return isCustomerMail
            ? ResolveLevelTemplate(_templateOptions.CustomerTemplateFiles, item.Alert.AlertLevel, "EmailTemplates:CustomerTemplateFiles")
            : ResolveLevelTemplate(_templateOptions.TemplateFiles, item.Alert.AlertLevel, "EmailTemplates:TemplateFiles");
    }

    private static string ResolveLevelTemplate(IReadOnlyDictionary<string, string> map, string level, string sectionName)
    {
        if (map.TryGetValue(level, out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        throw new InvalidOperationException($"{sectionName} has no template configured for alert level '{level}'.");
    }

    private Dictionary<string, string> BuildSingleValues(PendingNotification item, bool isCustomerMail)
    {
        var cert = item.Certificate;
        var alert = item.Alert;
        var expire = alert.ExpireDateSnapshot;
        var days = item.DaysRemaining;
        var daysOverdue = Math.Max(0, -days);
        var expireThai = DateFormatter.LongThai(expire);
        var expireEn = DateFormatter.LongEnglish(expire);
        var notifyCount = item.EffectiveNotifyCount;
        var isExpired = WellKnownAlertLevels.IsExpired(alert.AlertLevel);
        var saleName = HtmlText.Encode(cert.SalesName);

        var notifyCountLineThai = notifyCount > 1 ? $"นี่คือการแจ้งเตือนครั้งที่ {notifyCount} — " : string.Empty;
        var notifyCountLineEn = notifyCount > 1 ? $"This is notification number {notifyCount} — " : string.Empty;

        var statusThai = days > 0
            ? $"จะหมดอายุในอีก {days} วัน (วันที่ {expireThai})"
            : $"ได้หมดอายุไปแล้วเมื่อวันที่ {expireThai} ({daysOverdue} วันที่ผ่านมา)";

        var statusEn = days > 0
            ? $"will expire in {days} days (on {expireEn})"
            : $"expired on {expireEn} ({daysOverdue} days ago)";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["customerName"] = HtmlText.Encode(cert.CustomerName),
            ["domain"] = HtmlText.Encode(cert.Domain),
            ["saleName"] = saleName,
            ["contractPeriod"] = DateFormatter.ContractPeriod(cert.OrderStartDate, cert.OrderEndDate),
            ["expireDate"] = expireEn,
            ["expireDateEn"] = expireEn,
            ["expireDateThai"] = expireThai,
            ["expiredDate"] = DateFormatter.ShortEnglish(expire),
            ["days"] = Math.Max(0, days).ToString(),
            ["daysOverdue"] = daysOverdue.ToString(),
            ["notifyCount"] = notifyCount.ToString(),
            ["certCount"] = "1",
            ["orderEndDateThai"] = cert.OrderEndDate is null ? "-" : DateFormatter.LongThai(cert.OrderEndDate.Value),
            ["orderEndDateEn"] = cert.OrderEndDate is null ? "-" : DateFormatter.LongEnglish(cert.OrderEndDate.Value),
            ["certStatusLineThai"] = statusThai,
            ["certStatusLineEn"] = statusEn,
            ["notifyCountLineThai"] = notifyCountLineThai,
            ["notifyCountLineEn"] = notifyCountLineEn,
            ["greetingThai"] = isCustomerMail ? "เรียน ท่านลูกค้า" : $"เรียน คุณ{saleName}",
            ["greetingEn"] = isCustomerMail ? "Dear Customer," : $"Dear {saleName},",
            ["ackButtonLabel"] = isExpired ? AckLabelStopAlerts : AckLabelInProgress,
            ["ackLink"] = BuildAckLink([alert.AckToken])
        };
    }

    private static Dictionary<string, string> BuildSubjectValues(PendingNotification item)
    {
        var days = item.DaysRemaining;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["domain"] = item.Certificate.Domain,
            ["customerName"] = item.Certificate.CustomerName,
            ["days"] = Math.Max(0, days).ToString(),
            ["daysOverdue"] = Math.Max(0, -days).ToString(),
            ["notifyCount"] = item.EffectiveNotifyCount.ToString(),
            ["expireDate"] = DateFormatter.LongEnglish(item.Alert.ExpireDateSnapshot),
            ["certCount"] = "1"
        };
    }

    private string RenderSubject(string key, IReadOnlyDictionary<string, string> values)
    {
        if (!_templateOptions.Subjects.TryGetValue(key, out var template) || string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException($"EmailTemplates:Subjects has no entry for key '{key}'.");
        }

        return _templates.Render(template, values);
    }

    /// <summary>Exposed for diagnostics: the clock the composer uses for "today" based values.</summary>
    internal DateTime Today => _clock.Today;
}
