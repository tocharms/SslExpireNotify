using System.Globalization;

namespace SslExpireNotify.Worker.Services;

/// <summary>Date shapes used by the email templates.</summary>
public static class DateFormatter
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly string[] ThaiMonths =
    [
        "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
        "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
    ];

    /// <summary>"18 September 2026".</summary>
    public static string LongEnglish(DateTime value) =>
        value.ToString("d MMMM yyyy", English);

    /// <summary>"18 Sep 2026" — used inside the grouped EXPIRED table rows.</summary>
    public static string ShortEnglish(DateTime value) =>
        value.ToString("d MMM yyyy", English);

    /// <summary>"18 กันยายน 2569" (Buddhist era).</summary>
    public static string LongThai(DateTime value) =>
        $"{value.Day} {ThaiMonths[value.Month - 1]} {value.Year + 543}";

    /// <summary>"18 September 2025 – 18 September 2026", or "-" when either end is unknown.</summary>
    public static string ContractPeriod(DateTime? start, DateTime? end) =>
        start is null || end is null
            ? "-"
            : $"{LongEnglish(start.Value)} – {LongEnglish(end.Value)}";
}
