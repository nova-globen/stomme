#nullable enable
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Security.Abstractions;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Resolves WHO and WHERE for a recording, server-side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The site comes from the request principal when there is a request, and from the RESOLVED ALIAS otherwise -
    /// a job, the boot path, or a background dispatch all meter against a real site without a caller naming one.
    /// Measured while building: <c>ICurrentUser.SiteId</c> is -1 with no <c>HttpContext</c>, so a recorder that
    /// trusted it alone recorded nothing outside a request and said so with <c>NoSite</c>.
    /// </para>
    /// <para>
    /// The user is deliberately NOT resolved that way: outside a request there is no user, and inventing one
    /// would attribute background consumption to a person.
    /// </para>
    /// </remarks>
    public sealed class MeteringScope
    {
        private readonly ICurrentUser _currentUser;
        private readonly ITenantContext _tenantContext;
        private readonly ITenantManager _tenantManager;

        public MeteringScope(ICurrentUser currentUser, ITenantContext tenantContext, ITenantManager tenantManager)
        {
            _currentUser = currentUser;
            _tenantContext = tenantContext;
            _tenantManager = tenantManager;
        }

        /// <summary>The site and tenant to record against, and the user to attribute it to (null outside a request).</summary>
        public async Task<(int SiteId, TenantId? TenantId, int? UserId)> ResolveAsync()
        {
            var siteId = _currentUser.SiteId;
            if (siteId <= 0)
            {
                var alias = await _tenantManager.GetAliasAsync();
                siteId = alias?.SiteId ?? -1;
            }

            return (siteId, _tenantContext.CurrentTenantId, _currentUser.UserId);
        }
    }
}
