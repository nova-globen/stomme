using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The per-aggregate store for <see cref="SiteAggregate"/> — the framework store's Site cluster on the
    /// persistence seam (D3/D7). Provider-selected: <see cref="EfSiteStore"/> today, a RavenDB
    /// implementation of the same contract later.
    /// </summary>
    /// <remarks>
    /// A per-aggregate interface rather than the open-generic <see cref="IRepository{TAggregate, TId}"/>
    /// for two reasons. It is the shape D7 mandates ("existing per-aggregate repository interfaces,
    /// provider-selected implementations"). It also avoids a live collision: <c>AddFeatureManagement</c>
    /// calls <c>AddEfCorePersistence&lt;FeatureManagementDbContext&gt;</c>, which registers the
    /// open-generic <c>IRepository&lt;,&gt;</c> AND <c>DbContext</c> against the feature module's own
    /// SQLite store. Registering a second open-generic <c>IRepository&lt;,&gt;</c> or <c>DbContext</c> in
    /// this host would be last-wins and would silently re-point the feature flags at the tenant database.
    /// Do not do it; give each framework-store aggregate its own closed interface.
    /// </remarks>
    public interface ISiteStore : IRepository<SiteAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="PageAggregate"/> — the framework store's Page cluster on the
    /// persistence seam (D3/D7/D19). Closed, not the open-generic <see cref="IRepository{TAggregate, TId}"/>,
    /// for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    public interface IPageStore : IRepository<PageAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="ModuleAggregate"/> — the framework store's Module cluster on
    /// the persistence seam (D3/D7/D21). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    public interface IModuleStore : IRepository<ModuleAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="UserAggregate"/> — the framework store's User cluster on the
    /// persistence seam (D3/D7/D22). Closed, not the open-generic <see cref="IRepository{TAggregate, TId}"/>,
    /// for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// NOT named <c>IUserStore</c>: ASP.NET Identity's <c>IUserStore&lt;TUser&gt;</c> is live in this host
    /// (<c>AddIdentityCore&lt;IdentityUser&gt;().AddEntityFrameworkStores&lt;TenantDBContext&gt;()</c>) and
    /// every file that touches identity has <c>using Microsoft.AspNetCore.Identity;</c> in scope. This store
    /// is Stomme's OWN <c>User</c> table, which sits BESIDE Identity's <c>AspNetUsers</c>; the two are
    /// correlated by username in application code, never by a navigation. See src/Stomme.Server/README.md (persistence seam).
    /// </remarks>
    public interface IUserAccountStore : IRepository<UserAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="RoleAggregate"/> — the framework store's Role cluster on the
    /// persistence seam (D3/D7/D22). Closed, not the open-generic <see cref="IRepository{TAggregate, TId}"/>,
    /// for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// NOT named <c>IRoleStore</c> — ASP.NET Identity owns that name. See the remarks on
    /// <see cref="IUserAccountStore"/>.
    /// </remarks>
    public interface ISecurityRoleStore : IRepository<RoleAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="UserRoleAggregate"/> — the framework store's UserRole cluster on
    /// the persistence seam (D3/D7/D22). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    public interface IUserRoleAssignmentStore : IRepository<UserRoleAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SettingAggregate"/> in the TENANT database — the framework
    /// store's Setting cluster on the persistence seam (D3/D7/D23). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Settings for Site, Page, Module, User, Folder, Visitor and friends — everything
    /// <c>SettingRepository.IsMaster</c> answers false for. Its master-database twin is
    /// <see cref="IMasterSettingStore"/>; both persist the SAME <see cref="SettingAggregate"/> CLR type to
    /// the same table shape in two different databases. See src/Stomme.Server/README.md (persistence seam).
    /// </remarks>
    public interface ISettingStore : IRepository<SettingAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SettingAggregate"/> in the MASTER database — the installation-
    /// wide half of the Setting cluster (D3/D7/D23).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Host, Job, ModuleDefinition, Theme and <c>master:*</c> settings are installation-wide, not per-tenant,
    /// and the harvested <c>SettingRepository</c> has always routed them to <see cref="MasterDBContext"/>
    /// (<c>IsMaster(entityName)</c>). That routing is application behaviour, so it stays in the repository;
    /// this store exists so the master half of the table is on the seam too. Without it, a RavenDB provider
    /// could serve a site's settings but not the Host's — and Host settings are read on effectively every
    /// request.
    /// </para>
    /// <para>
    /// A SEPARATE interface rather than a store-name parameter on <see cref="ISettingStore"/>, because
    /// <see cref="IRepository{TAggregate, TId}"/> has no store-name notion and the two differ by DATABASE
    /// (different connection, different context, different migration set), not by table.
    /// </para>
    /// </remarks>
    public interface IMasterSettingStore : IRepository<SettingAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="PermissionAggregate"/> — the framework store's Permission
    /// cluster on the persistence seam (D3/D7/D24). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE store, not two: unlike <see cref="ISettingStore"/>/<see cref="IMasterSettingStore"/>, Permission
    /// lives in the TENANT database only. <see cref="MasterDBContext"/> never mapped it and the master
    /// migration set never created the table. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// This store — NOT <see cref="IPermissionRepository"/> — is what <c>RoleRepository.DeleteRole</c> and
    /// <c>UserRepository.DeleteUser</c> use to cascade their permission cleanups. That is deliberate and it
    /// is the whole reason the cluster's one outbound edge does not close a cycle:
    /// <c>PermissionRepository</c> takes <see cref="IRoleRepository"/>, so a <c>RoleRepository</c> that took
    /// <see cref="IPermissionRepository"/> back would deadlock the container. A STORE has no repository edges
    /// at all (context factory + tracker + tenant context), so depending on it is always acyclic. See
    /// src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// </remarks>
    public interface IPermissionStore : IRepository<PermissionAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="LanguageAggregate"/> — the framework store's Language cluster on
    /// the persistence seam (D3/D7/D44). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// One of D44's five LEAF clusters: no repository edges out, no navigations either way, one table, tenant
    /// -side only. Like <see cref="IPermissionStore"/> and unlike <see cref="ISettingStore"/> there is no
    /// master twin — <see cref="MasterDBContext"/> never mapped Language.
    /// </remarks>
    public interface ILanguageStore : IRepository<LanguageAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="LogAggregate"/> — the framework store's Log cluster on the
    /// persistence seam (D3/D7/D44). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// NOT named against <c>ILogManager</c>/<c>ILogRepository</c>'s shape: this is the <c>Log</c> TABLE, which
    /// <see cref="ILogRepository"/> appends to and purges from and never updates. Its aggregate is the one
    /// framework-store type with no audit columns — see <see cref="LogAggregate"/>.
    /// </remarks>
    public interface ILogStore : IRepository<LogAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="ProfileAggregate"/> — the framework store's Profile cluster on
    /// the persistence seam (D3/D7/D44). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// The Profile PROPERTY DEFINITIONS, not a user's values for them — the values are Settings (D23) under
    /// <c>EntityNames.User</c>. Note <see cref="SiteRepository"/> takes <see cref="IProfileRepository"/> to
    /// seed a new site's defaults; that edge is inbound only and this cluster takes no repository, so it
    /// cannot close a cycle (D22 rule 8). See <see cref="ProfileAggregate"/>.
    /// </remarks>
    public interface IProfileStore : IRepository<ProfileAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SiteTaskAggregate"/> — the framework store's SiteTask cluster on
    /// the persistence seam (D3/D7/D44). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    public interface ISiteTaskStore : IRepository<SiteTaskAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="NotificationAggregate"/> — the framework store's Notification
    /// cluster on the persistence seam (D3/D7/D44). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// The first <c>IDeletable</c> aggregate on the seam — see <see cref="NotificationAggregate"/> for why
    /// that interface is load-bearing and for the provider divergence it inherits.
    /// </remarks>
    public interface INotificationStore : IRepository<NotificationAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SiteGroupAggregate"/> — the framework store's SiteGroup cluster
    /// on the persistence seam (D3/D7/D45). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Tenant-side only, like <see cref="IPermissionStore"/> and unlike <see cref="ISettingStore"/> — there is
    /// no master twin. Landed in the SAME increment as <see cref="ISiteGroupMemberStore"/> and could not have
    /// landed without it: <c>SiteGroupMember.SiteGroup</c> was a mapped navigation into this aggregate's
    /// contract, and while it stayed mapped EF rediscovered <c>SiteGroup</c> through it and broke the model at
    /// startup. See <see cref="SiteGroupAggregate"/>.
    /// </remarks>
    public interface ISiteGroupStore : IRepository<SiteGroupAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SiteGroupMemberAggregate"/> — the framework store's
    /// SiteGroupMember cluster on the persistence seam (D3/D7/D45). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// The other half of the pair (see <see cref="ISiteGroupStore"/>). Its aggregate references the group by id
    /// only; the navigation the harvested entity carried is severed, and
    /// <see cref="SiteGroupMemberRepository"/> re-populates the contract's <c>SiteGroup</c> member from a
    /// second query rather than from a database join — the harvested <c>.Include</c> had no join predicate, so
    /// there was none to lose (D22 rule 9).
    /// </remarks>
    public interface ISiteGroupMemberStore : IRepository<SiteGroupMemberAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="VisitorAggregate"/> — the framework store's Visitor cluster on
    /// the persistence seam (D3/D7/D47). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Tenant-side only, like <see cref="IPermissionStore"/> and unlike <see cref="ISettingStore"/>. Its
    /// reference into the User aggregate was already severed by D22, so unlike the SiteGroup pair (D45) this
    /// cluster had no navigation left to break. It IS on the render path — <c>App.razor</c>'s
    /// <c>TrackVisitorAsync</c> reads and writes a visitor on every request of a site with VisitorTracking on.
    /// </remarks>
    public interface IVisitorStore : IRepository<VisitorAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="UrlMappingAggregate"/> — the framework store's UrlMapping
    /// cluster on the persistence seam (D3/D7/D47). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Tenant-side only. A leaf in the schema but NOT in the object graph:
    /// <see cref="UrlMappingRepository"/> takes <see cref="ISiteRepository"/> to read
    /// <c>Site.CaptureBrokenUrls</c>, and that edge is acyclic because NO repository anywhere takes
    /// <see cref="IUrlMappingRepository"/> back — only controllers, Razor pages and PurgeJob consume it (D22
    /// rule 8). On the render path via <c>App.razor</c>'s 404 handler. See
    /// <see cref="UrlMappingAggregate"/> for the unique index this cluster's production code depends on.
    /// </remarks>
    public interface IUrlMappingStore : IRepository<UrlMappingAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="PageModuleAggregate"/> — the framework store's PageModule
    /// cluster on the persistence seam (D3/D7/D47). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on
    /// <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tenant-side only, and <b>the most render-critical cluster on the seam</b>: every page render enumerates
    /// its page's module instances. D19 and D21 deliberately left it behind while taking Page and Module,
    /// severing its two navigations to ids and leaving the row itself in <c>TenantDBContext</c>; this store is
    /// what finally moves the row.
    /// </para>
    /// <para>
    /// The one aggregate that is BOTH <see cref="IAuditable"/> and <see cref="IDeletable"/> — see
    /// <see cref="PageModuleAggregate"/>.
    /// </para>
    /// </remarks>
    public interface IPageModuleStore : IRepository<PageModuleAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="FolderAggregate"/> — the framework store's Folder cluster on the
    /// persistence seam (D3/D7/D48). Closed, not the open-generic <see cref="IRepository{TAggregate, TId}"/>,
    /// for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tenant-side only, like <see cref="IPermissionStore"/> and unlike <see cref="ISettingStore"/>. On the
    /// render path: <c>Pages/Files.cshtml.cs</c> resolves a folder to serve every static file a site publishes.
    /// </para>
    /// <para>
    /// <b>Landed WITHOUT its File half, and that asymmetry is the whole story of D48.</b> The two were meant to
    /// land together, because <c>File.Folder</c> was a mapped navigation into this aggregate's contract and
    /// while it stayed mapped EF rediscovered <c>Folder</c> through it and broke the model at startup — the
    /// D45 mechanism, OBSERVED here. So the navigation IS severed and <see cref="FileRepository"/> stitches the
    /// contract's Folder member on by hand. But the FILE ROW did not move: <c>FileRepository</c>'s two
    /// name lookups filter with <c>.ToLower()</c> on the COLUMN, and RavenDB's LINQ provider cannot translate
    /// it — it throws <c>NotSupportedException</c> rather than returning a wrong answer. There is no portable
    /// spelling of a case-insensitive column filter on this seam, so moving File would have made the cluster
    /// crash on any RavenDB host. See <c>src/Stomme.Server/README.md (persistence seam)</c> and D48.
    /// </para>
    /// <para>
    /// <b>D49 lifted that blocker and <see cref="IFileStore"/> now exists</b>, so the paragraph above is
    /// history rather than current state — kept because it is the reason this cluster's
    /// <see cref="FolderAggregate.PathNormalized"/> column exists at all. The blocker was not removed by
    /// making <c>.ToLower()</c> translate; it was removed by normalizing the DATA so the query never needs to.
    /// </para>
    /// </remarks>
    public interface IFolderStore : IRepository<FolderAggregate, int>
    {
    }

    /// <summary>
    /// The File cluster (D49) — the last framework cluster to cross the seam, and the one D48 escalated.
    /// Tenant-side only, so there is no master twin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it was late.</b> D48 measured a provider disagreement with no portable spelling: this cluster's
    /// two name lookups folded case on the COLUMN (<c>item.Name.ToLower() == fileName</c>), which the
    /// relational providers translate and RavenDB THROWS on; and the portable <c>==</c> means case-SENSITIVE
    /// on relational and case-INSENSITIVE on RavenDB. Keep the fold and File crashes on every RavenDB host;
    /// drop it and File 404s every mixed-case file on every relational host. That is D22 rule 9's escalate
    /// condition, and it was escalated rather than papered over.
    /// </para>
    /// <para>
    /// <b>How D49 resolved it: normalize the DATA, do not translate the QUERY.</b>
    /// <see cref="FileAggregate.NameNormalized"/> and <see cref="FolderAggregate.PathNormalized"/> store a
    /// lowercase copy alongside the stored case, and the reads filter them with plain <c>==</c>. Both
    /// providers agree because for two lowercase strings ordinal and case-insensitive equality are the same
    /// relation. This is ASP.NET Identity's <c>NormalizedUserName</c> split, already running in this codebase.
    /// The rejected alternative — teaching <c>RavenLinqRewriting</c> to fold <c>.ToLower()</c> — was
    /// correct-but-conditional: its equivalence depends on every caller passing a lowercase right-hand side, a
    /// convention the seam cannot see or enforce.
    /// </para>
    /// <para>
    /// Note <see cref="Models.File.Folder"/> was already severed to <c>[NotMapped]</c> by D48, so this
    /// interface's arrival needed no further model surgery.
    /// </para>
    /// </remarks>
    public interface IFileStore : IRepository<FileAggregate, int>
    {
    }

    // --- The SearchContent cluster (D50). Four tables, tenant-side only, no master half. ---

    /// <summary>
    /// The per-aggregate store for <see cref="SearchContentAggregate"/> — the head of the search cluster
    /// (D50). Closed, not the open-generic <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons
    /// documented on <see cref="ISiteStore"/>.
    /// </summary>
    public interface ISearchContentStore : IRepository<SearchContentAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SearchContentPropertyAggregate"/> (D50) — name/value metadata
    /// hung off one SearchContent. Its navigation from <c>SearchContent</c> is <c>[NotMapped]</c> (D45).
    /// </summary>
    public interface ISearchContentPropertyStore : IRepository<SearchContentPropertyAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SearchContentWordAggregate"/> (D50) — the term-frequency half of
    /// the inverted index. Both of its contract navigations are <c>[NotMapped]</c> (D45).
    /// </summary>
    public interface ISearchContentWordStore : IRepository<SearchContentWordAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="SearchWordAggregate"/> (D50) — the distinct-word dictionary.
    /// </summary>
    public interface ISearchWordStore : IRepository<SearchWordAggregate, int>
    {
    }

    /// <summary>
    /// <b>The SEARCH READ (D50) — the seam's one closed contract that is NOT a repository, because the read it
    /// serves is not expressible as a portable LINQ query.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other cluster reads through <c>IQuerySession.Query&lt;T&gt;()</c> (D19), which hands back a bare
    /// <see cref="IQueryable{T}"/> that both providers translate. This read cannot: it filters
    /// <c>SearchWord.Word</c> by PREFIX and sums <c>SearchContentWord.Count</c> per content — an aggregation
    /// over a subset chosen at RUNTIME by the caller's keywords. RavenDB serves a dynamic
    /// <c>GroupBy</c>+<c>Sum</c> only while every filter arm touches the group-by key, and this one does not
    /// (measured: <c>InvalidQueryException: Field 'Word' is neither an aggregation operation nor part of the
    /// group by key</c>). So the seam's read surface cannot express it, and pretending otherwise is what D50
    /// stopped doing.
    /// </para>
    /// <para>
    /// <b>The implementations are deliberately NOT alike, and the observable behaviour is IDENTICAL.</b> The EF
    /// half keeps today's query verbatim — <c>GROUP BY</c> + <c>SUM</c> stays in SQL over the four inverted-index
    /// tables. The RavenDB half does the same work with a query-time FACET (<c>AggregateBy</c>/<c>SumOn</c>)
    /// over a static <c>LoadDocument</c> index, which aggregates over the matched result set rather than under
    /// a reduce key fixed at index-definition time. Both keep EVERY filter arm AND the sum in the store.
    /// </para>
    /// <para>
    /// <b>The rejected mechanisms, both measured, both wrong.</b> A MAP-REDUCE index aggregates at INDEX time
    /// under a fixed reduce key, so it cannot sum over a keyword-chosen subset: reduce by
    /// <c>(SearchContentId, Word)</c> and it has re-derived <c>SearchContentWord</c>; reduce by
    /// <c>SearchContentId</c> and it yields the total frequency of EVERY word in the document, which is the
    /// wrong number. A NATIVE FULL-TEXT index (<c>FieldIndexing.Search</c>) is not the same relation as
    /// <c>StartsWith</c> in EITHER direction — its analyzer tokenizes <c>state-of-the-art</c>, which
    /// <c>FormatContent</c> does not, so it MISSES <c>state-of*</c> and INVENTS <c>art*</c>. The index behind
    /// the RavenDB half therefore uses the DEFAULT analyzer on the word field. See D50 §3.
    /// </para>
    /// <para>
    /// <b>Ordering is deliberately not part of this contract.</b> The harvested query had no <c>OrderBy</c> and
    /// <c>SearchService</c> sorts and pages entirely in memory over the returned set, so the store's row order
    /// is not observable. What must match across providers is the SET and the per-content <c>Count</c> —
    /// which is what the conformance suite asserts.
    /// </para>
    /// </remarks>
    public interface ISearchQueryStore
    {
        /// <summary>
        /// The search read. Returns one <see cref="SearchContent"/> per matching content with
        /// <c>Count</c> populated from the summed term frequency of the MATCHED words. Unordered.
        /// </summary>
        Task<IReadOnlyList<SearchContent>> SearchAsync(SearchQuery searchQuery, CancellationToken ct = default);
    }

    /// <summary>
    /// The per-aggregate store for <see cref="JobAggregate"/> in the MASTER database — the framework store's Job
    /// cluster on the persistence seam (D52). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// A MASTER store like <see cref="IMasterSettingStore"/> and unlike <see cref="ISettingStore"/>: Job is
    /// installation-wide, so it is bound to <see cref="MasterDBContext"/> (EF) / the master RavenDB database, and
    /// its reads go through <see cref="IMasterStoreSessionFactory"/>. Unlike the Setting cluster there is NO
    /// tenant twin — Job lives in the master database ONLY. The job scheduler reads and writes it from a
    /// background scope with no request and no tenant.
    /// </remarks>
    public interface IJobStore : IRepository<JobAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="JobLogAggregate"/> in the MASTER database — the framework store's
    /// JobLog cluster on the persistence seam (D52). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// The other half of the Job/JobLog pair, and MASTER-only like <see cref="IJobStore"/>. Its aggregate
    /// references the job by id; the <c>JobLog.Job</c> navigation the harvested entity carried is severed
    /// (<c>[NotMapped]</c>, D45's mechanism), and <see cref="JobLogRepository"/> re-populates the contract's
    /// <c>Job</c> member from a second master-session query rather than an eager <c>.Include</c>.
    /// </remarks>
    public interface IJobLogStore : IRepository<JobLogAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="ThemeAggregate"/> in the MASTER database — the framework store's
    /// Theme cluster on the persistence seam (D52). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// A MASTER store like <see cref="IJobStore"/> and <see cref="IMasterSettingStore"/>, and unlike
    /// <see cref="ISettingStore"/>: Theme is installation-wide, so it is bound to <see cref="MasterDBContext"/>
    /// (EF) / the master RavenDB database, and its reads go through <see cref="IMasterStoreSessionFactory"/>.
    /// There is NO tenant twin — Theme lives in the master database ONLY. The Theme table itself carries just
    /// four columns (theme_name, name, the version string and the audit quartet); everything else on the Theme
    /// contract is <c>[NotMapped]</c> runtime state the repository rebuilds from the loaded theme assemblies and
    /// from Settings/Permissions. See <see cref="ThemeAggregate"/>.
    /// </remarks>
    public interface IThemeStore : IRepository<ThemeAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="ModuleDefinitionAggregate"/> in the MASTER database — the
    /// framework store's ModuleDefinition cluster on the persistence seam (D52). Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// A MASTER store like <see cref="IThemeStore"/>, <see cref="IJobStore"/> and <see cref="IMasterSettingStore"/>,
    /// and unlike <see cref="ISettingStore"/>: ModuleDefinition is installation-wide — the registry of installed
    /// module types, read during module discovery at startup — so it is bound to <see cref="MasterDBContext"/>
    /// (EF) / the master RavenDB database, and its reads go through <see cref="IMasterStoreSessionFactory"/>.
    /// There is NO tenant twin — ModuleDefinition lives in the master database ONLY. The table carries just six
    /// columns (module_definition_name, name, description, categories, the version string and the audit quartet);
    /// everything else on the ModuleDefinition contract is <c>[NotMapped]</c> runtime state the repository
    /// rebuilds from the loaded module assemblies and from Settings/Permissions. See
    /// <see cref="ModuleDefinitionAggregate"/>. Note there is no mapped navigation into this aggregate anywhere:
    /// <c>Module.ModuleDefinition</c> is <c>[NotMapped]</c> and, being tenant-side, could not carry a cross-database
    /// foreign key even in principle — so unlike the SiteGroup (D45) and Folder/File (D48/D49) pairs there was no
    /// navigation to sever.
    /// </remarks>
    public interface IModuleDefinitionStore : IRepository<ModuleDefinitionAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="TenantAggregate"/> in the MASTER database — the framework store's Tenant
    /// cluster on the persistence seam (D52), and half of the routing core. Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// A MASTER store like <see cref="IThemeStore"/>, <see cref="IJobStore"/> and <see cref="IMasterSettingStore"/>,
    /// and unlike <see cref="ISettingStore"/>: Tenant is installation-wide — the registry of physical tenant databases,
    /// read on every request to resolve the current tenant and select its persistence provider (<c>Tenant.DBType</c>).
    /// So it is bound to <see cref="MasterDBContext"/> (EF) / the master RavenDB database, and its reads go through
    /// <see cref="IMasterStoreSessionFactory"/>. There is NO tenant twin — Tenant lives in the master database ONLY.
    /// The <c>version</c> column is a STRING carried by <see cref="TenantAggregate.TenantVersion"/> past the
    /// <see cref="AggregateRoot{TId}.Version"/> concurrency-token shadow; <c>db_type</c> (the provider-selector) is
    /// absent from the builder but present in the live table (D52 survey, MEASURED). This is a landed-TOGETHER pair
    /// with <see cref="IAliasStore"/>: the installer creates the Tenant then its Aliases, and the resolver maps
    /// Alias -> Site -> Tenant on every request. There is no mapped navigation between them — Alias references the
    /// tenant by id only, so unlike the SiteGroup (D45) or Folder/File (D48/D49) pairs there was no navigation to sever.
    /// </remarks>
    public interface ITenantStore : IRepository<TenantAggregate, int>
    {
    }

    /// <summary>
    /// The per-aggregate store for <see cref="AliasAggregate"/> in the MASTER database — the framework store's Alias
    /// cluster on the persistence seam (D52), and the other half of the routing core. Closed, not the open-generic
    /// <see cref="IRepository{TAggregate, TId}"/>, for exactly the reasons documented on <see cref="ISiteStore"/>.
    /// </summary>
    /// <remarks>
    /// A MASTER store like <see cref="ITenantStore"/>: Alias is installation-wide routing state — the map from an
    /// incoming host+path to a Site and a Tenant — so it is bound to <see cref="MasterDBContext"/> (EF) / the master
    /// RavenDB database, and its reads go through <see cref="IMasterStoreSessionFactory"/>. There is NO tenant twin —
    /// Alias lives in the master database ONLY. It has no <c>version</c> column at all (unlike Tenant/Theme/
    /// ModuleDefinition), so there is no concurrency-token shadow to carry, and <c>is_default</c> is absent from the
    /// builder but present in the live table (D52 survey, MEASURED). <b>The hostname lookup that resolves a request
    /// to a site (<c>AliasRepository.GetAliasAsync(url)</c>) runs ENTIRELY IN MEMORY over the cached alias list with
    /// <c>OrdinalIgnoreCase</c>, never a translated query</b>, so the EF-vs-RavenDB string-equality divergence
    /// (D48/D49) does not reach it — the stored name is lower-cased on write besides.
    /// </remarks>
    public interface IAliasStore : IRepository<AliasAggregate, int>
    {
    }

    /// <summary>
    /// Opens sessions over the framework store (the tenant database) for read queries —
    /// <c>IQuerySession.Query&lt;SiteAggregate&gt;()</c>. Distinct from the ambient
    /// <see cref="IDocumentSessionFactory"/>, which <c>AddFeatureManagement</c> already owns for its own
    /// store; see the remarks on <see cref="ISiteStore"/>.
    /// </summary>
    public interface IFrameworkStoreSessionFactory : IDocumentSessionFactory
    {
    }

    /// <summary>
    /// Opens sessions over the MASTER database for read queries — the read half of
    /// <see cref="IMasterSettingStore"/>, and the only session factory bound to
    /// <see cref="MasterDBContext"/>. Reads go through this rather than the store for the D19 reason
    /// documented on <c>SettingRepository</c>.
    /// </summary>
    public interface IMasterStoreSessionFactory : IDocumentSessionFactory
    {
    }
}
