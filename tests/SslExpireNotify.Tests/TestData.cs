using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;

namespace SslExpireNotify.Tests;

/// <summary>Shared builders so each test only spells out what it actually cares about.</summary>
internal static class TestData
{
    public static readonly DateTime Today = new(2026, 03, 10);

    public static List<AlertLevelOptions> DefaultLevels() =>
    [
        new() { Level = "NOTICE",  Days = 30, Severity = 1, RepeatEveryDays = 7 },
        new() { Level = "WARNING", Days = 15, Severity = 2, RepeatEveryDays = 7 },
        new() { Level = "URGENT",  Days = 7,  Severity = 3, RepeatEveryDays = 1 },
        new() { Level = "EXPIRED", Days = 0,  Severity = 4, RepeatEveryDays = 1 }
    ];

    public static AlertLevelResolver DefaultResolver() => new(DefaultLevels());

    public static AlertPlanner Planner(int contractThresholdDays = 199) =>
        new(DefaultResolver(), contractThresholdDays);

    public static SslCertificateRecord Certificate(
        int id = 1,
        int daysUntilExpiry = 25,
        decimal? salesId = 1001,
        string? salesEmail = "sales@ksc.net",
        string? customerEmail = "customer@example.com",
        DateTime? orderEndDate = null,
        bool orderEndDateNull = false,
        string domain = "www.example.co.th") => new()
        {
            SslCertId = id,
            CustomerId = 5001,
            CustomerDisplayName = "Example Co.",
            CustomerCompanyName = "Example Company Limited",
            DomainName = domain,
            CommonName = domain,
            OrderStartDate = Today.AddYears(-1),
            OrderEndDate = orderEndDateNull ? null : orderEndDate ?? Today.AddYears(3),
            SslExpiredDate = Today.AddDays(daysUntilExpiry),
            EmailAlert = customerEmail,
            SalesId = salesId,
            SalesEmail = salesEmail,
            SalesFirstName = "Somchai",
            SalesLastName = "Jaidee"
        };

    public static CertificateAlertRecord Alert(
        long alertId = 1,
        int certificateId = 1,
        string level = "NOTICE",
        string status = AlertStatus.Pending,
        DateTime? snapshot = null,
        DateTime? lastNotifiedAt = null,
        int notifyCount = 1,
        string notificationType = NotificationType.CertRenewal) => new()
        {
            AlertId = alertId,
            CertificateId = certificateId,
            AlertLevel = level,
            AlertStatus = status,
            NotificationType = notificationType,
            ExpireDateSnapshot = snapshot ?? Today.AddDays(25),
            DaysRemaining = 25,
            AckToken = Guid.NewGuid(),
            LastNotifiedAt = lastNotifiedAt,
            NotifyCount = notifyCount,
            CreatedAt = Today
        };

    public static PendingNotification Pending(
        SslCertificateRecord certificate,
        string level = "EXPIRED",
        string notificationType = NotificationType.CertRenewal,
        bool isFirstSend = true,
        int notifyCount = 1,
        int daysRemaining = -5,
        DateTime? snapshot = null) => new()
        {
            Alert = Alert(
                alertId: certificate.SslCertId,
                certificateId: certificate.SslCertId,
                level: level,
                snapshot: snapshot ?? Today.AddDays(daysRemaining),
                notifyCount: notifyCount,
                notificationType: notificationType),
            Certificate = certificate,
            Level = DefaultLevels().First(l => l.Level == level),
            IsFirstSend = isFirstSend,
            DaysRemaining = daysRemaining
        };
}
