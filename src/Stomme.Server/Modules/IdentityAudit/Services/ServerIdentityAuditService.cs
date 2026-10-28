#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models.IdentityAudit;
using Stomme.Modules.IdentityAudit.Controllers;
using Stomme.IdentityAudit.Persistence;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;

namespace Stomme.Modules.IdentityAudit.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IIdentityAuditService"/>, for a page rendered on the server: the same Administrators
    /// decision the controller makes, then the same read - never an HTTP call back to its own host.
    /// </summary>
    public sealed class ServerIdentityAuditService(IIdentityAuditRepository audit, SiteState siteState, IHttpContextAccessor accessor) : IIdentityAuditService
    {
        public async Task<List<IdentityAuditEntryView>> GetRecentAsync(int days = 30, int take = 500)
        {
            var site = siteState.Alias?.SiteId ?? 0;
            if (site <= 0 || accessor.HttpContext?.User?.IsInRole(RoleNames.Admin) != true)
            {
                return new List<IdentityAuditEntryView>();
            }

            var window = Math.Clamp(days <= 0 ? 30 : days, 1, IdentityAuditController.MaximumDays);
            var rows = await audit.ListRecentAsync(site, DateTime.UtcNow.AddDays(-window), Math.Clamp(take <= 0 ? 500 : take, 1, IdentityAuditController.MaximumTake));
            return rows.Select(row => row.ToView()).ToList();
        }
    }
}
