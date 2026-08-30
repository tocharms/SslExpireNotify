using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// Who receives one notification group. The sales mail and the customer mail are completely independent:
/// one failing never stops the other.
/// </summary>
public sealed record RecipientPlan
{
    /// <summary>Validated sales address, null when the certificate has no usable sales email.</summary>
    public string? SalesTo { get; init; }

    /// <summary>Reason the sales mail cannot be sent; written to EmailLog.ErrorMessage.</summary>
    public string? SalesError { get; init; }

    /// <summary>Cc applied to every mail, already filtered to valid addresses. Empty string = no Cc.</summary>
    public string Cc { get; init; } = string.Empty;

    /// <summary>True when a separate customer mail is expected for this group.</summary>
    public bool CustomerMailRequested { get; init; }

    /// <summary>Validated customer address, null when EmailAlert is missing or malformed.</summary>
    public string? CustomerTo { get; init; }

    public string? CustomerError { get; init; }
}

public static class RecipientResolver
{
    public const string NoRecipientError = "no recipient";
    public const string NoCustomerEmailError = "no customer email";

    public static RecipientPlan Resolve(NotificationGroup group, RecipientsOptions options)
    {
        var cc = string.Join(",", EmailAddressHelper.SplitValid(options.Cc));

        var salesEmail = EmailAddressHelper.Normalize(group.First.Certificate.SalesEmail);

        // The grouped digest is a sales view of the world; it is never exploded to customers.
        var customerRequested = options.SendToCustomer && !group.IsGrouped;

        string? customerTo = null;
        string? customerError = null;

        if (customerRequested)
        {
            customerTo = EmailAddressHelper.Normalize(group.First.Certificate.EmailAlert);
            if (customerTo is null)
            {
                customerError = NoCustomerEmailError;
            }
        }

        return new RecipientPlan
        {
            SalesTo = salesEmail,
            SalesError = salesEmail is null ? NoRecipientError : null,
            Cc = cc,
            CustomerMailRequested = customerRequested,
            CustomerTo = customerTo,
            CustomerError = customerError
        };
    }
}
