using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// A site's OPERATORS - the people a site-scoped alert is for: the site's Administrators and the Host Users, who administer
    /// every site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fresh install's site has NO Administrators member: the install's one user is the host, holding only Host Users. An
    /// alert addressed to "the site's administrators" alone therefore reached nobody on a single-host installation - Metering's
    /// quota crossings, CloudUsage's budget and anomaly alerts and Health's site-scope notices were each silent until someone was
    /// made an administrator, and every suite that covered them stubbed the recipients (found by the BL-86 exemplar's drive,
    /// which addresses operators this way). Every site-scoped alert asks this one place.
    /// </para>
    /// <para>
    /// The Host Users role is installation-wide (its <c>SiteId</c> is null), so it answers for any site. A person holding both
    /// roles is one recipient; the ids come back in ascending order.
    /// </para>
    /// </remarks>
    public static class SiteOperators
    {
        public static async Task<IReadOnlyList<int>> GetSiteOperatorIdsAsync(this IUserRoleRepository userRoles, int siteId)
        {
            var administrators = await userRoles.GetUserRolesAsync(RoleNames.Admin, siteId);
            var hosts = await userRoles.GetUserRolesAsync(RoleNames.Host, siteId);
            return administrators.Concat(hosts).Select(role => role.UserId).Distinct().OrderBy(id => id).ToList();
        }
    }
}
