using System.Net.Mail;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// The source columns are nvarchar(50) free text, so every address is validated before it is used.
/// </summary>
public static class EmailAddressHelper
{
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var trimmed = address.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains(',') || trimmed.Contains(';'))
        {
            return false;
        }

        if (!MailAddress.TryCreate(trimmed, out var parsed))
        {
            return false;
        }

        // MailAddress accepts "user@host" without a dot; a real mailbox needs a dotted domain.
        var host = parsed.Host;
        return host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.');
    }

    public static string? Normalize(string? address) =>
        IsValid(address) ? address!.Trim() : null;

    /// <summary>Splits a comma separated Cc list and keeps only the addresses that parse.</summary>
    public static IReadOnlyList<string> SplitValid(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return [];
        }

        return list
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValid)
            .ToList();
    }
}
