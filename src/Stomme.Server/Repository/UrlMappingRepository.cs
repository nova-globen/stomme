using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Repository
{
    public interface IUrlMappingRepository
    {
        Task<IEnumerable<UrlMapping>> GetUrlMappingsAsync(int siteId, bool isMapped);
        Task<UrlMapping> AddUrlMappingAsync(UrlMapping urlMapping);
        Task<UrlMapping> UpdateUrlMappingAsync(UrlMapping urlMapping);
        Task<UrlMapping> GetUrlMappingAsync(int urlMappingId);
        Task<UrlMapping> GetUrlMappingAsync(int urlMappingId, bool tracking);
        Task<UrlMapping> GetUrlMappingAsync(int siteId, string url);
        Task<UrlMapping> GetUrlMappingAsync(int siteId, string url, string referrer);
        Task DeleteUrlMappingAsync(int urlMappingId);
        Task<int> DeleteUrlMappingsAsync(int siteId, int age);
    }

    /// <summary>
    /// The UrlMapping cluster on the persistence seam: writes through <see cref="IUrlMappingStore"/>,
    /// reads through <see cref="IFrameworkStoreSessionFactory"/>. The <c>DbSet&lt;UrlMapping&gt;</c> is
    /// gone from <see cref="TenantDBContext"/> — that deletion is the compiler's proof this cluster moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one outbound edge is <see cref="ISiteRepository"/>, and it is acyclic.</b> Checked
    /// directly against the dependency graph: <c>SiteRepository</c> takes Role, Profile,
    /// Folder, Page, Module, PageModule, ModuleDefinition, Theme and Setting — not this one. The stronger
    /// statement is the true one: NO repository anywhere takes <see cref="IUrlMappingRepository"/>. Its only
    /// consumers are <c>PageController</c>, <c>UrlMappingController</c>, <c>PurgeJob</c>,
    /// <c>Files.cshtml.cs</c>, <c>Sitemap.cshtml.cs</c> and <c>App.razor</c> — controllers, pages and a job.
    /// So the edge cannot close a loop from any direction, and the store-instead-of-repository escape
    /// used for Permission is not needed here.
    /// </para>
    /// <para>
    /// <b>ON THE RENDER PATH.</b> <c>App.razor</c> calls <see cref="GetUrlMappingAsync(int, string, string)"/>
    /// on every request whose path does not resolve to a page — the 404 handler — and that overload WRITES.
    /// </para>
    /// <para>
    /// <b>THIS CLUSTER RESOLVES A STORE PER WRITE, AND IT IS THE ONLY ONE ON THE SEAM THAT DOES. THAT IS NOT
    /// STYLE — IT IS A BUG FIX, AND THE BUG WAS OBSERVED.</b> Every other migrated repository takes its store
    /// by constructor injection, which is safe for them because a store owns ONE
    /// <see cref="TenantDBContext"/> for its lifetime (see <c>EfFrameworkStore&lt;TAggregate&gt;</c>) and they
    /// each write a given row at most once per instance. This cluster is the first that writes the SAME ROW
    /// TWICE through one repository instance, because <see cref="GetUrlMappingAsync(int, string, string)"/> is
    /// NOT A PURE READ — it records the request, so it writes — and its callers then write again:
    /// <code>
    ///     // PageController.UpdatePage, on every page rename:
    ///     var urlMapping = await _urlMappings.GetUrlMappingAsync(page.SiteId, page.Path); // WRITE #1
    ///     if (urlMapping != null) { urlMapping.MappedUrl = ...; await UpdateUrlMappingAsync(urlMapping); } // WRITE #2
    /// </code>
    /// A controller holds ONE transient repository for the whole request, so both writes hit one context and
    /// the second one throws:
    /// <code>
    ///     System.InvalidOperationException: The instance of entity type 'UrlMappingAggregate' cannot be
    ///     tracked because another instance with the same key value for {'Id'} is already being tracked.
    /// </code>
    /// OBSERVED against the real host on PostgreSQL, not reasoned about — the compiler, 452 unit tests, the
    /// conformance suite on both providers and the render guard were ALL green while renaming a page was
    /// broken. The harvested code never had this: it opened a fresh <c>DbContext</c> INSIDE EVERY METHOD, so
    /// no identity map outlived a call. Resolving a store per write restores exactly that lifetime — it is the
    /// faithful port, and constructor-injecting the store was the unfaithful one.
    /// <para>
    /// <see cref="EfRepository{TAggregate, TId}"/> is where this could be fixed centrally (it could detach a
    /// conflicting entry before attaching); it is deliberately NOT touched here — it is a known latent defect
    /// with its own owner. The seam's own guard <c>The_store_rejects_a_detached_update_after_a_tracked_read</c>
    /// documents this exact behaviour as intended, so the store is behaving as designed and the repository is
    /// what had to adapt. Pinned by <c>TheUrlMappingWriteLifetimeTests</c>.
    /// </para>
    /// </para>
    /// </remarks>
    public class UrlMappingRepository : IUrlMappingRepository
    {
        private readonly IServiceProvider _services;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly ISiteRepository _sites;

        public UrlMappingRepository(IServiceProvider services, IFrameworkStoreSessionFactory sessions, ISiteRepository sites)
        {
            _services = services;
            _sessions = sessions;
            _sites = sites;
        }

        /// <summary>
        /// A store bound to a FRESH unit of work, as the harvested code's per-method
        /// <c>CreateDbContextAsync()</c> was. See the remarks on this class: reusing one store across two
        /// writes of the same row throws. <see cref="IUrlMappingStore"/> is registered transient, so every
        /// call here yields a new store over a new context; the scope disposes them.
        /// </summary>
        private IUrlMappingStore NewStore() => _services.GetRequiredService<IUrlMappingStore>();

        /// <summary>
        /// Opens a read session, runs <paramref name="read"/> against it and disposes it. Reads NEVER go
        /// through the store.
        /// </summary>
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        /// <summary>
        /// Splits the site's mappings into the redirects the admin manages (<paramref name="isMapped"/> true)
        /// and the broken-link report (false).
        /// </summary>
        /// <remarks>
        /// <b>The predicate is <c>string.IsNullOrEmpty</c>, and it is kept EXACTLY as harvested</b> rather than
        /// narrowed to <c>== ""</c>. The mapped_url column is NOT NULL, so only the empty arm can match a real
        /// row today — but the harvested query tests both, a provider is free to translate the two arms
        /// differently, and rewriting a predicate to match the schema's current nullability is the kind of
        /// tidy-up that silently changes behaviour when a later migration relaxes the column. Both arms and
        /// both directions are pinned on both providers by the conformance suite
        /// (<c>Url_mappings_split_into_the_mapped_and_the_unmapped_halves</c>) — which is where a translation
        /// gap would surface, exactly as a sentinel comparison once did.
        /// </remarks>
        public async Task<IEnumerable<UrlMapping>> GetUrlMappingsAsync(int siteId, bool isMapped)
        {
            return await QueryAsync(session =>
            {
                var query = session.Query<UrlMappingAggregate>().Where(item => item.SiteId == siteId);

                query = isMapped
                    ? query.Where(item => !string.IsNullOrEmpty(item.MappedUrl))
                    : query.Where(item => string.IsNullOrEmpty(item.MappedUrl));

                return query.ToList().ConvertAll(UrlMappingMapper.ToModel);
            });
        }

        public async Task<UrlMapping> AddUrlMappingAsync(UrlMapping urlMapping)
        {
            var aggregate = UrlMappingMapper.ToAggregate(urlMapping, assignId: false);
            await NewStore().AddAsync(aggregate);
            UrlMappingMapper.CopyPersistedValues(aggregate, urlMapping);
            return urlMapping;
        }

        public async Task<UrlMapping> UpdateUrlMappingAsync(UrlMapping urlMapping)
        {
            var aggregate = UrlMappingMapper.ToAggregate(urlMapping, assignId: true);
            await NewStore().UpdateAsync(aggregate);
            UrlMappingMapper.CopyPersistedValues(aggregate, urlMapping);
            return urlMapping;
        }

        public async Task<UrlMapping> GetUrlMappingAsync(int urlMappingId)
        {
            return await GetUrlMappingAsync(urlMappingId, true);
        }

        /// <summary>
        /// NOTE the <paramref name="tracking"/> parameter is now INERT, and deliberately kept on the
        /// interface. A session read tracks nothing, so both arms return the same detached model; the
        /// harvested tracking arm never mattered anyway, because the only mutating caller
        /// (<c>PageController.UpdatePage</c>) hands the result back to <see cref="UpdateUrlMappingAsync"/>,
        /// which re-attaches it on a different context. Removing the overload would churn
        /// <c>UrlMappingController</c> for no behaviour change; it stays at parity.
        /// </summary>
        public async Task<UrlMapping> GetUrlMappingAsync(int urlMappingId, bool tracking)
        {
            return await QueryAsync(session => UrlMappingMapper.ToModel(session.Query<UrlMappingAggregate>()
                .FirstOrDefault(item => item.Id == urlMappingId)));
        }

        public async Task<UrlMapping> GetUrlMappingAsync(int siteId, string url)
        {
            return await GetUrlMappingAsync(siteId, url, "");
        }

        /// <summary>
        /// Resolves a requested URL, RECORDING the request as a side effect: a known URL has its
        /// <see cref="UrlMapping.Requests"/> incremented, an unknown one is captured as a broken link if the
        /// site says so. <c>App.razor</c>'s 404 handler and <c>Files.cshtml.cs</c> both call it on the request
        /// path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The 750-character truncation is NOT cosmetic and is kept exactly: <c>url</c> is an INDEX column
        /// capped at varchar(750) because MySQL limits an index to 3072 bytes. See
        /// <see cref="UrlMappingAggregate"/>.
        /// </para>
        /// <para>
        /// <b>The read-modify-write below never depended on change tracking</b>, so it ports unchanged: the
        /// harvested code read on one context and called <c>UpdateUrlMappingAsync</c>, which set
        /// <c>State = Modified</c> on ANOTHER context. It was always a detached update. What DOES change is the
        /// duplicate-key catch — see the remarks on <see cref="UrlMappingAggregate"/>: the relational unique
        /// index on (site_id, url) is the race control here, and RavenDB has no equivalent.
        /// </para>
        /// </remarks>
        public async Task<UrlMapping> GetUrlMappingAsync(int siteId, string url, string referrer)
        {
            url = (url.StartsWith("/")) ? url.Substring(1) : url;
            url = (url.Length > 750) ? url.Substring(0, 750) : url;

            // Normalize the ARGUMENT in C# and filter the normalized COLUMN, never the raw Url. A bare
            // `item.Url == url` means two different things behind the seam — case-SENSITIVE on PostgreSQL,
            // case-INSENSITIVE on RavenDB (DRIVEN on both real hosts) — so a mixed-case row silently resolved
            // on one provider and not the other on the render 404 path. Both sides lowercased, the providers
            // agree (case-INSENSITIVE, matching the Folder/File precedent). NEVER `.ToLower()` inside the
            // query — it THROWS on RavenDB. The STORED Url keeps its case; UrlNormalized is the lookup key.
            var urlNormalized = NormalizedText.Normalize(url);

            var urlMapping = await QueryAsync(session => UrlMappingMapper.ToModel(session.Query<UrlMappingAggregate>()
                .FirstOrDefault(item => item.SiteId == siteId && item.UrlNormalized == urlNormalized)));

            if (urlMapping == null)
            {
                var site = await _sites.GetSiteAsync(siteId);
                if (site.CaptureBrokenUrls)
                {
                    urlMapping = new UrlMapping
                    {
                        SiteId = siteId,
                        Url = url,
                        MappedUrl = "",
                        Requests = 1,
                        Referrer = referrer,
                        CreatedOn = DateTime.UtcNow,
                        RequestedOn = DateTime.UtcNow
                    };
                    try
                    {
                        urlMapping = await AddUrlMappingAsync(urlMapping);
                    }
                    catch
                    {
                        // ignore duplicate key exception which can be caused by a race condition
                    }
                }
            }
            else
            {
                urlMapping.Requests += 1;
                urlMapping.RequestedOn = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(referrer))
                {
                    urlMapping.Referrer = referrer;
                }
                urlMapping = await UpdateUrlMappingAsync(urlMapping);
            }
            return urlMapping;
        }

        public async Task DeleteUrlMappingAsync(int urlMappingId)
        {
            var store = NewStore();
            var urlMapping = await store.GetByIdAsync(urlMappingId);
            await store.DeleteAsync(urlMapping);
        }

        /// <summary>
        /// The nightly purge of uncaptured broken links, run by <c>PurgeJob</c> per site.
        /// </summary>
        /// <remarks>
        /// <b>The same named cost regression as <c>VisitorRepository.DeleteVisitorsAsync</c>:</b> the harvested
        /// <c>RemoveRange</c> was one round trip per 100-row batch, and the seam has no batch delete, so this
        /// is one <see cref="IUrlMappingStore.DeleteAsync"/> per row. The read cap and the ordering stay in the
        /// database. Background job, retention window — latency nobody waits on, but a real regression, named.
        /// <para>
        /// Note the predicate keeps <c>string.IsNullOrEmpty(MappedUrl)</c>: the purge deletes only UNMAPPED
        /// rows. Lose that arm and the nightly job starts deleting the admin's configured REDIRECTS.
        /// </para>
        /// </remarks>
        public async Task<int> DeleteUrlMappingsAsync(int siteId, int age)
        {
            var count = 0;
            var purgedate = DateTime.UtcNow.AddDays(-age);

            // ONE store for the whole purge, not one per row: every batch deletes DISTINCT ids, so there is no
            // identity-map conflict to avoid here, and a store per row would hold a context per deleted row
            // open until the scope ends. This is exactly the harvested lifetime — one context per METHOD.
            var store = NewStore();

            var urlMappings = await ReadPurgeBatchAsync(siteId, purgedate);
            while (urlMappings.Count > 0)
            {
                count += urlMappings.Count;
                foreach (var urlMapping in urlMappings)
                {
                    await store.DeleteAsync(urlMapping);
                }
                urlMappings = await ReadPurgeBatchAsync(siteId, purgedate);
            }

            return count;
        }

        private async Task<List<UrlMappingAggregate>> ReadPurgeBatchAsync(int siteId, DateTime purgedate) =>
            await QueryAsync(session => session.Query<UrlMappingAggregate>()
                .Where(item => item.SiteId == siteId && string.IsNullOrEmpty(item.MappedUrl) && item.RequestedOn < purgedate)
                .OrderBy(item => item.RequestedOn)
                .Take(100)
                .ToList());
    }
}
