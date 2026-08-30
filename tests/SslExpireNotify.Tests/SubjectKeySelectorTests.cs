using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class SubjectKeySelectorTests
{
    [Theory]
    [InlineData("NOTICE")]
    [InlineData("WARNING")]
    [InlineData("URGENT")]
    public void Sales_mails_before_expiry_use_the_level_key(string level)
    {
        var key = SubjectKeySelector.Select(isCustomerMail: false, isContractRenewal: false, isGrouped: false, level, notifyCount: 1);

        Assert.Equal(level, key);
    }

    [Fact]
    public void The_expired_digest_always_uses_EXPIRED_GROUP()
    {
        // NotifyCount differs per certificate in the digest, so there is no per-certificate subject.
        var key = SubjectKeySelector.Select(isCustomerMail: false, isContractRenewal: false, isGrouped: true, "EXPIRED", notifyCount: 5);

        Assert.Equal(SubjectKeySelector.ExpiredGroup, key);
    }

    [Theory]
    [InlineData("NOTICE")]
    [InlineData("WARNING")]
    [InlineData("URGENT")]
    public void Contract_renewals_before_expiry_use_CONTRACT(string level)
    {
        var key = SubjectKeySelector.Select(isCustomerMail: false, isContractRenewal: true, isGrouped: false, level, notifyCount: 1);

        Assert.Equal(SubjectKeySelector.Contract, key);
    }

    [Fact]
    public void An_expired_contract_renewal_switches_to_the_repeat_subject_after_the_first_mail()
    {
        Assert.Equal(SubjectKeySelector.ContractExpired,
            SubjectKeySelector.Select(false, true, false, "EXPIRED", notifyCount: 1));

        Assert.Equal(SubjectKeySelector.ContractExpiredRepeat,
            SubjectKeySelector.Select(false, true, false, "EXPIRED", notifyCount: 2));
    }

    [Fact]
    public void Customer_mails_get_their_own_expired_subjects()
    {
        Assert.Equal(SubjectKeySelector.CustomerExpired,
            SubjectKeySelector.Select(true, false, false, "EXPIRED", notifyCount: 1));

        Assert.Equal(SubjectKeySelector.CustomerExpiredRepeat,
            SubjectKeySelector.Select(true, false, false, "EXPIRED", notifyCount: 3));
    }

    [Fact]
    public void Customer_mails_before_expiry_share_the_level_subject_with_sales()
    {
        Assert.Equal("WARNING", SubjectKeySelector.Select(true, false, false, "WARNING", notifyCount: 1));
    }
}
