using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Services;

/// <summary>Picks which key of EmailTemplates:Subjects a given mail uses.</summary>
public static class SubjectKeySelector
{
    public const string ExpiredGroup = "EXPIRED_GROUP";
    public const string Contract = "CONTRACT";
    public const string ContractExpired = "CONTRACT_EXPIRED";
    public const string ContractExpiredRepeat = "CONTRACT_EXPIRED_REPEAT";
    public const string CustomerExpired = "CUSTOMER_EXPIRED";
    public const string CustomerExpiredRepeat = "CUSTOMER_EXPIRED_REPEAT";

    public static string Select(bool isCustomerMail, bool isContractRenewal, bool isGrouped, string level, int notifyCount)
    {
        // The grouped digest exists only for CERT_RENEWAL / EXPIRED / sales.
        if (isGrouped)
        {
            return ExpiredGroup;
        }

        var expired = WellKnownAlertLevels.IsExpired(level);

        if (isContractRenewal)
        {
            if (!expired)
            {
                return Contract;
            }

            return notifyCount > 1 ? ContractExpiredRepeat : ContractExpired;
        }

        if (isCustomerMail && expired)
        {
            return notifyCount > 1 ? CustomerExpiredRepeat : CustomerExpired;
        }

        return level;
    }
}
