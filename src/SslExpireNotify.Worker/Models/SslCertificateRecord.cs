namespace SslExpireNotify.Worker.Models;

/// <summary>
/// One row of SSL_Certificate joined with CUSTOMER and KSC_USERS. Read only: this application never
/// writes to any of those three tables.
/// </summary>
public sealed class SslCertificateRecord
{
    public int SslCertId { get; init; }
    public decimal? CustomerId { get; init; }
    public string? CommonName { get; init; }
    public string? DomainName { get; init; }

    public DateTime? OrderStartDate { get; init; }
    public DateTime? OrderEndDate { get; init; }
    public DateTime? SslExpiredDate { get; init; }

    /// <summary>Customer facing address, used only when Recipients:SendToCustomer is enabled.</summary>
    public string? EmailAlert { get; init; }

    public decimal? SalesId { get; init; }

    /// <summary>CUSTOMER.DISPLAYNAME.</summary>
    public string? CustomerDisplayName { get; init; }

    /// <summary>CUSTOMER.COMPANYNAME, used when DISPLAYNAME is null.</summary>
    public string? CustomerCompanyName { get; init; }

    public string? SalesEmail { get; init; }
    public string? SalesFirstName { get; init; }
    public string? SalesLastName { get; init; }

    /// <summary>Customer name shown in the templates.</summary>
    public string CustomerName =>
        FirstNonEmpty(CustomerDisplayName, CustomerCompanyName) ?? "-";

    /// <summary>Domain shown in the templates: DomainName, falling back to CommonName.</summary>
    public string Domain =>
        FirstNonEmpty(DomainName, CommonName) ?? "-";

    public string SalesName
    {
        get
        {
            var name = $"{SalesFirstName} {SalesLastName}".Trim();
            return string.IsNullOrWhiteSpace(name) ? "-" : name;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
