#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Help.Persistence
{
    /// <summary>
    /// The help topics' closed read surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No method here takes a site.</b> A plain SiteId filter is not enough: the
    /// persistence seam's automatic isolation is TENANT-only, and the default install puts every site in one tenant,
    /// so a site parameter would be a caller's chance to get it wrong. The site comes from the resolved alias, once,
    /// here - and a gate with two sites in ONE tenant drives every method.
    /// </para>
    /// <para>
    /// <b>The only <c>FindAsync</c> is on the site id, an int.</b> Resolution is a by-id read on a deterministic id
    /// - a <c>FindAsync</c> over anchor and locale would be a string comparison, and this
    /// repository has measured three different answers to that across its providers.
    /// </para>
    /// <para>
    /// The namespace is <c>Stomme.Help.Persistence</c>, not <c>Stomme.Modules.*</c>: the Metering, Notifications,
    /// DataExport and MCP convention, so that <c>ModuleStoreRegistrationTests</c> - which holds every closed store
    /// contract under <c>Stomme.Modules.*</c> to a hand-written EF and RavenDB twin - does not demand twins for a
    /// repository that delegates to the storage-blind closed <c>IRepository</c>.
    /// </para>
    /// </remarks>
    public interface IHelpTopicRepository
    {
        /// <summary>The topic for this site, anchor and locale, or null. A by-id read on the deterministic id.</summary>
        Task<HelpTopicAggregate?> GetAsync(string anchor, string locale, CancellationToken ct = default);

        /// <summary>The topic with this id, or null when it does not exist OR belongs to another site.</summary>
        Task<HelpTopicAggregate?> GetByIdForSiteAsync(Guid id, CancellationToken ct = default);

        /// <summary>Every topic of this site, drafts included. The <c>help:manage</c> list.</summary>
        Task<IReadOnlyList<HelpTopicAggregate>> ListAsync(CancellationToken ct = default);

        /// <summary>Every PUBLISHED topic of this site.</summary>
        Task<IReadOnlyList<HelpTopicAggregate>> ListPublishedAsync(CancellationToken ct = default);

        Task AddAsync(HelpTopicAggregate topic, CancellationToken ct = default);

        Task UpdateAsync(HelpTopicAggregate topic, CancellationToken ct = default);

        Task DeleteAsync(HelpTopicAggregate topic, CancellationToken ct = default);
    }

    public sealed class HelpTopicRepository : IHelpTopicRepository
    {
        private readonly IRepository<HelpTopicAggregate, Guid> _inner;
        private readonly SiteState _siteState;
        private readonly ITenantContext _tenantContext;

        public HelpTopicRepository(IRepository<HelpTopicAggregate, Guid> inner, SiteState siteState, ITenantContext tenantContext)
        {
            _inner = inner;
            _siteState = siteState;
            _tenantContext = tenantContext;
        }

        /// <summary>The site THIS REQUEST resolved, read fresh every time and never captured.</summary>
        private int SiteId => _siteState.Alias?.SiteId ?? -1;

        /// <summary>
        /// The tenant the id is derived under - the SAME answer the provider's tenant filter uses
        /// (<c>TenantAwareDbContextFactory</c> falls back to the single logical tenant in exactly this way), so an id this
        /// computes names a row that filter can see. Part of the key because site ids repeat across tenants.
        /// </summary>
        private Guid TenantKey => (_tenantContext.CurrentTenantId ?? TenantId.Default).Value;

        public async Task<HelpTopicAggregate?> GetAsync(string anchor, string locale, CancellationToken ct = default)
        {
            var siteId = SiteId;
            if (siteId <= 0 || !HelpKeys.TryNormalizeAnchor(anchor, out var normalizedAnchor) || !HelpKeys.TryNormalizeLocale(locale, out var normalizedLocale))
            {
                // Not an exception: the anchor comes from the wire, and "no topic" is the answer.
                return null;
            }

            return await GetOwnedAsync(HelpTopicIds.For(TenantKey, siteId, normalizedAnchor, normalizedLocale), siteId, ct);
        }

        public async Task<HelpTopicAggregate?> GetByIdForSiteAsync(Guid id, CancellationToken ct = default)
        {
            var siteId = SiteId;
            return siteId <= 0 ? null : await GetOwnedAsync(id, siteId, ct);
        }

        public async Task<IReadOnlyList<HelpTopicAggregate>> ListAsync(CancellationToken ct = default)
        {
            var siteId = SiteId;
            return siteId <= 0
                ? Array.Empty<HelpTopicAggregate>()
                : await _inner.FindAsync(topic => topic.SiteId == siteId, ct);
        }

        public async Task<IReadOnlyList<HelpTopicAggregate>> ListPublishedAsync(CancellationToken ct = default)
        {
            var siteId = SiteId;
            return siteId <= 0
                ? Array.Empty<HelpTopicAggregate>()
                : await _inner.FindAsync(topic => topic.SiteId == siteId && topic.IsPublished == true, ct);
        }

        public Task AddAsync(HelpTopicAggregate topic, CancellationToken ct = default) => _inner.AddAsync(topic, ct);

        public Task UpdateAsync(HelpTopicAggregate topic, CancellationToken ct = default) => _inner.UpdateAsync(topic, ct);

        public Task DeleteAsync(HelpTopicAggregate topic, CancellationToken ct = default) => _inner.DeleteAsync(topic, ct);

        /// <summary>
        /// A by-id read that gives ONE answer - null - for "no such topic" and "another site's topic". The id already
        /// encodes the site, so a mismatch means a caller supplied an id from somewhere else, and the id space must
        /// not tell them whether it exists.
        /// </summary>
        private async Task<HelpTopicAggregate?> GetOwnedAsync(Guid id, int siteId, CancellationToken ct)
        {
            var topic = await _inner.GetByIdAsync(id, ct);
            return topic is null || topic.SiteId != siteId ? null : topic;
        }
    }
}
