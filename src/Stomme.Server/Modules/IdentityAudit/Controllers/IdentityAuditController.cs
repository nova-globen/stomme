#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Models.IdentityAudit;
using Stomme.IdentityAudit.Persistence;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.IdentityAudit.Controllers
{
    /// <summary>
    /// <c>GET api/IdentityAudit?days=30&amp;take=500</c> - this site's identity audit ledger, newest first.
    /// </summary>
    /// <remarks>
    /// Gated by a FRAMEWORK policy - the Administrators role - and deliberately by no permission key: <c>identity.*</c> is
    /// the prefix D109 reserves, and who may read the account record of a site is the framework's question, not a
    /// module grant a site could hand out. The site is the request's own; nothing here takes one.
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class IdentityAuditController : ModuleControllerBase
    {
        public const int MaximumDays = 365;
        public const int MaximumTake = 2000;

        private readonly IIdentityAuditRepository _audit;
        private readonly SiteState _siteState;

        public IdentityAuditController(IIdentityAuditRepository audit, SiteState siteState, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _audit = audit;
            _siteState = siteState;
        }

        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<IdentityAuditEntryView>> Get(int days, int take, CancellationToken ct)
        {
            var site = _siteState.Alias?.SiteId ?? 0;
            if (site <= 0)
            {
                return Array.Empty<IdentityAuditEntryView>();
            }

            var window = Math.Clamp(days <= 0 ? 30 : days, 1, MaximumDays);
            var rows = await _audit.ListRecentAsync(site, DateTime.UtcNow.AddDays(-window), Math.Clamp(take <= 0 ? 500 : take, 1, MaximumTake), ct);
            return rows.Select(row => row.ToView()).ToList();
        }
    }
}
