#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Models.IdentityAudit;
using Stomme.Modules.IdentityAudit.Domain;
using Stomme.IdentityAudit.Persistence;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.IdentityAudit.Application
{
    /// <summary>
    /// Writes one line of the identity audit ledger. The caller says WHAT happened and to WHOM; the recorder
    /// derives everything else - tenant, site, the acting user, the client address, the time - so no caller can supply
    /// them (the "no caller-supplied subject" rule of the security posture).
    /// </summary>
    public interface IIdentityAuditRecorder
    {
        /// <param name="siteId">The site the account belongs to when the caller knows it (a sign-in names one); otherwise the request's site.</param>
        Task RecordAsync(IdentityAuditEvent evt, int? subjectUserId, string? subjectName, string? detail = null, int? siteId = null);
    }

    /// <summary>
    /// The recorder. It never throws into the flow it audits: a sign-in must not fail because its audit line could not
    /// be written. A write that cannot be made is logged at Error - loudly, because an audit that silently stops is the
    /// failure this ledger exists to prevent.
    /// </summary>
    public sealed class IdentityAuditRecorder(
        IIdentityAuditRepository audit,
        ITenantContext tenantContext,
        SiteState siteState,
        IHttpContextAccessor accessor,
        ILogger<IdentityAuditRecorder> logger) : IIdentityAuditRecorder
    {
        public async Task RecordAsync(IdentityAuditEvent evt, int? subjectUserId, string? subjectName, string? detail = null, int? siteId = null)
        {
            try
            {
                var site = siteId is > 0 ? siteId.Value : siteState?.Alias?.SiteId ?? 0;
                var tenant = tenantContext.CurrentTenantId;
                if (tenant is null || site <= 0)
                {
                    logger.LogError("Identity audit: {Event} for user {UserId} was NOT recorded - no tenant or site is resolved for this request.", evt, subjectUserId);
                    return;
                }

                var context = accessor.HttpContext;
                var principal = context?.User;
                int? actor = principal?.Identity?.IsAuthenticated == true ? principal.UserId() : null;
                var address = context?.Connection.RemoteIpAddress?.ToString();

                await audit.AddAsync(IdentityAuditEntryAggregate.Record(
                    tenant, site, evt, actor, subjectUserId, subjectName, address, detail, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Identity audit: {Event} for user {UserId} could not be written.", evt, subjectUserId);
            }
        }
    }
}
