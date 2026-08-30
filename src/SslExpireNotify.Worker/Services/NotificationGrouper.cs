using SslExpireNotify.Worker.Models;

namespace SslExpireNotify.Worker.Services;

/// <summary>
/// STEP 4 grouping. Expired certificate renewals are digested per sales owner; everything else stays
/// one mail per certificate.
/// </summary>
public static class NotificationGrouper
{
    public static IReadOnlyList<NotificationGroup> Group(IEnumerable<PendingNotification> pending)
    {
        var groups = new List<NotificationGroup>();
        var digestCandidates = new List<PendingNotification>();

        foreach (var item in pending)
        {
            var isDigest = !item.IsContractRenewal && item.IsExpiredLevel;

            if (isDigest && item.Certificate.SalesId is not null)
            {
                digestCandidates.Add(item);
                continue;
            }

            if (isDigest)
            {
                // No sales owner to group by: the mail still uses the list template, with a single row.
                groups.Add(new NotificationGroup
                {
                    Items = [item],
                    SalesId = null,
                    IsGrouped = true
                });
                continue;
            }

            // Group 1 (NOTICE/WARNING/URGENT) and group 3 (CONTRACT_RENEWAL): one mail per certificate.
            groups.Add(new NotificationGroup
            {
                Items = [item],
                SalesId = item.Certificate.SalesId,
                IsGrouped = false
            });
        }

        // Group 2: one digest per sales owner, oldest expiry first inside the mail.
        foreach (var bySales in digestCandidates.GroupBy(i => i.Certificate.SalesId!.Value))
        {
            groups.Add(new NotificationGroup
            {
                Items = bySales
                    .OrderBy(i => i.Alert.ExpireDateSnapshot)
                    .ThenBy(i => i.Certificate.Domain, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                SalesId = bySales.Key,
                IsGrouped = true
            });
        }

        return groups;
    }
}
