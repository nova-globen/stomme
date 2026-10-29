#nullable enable annotations // project is nullable-disabled; make the string?/TenantId? annotations legal (CS8632) without turning on flow analysis
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Raven.Client.Documents;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The RavenDB half of the framework store: the same closed store interfaces the EF Core half
    /// implements, over <see cref="RavenRepository{TAggregate, TId}"/>.
    /// <para>
    /// <b>No count is stated here on purpose.</b> This comment used to say "the same eight closed store
    /// interfaces" and was wrong for as long as D24's Permission cluster went un-twinned — a hand-counted
    /// comment drifts exactly when the code does, and reassures while it drifts. The set is whatever
    /// <c>ISiteStore.cs</c> declares; <c>RavenFrameworkStoreRegistrationTests</c> discovers it reflectively
    /// and holds BOTH providers to it, which is the only statement of the count that cannot go stale.
    /// </para>
    /// <para>
    /// Each store is a thin binding of one aggregate to one RavenDB database, mirroring
    /// <see cref="EfSiteStore"/>'s binding of one aggregate to one <see cref="TenantDBContext"/>. The
    /// substance lives in the provider; what these types contribute is the CLOSED interface — see the remarks
    /// on <see cref="ISiteStore"/> for why an open-generic registration is forbidden in this host.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>No per-instance context to own.</b> The EF stores create a <see cref="TenantDBContext"/> per store
    /// instance because a cached context would bind to the first tenant's connection and silently serve
    /// tenant #1's data to every later tenant. RavenDB's <c>IDocumentStore</c> has no such state: it is a
    /// long-lived, thread-safe connection factory, and <see cref="RavenRepository{TAggregate, TId}"/> opens a
    /// session per operation, resolving the database through <see cref="IRavenTenantDatabaseResolver"/> at
    /// that moment. So these stores hold no disposable state and are tenant-correct for the same reason the
    /// EF ones are — a fresh session per operation, never a cached one.
    /// </remarks>
    internal sealed class RavenSiteStore : RavenFrameworkStore<SiteAggregate>, ISiteStore
    {
        public RavenSiteStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>The RavenDB <see cref="IPageStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenPageStore : RavenFrameworkStore<PageAggregate>, IPageStore
    {
        public RavenPageStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>The RavenDB <see cref="IModuleStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenModuleStore : RavenFrameworkStore<ModuleAggregate>, IModuleStore
    {
        public RavenModuleStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>The RavenDB <see cref="IUserAccountStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenUserAccountStore : RavenFrameworkStore<UserAggregate>, IUserAccountStore
    {
        public RavenUserAccountStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>The RavenDB <see cref="ISecurityRoleStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenSecurityRoleStore : RavenFrameworkStore<RoleAggregate>, ISecurityRoleStore
    {
        public RavenSecurityRoleStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>The RavenDB <see cref="IUserRoleAssignmentStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenUserRoleAssignmentStore : RavenFrameworkStore<UserRoleAggregate>, IUserRoleAssignmentStore
    {
        public RavenUserRoleAssignmentStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="ISettingStore"/> — the TENANT half of the Setting cluster. Bound to the
    /// framework database, exactly as <see cref="EfSettingStore"/> is bound to <see cref="TenantDBContext"/>.
    /// </summary>
    internal sealed class RavenSettingStore : RavenFrameworkStore<SettingAggregate>, ISettingStore
    {
        public RavenSettingStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IMasterSettingStore"/> — the MASTER half of the Setting cluster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same aggregate, a different database — and that is the whole point.</b> The relational half
    /// expresses this by handing <see cref="EfMasterSettingStore"/> a <see cref="MasterDBContext"/> instead of
    /// a <see cref="TenantDBContext"/>. RavenDB has no context to swap, so the database is pinned instead:
    /// this store overrides tenant routing with <see cref="FixedRavenDatabaseResolver"/> so every session it
    /// opens lands on the master database regardless of the ambient tenant. Without that override the master
    /// settings — Host, Job, ModuleDefinition, Theme, <c>master:*</c> — would be read from and written to the
    /// CURRENT TENANT's database, which is silent: the documents would store and load happily, just in the
    /// wrong database and invisible to every other tenant.
    /// </para>
    /// </remarks>
    internal sealed class RavenMasterSettingStore : RavenFrameworkStore<SettingAggregate>, IMasterSettingStore
    {
        public RavenMasterSettingStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IPermissionStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <b>Bound to the framework database, with no master twin</b> — Permission is tenant-side only, so
    /// unlike <see cref="RavenMasterSettingStore"/> there is no <see cref="FixedRavenDatabaseResolver"/> here
    /// and tenant routing applies normally. Added by D42: the cluster was migrated after the RavenDB
    /// provider was written, and the twin was never added — <c>AddRavenFrameworkStore</c> registered eight
    /// stores against the EF provider's nine, so any host on the RavenDB path threw on the first
    /// <c>PermissionRepository</c> construction. The registration gate is reflective now precisely so the
    /// next cluster cannot repeat it.
    /// </remarks>
    internal sealed class RavenPermissionStore : RavenFrameworkStore<PermissionAggregate>, IPermissionStore
    {
        public RavenPermissionStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="ILanguageStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Bound to the framework database with no master twin — one of D44's five tenant-side leaf clusters, so
    /// tenant routing applies normally and there is no <see cref="FixedRavenDatabaseResolver"/> here.
    /// </remarks>
    internal sealed class RavenLanguageStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<LanguageAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ILanguageStore;

    /// <summary>
    /// The RavenDB <see cref="ILogStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="LogAggregate"/> is the one framework-store aggregate with no audit columns, which this
    /// provider handles for free: <see cref="AuditStamp.IsAuditable"/> requires all four members and finds
    /// none, so the store-level audit listener correctly declines to stamp it — the same answer
    /// <c>DbContextUtils</c>' <c>is IAuditable</c> test gives on the relational side.
    /// </remarks>
    internal sealed class RavenLogStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<LogAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ILogStore;

    /// <summary>
    /// The RavenDB <see cref="IProfileStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    internal sealed class RavenProfileStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<ProfileAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          IProfileStore;

    /// <summary>
    /// The RavenDB <see cref="ISiteTaskStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    internal sealed class RavenSiteTaskStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SiteTaskAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISiteTaskStore;

    /// <summary>
    /// The RavenDB <see cref="INotificationStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <b>RESOLVED in D59 — this comment previously described a divergence that no longer exists.</b>
    /// <see cref="NotificationAggregate"/> is <c>IDeletable</c>, and the <c>DeletedBy</c>/<c>DeletedOn</c>
    /// stamp now AGREES across providers: <see cref="AuditStamp"/> ports it (<c>StampDeleted</c>), detecting
    /// the transition from the entity's own state (<c>IsDeleted == true &amp;&amp; DeletedOn == null</c>)
    /// rather than from a database load — which is why the "a document provider cannot evaluate that without
    /// an extra load" blocker cited by the original deferral did not hold. A Notification soft-deleted through
    /// this store records who and when, matching EF's observable outcome; pinned by a driven test. See D59 and
    /// the remarks on <see cref="NotificationAggregate"/>.
    /// </remarks>
    internal sealed class RavenNotificationStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<NotificationAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          INotificationStore;

    /// <summary>
    /// The RavenDB <see cref="ISiteGroupStore"/>. See <see cref="RavenFrameworkStore{TAggregate}"/>.
    /// </summary>
    /// <remarks>
    /// The severed navigation costs this provider nothing and arguably suits it: the aggregate references the
    /// group by id, which is the shape a document store wants anyway. Note what does NOT carry over — the
    /// relational unique index on (site_id, site_group_id) is a DATABASE constraint, so on RavenDB a duplicate
    /// membership is not rejected. That divergence is inherited from the seam (the same is true of every
    /// unique index on every migrated cluster), not introduced here; see src/Stomme.Persistence.RavenDb/README.md.
    /// </remarks>
    internal sealed class RavenSiteGroupStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SiteGroupAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISiteGroupStore;

    /// <summary>
    /// The RavenDB <see cref="ISiteGroupMemberStore"/>. See <see cref="RavenFrameworkStore{TAggregate}"/>
    /// and the remarks on <see cref="RavenSiteGroupStore"/> for the unique-index divergence.
    /// </summary>
    internal sealed class RavenSiteGroupMemberStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SiteGroupMemberAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISiteGroupMemberStore;

    /// <summary>The RavenDB <see cref="IVisitorStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenVisitorStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<VisitorAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          IVisitorStore;

    /// <summary>
    /// The RavenDB <see cref="IUrlMappingStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Note what does NOT carry over: the relational UNIQUE index on (site_id, url). On the relational
    /// providers that index IS the concurrency control for two simultaneous requests to the same broken URL —
    /// <c>UrlMappingRepository.GetUrlMappingAsync</c> catches the duplicate-key exception it raises and moves
    /// on. RavenDB raises nothing, so the race inserts two documents. Inherited from the seam, not introduced
    /// here; see <see cref="UrlMappingAggregate"/> and src/Stomme.Persistence.RavenDb/README.md.
    /// </remarks>
    internal sealed class RavenUrlMappingStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<UrlMappingAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          IUrlMappingStore;

    /// <summary>
    /// The RavenDB <see cref="IPageModuleStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <b>RESOLVED in D59</b> — as for <see cref="RavenNotificationStore"/>, the IDeletable stamp no longer
    /// diverges: <c>AuditStamp</c> ports the soft-delete rule, so a PageModule soft-deleted through this store
    /// records <c>DeletedBy</c>/<c>DeletedOn</c> as well as <c>IsDeleted</c>. The IAuditable half applies too.
    /// See D59 and <see cref="PageModuleAggregate"/>.
    /// </remarks>
    internal sealed class RavenPageModuleStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<PageModuleAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          IPageModuleStore;

    /// <summary>
    /// The RavenDB <see cref="IFolderStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bound to the framework database with no master twin — Folder is tenant-side only, so tenant routing
    /// applies normally and there is no <see cref="FixedRavenDatabaseResolver"/> here.
    /// </para>
    /// <para>
    /// <b>Two divergences, both inherited and both named.</b> The relational UNIQUE index on (site_id, path) is
    /// a DATABASE constraint, so on RavenDB two folders may share a path within a site — the same shape as
    /// <see cref="RavenSiteGroupStore"/>'s and <see cref="RavenUrlMappingStore"/>'s. And RavenDB's string
    /// equality is CASE-INSENSITIVE by default, so <c>FolderRepository.GetFolderAsync(siteId, path)</c> —
    /// <c>item.Path == path</c> — matches <c>Users/</c> for a request for <c>users/</c> here and does not on the
    /// relational providers. That second one is NOT specific to this cluster: it applies to every string
    /// equality on the seam (Page.Path, User.Username, UrlMapping.Url), and it is measured rather than assumed
    /// — see src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// </remarks>
    internal sealed class RavenFolderStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<FolderAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          IFolderStore;

    /// <summary>
    /// The RavenDB <see cref="IFileStore"/>. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bound to the framework database with no master twin — File is tenant-side only, so tenant routing
    /// applies normally and there is no <see cref="FixedRavenDatabaseResolver"/> here.
    /// </para>
    /// <para>
    /// <b>This is the store D48 said could not exist</b>, and what changed is the DATA, not this class. The
    /// blocker was <c>FileRepository</c>'s <c>item.Name.ToLower() == fileName</c>, which this provider throws
    /// on. D49 stores a lowercase <see cref="FileAggregate.NameNormalized"/> alongside the stored case and
    /// filters THAT with plain <c>==</c> — an expression this provider has always understood. Note the
    /// equivalence relies on every stored value in that column actually being lowercase, which is why
    /// <see cref="FileAggregate.Name"/>'s setter derives it rather than trusting a caller.
    /// </para>
    /// <para>
    /// <b>One divergence, inherited and named:</b> the relational UNIQUE index on (folder_id, name) is a
    /// DATABASE constraint, so on RavenDB two files may share a name within a folder — the same shape as
    /// <see cref="RavenFolderStore"/>'s (site_id, path). Note this does NOT interact with the normalized
    /// column: the index is on <c>name</c>, not <c>name_normalized</c>, and D49 deliberately left it there, so
    /// <c>Logo.png</c> and <c>logo.png</c> coexist on relational exactly as they did before — which is why the
    /// render path's folder resolution fans in on a LIST rather than assuming one row.
    /// </para>
    /// <para>
    /// RavenDB's case-INSENSITIVE string equality — the divergence named on <see cref="RavenFolderStore"/> —
    /// is INERT for this cluster's own reads, and that is the point of D49 rather than an accident: every
    /// string this cluster filters on is already lowercase on both sides, so the two providers' equality
    /// relations coincide exactly. Asserted live on both in
    /// <c>A_normalized_column_filter_is_identical_on_every_provider</c>.
    /// </para>
    /// </remarks>
    internal sealed class RavenFileStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<FileAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          IFileStore;

    // The SearchContent cluster. Four stores; the search READ is ISearchQueryStore, not one of these.

    /// <summary>The RavenDB <see cref="ISearchContentStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenSearchContentStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SearchContentAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISearchContentStore;

    /// <summary>The RavenDB <see cref="ISearchContentPropertyStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenSearchContentPropertyStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SearchContentPropertyAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISearchContentPropertyStore;

    /// <summary>The RavenDB <see cref="ISearchContentWordStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenSearchContentWordStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SearchContentWordAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISearchContentWordStore;

    /// <summary>The RavenDB <see cref="ISearchWordStore"/>. See <see cref="RavenSiteStore"/>.</summary>
    internal sealed class RavenSearchWordStore(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator)
        : RavenFrameworkStore<SearchWordAggregate>(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator),
          ISearchWordStore;

    /// <summary>
    /// The RavenDB <see cref="IJobStore"/> — the MASTER side. See <see cref="RavenSiteStore"/>.
    /// </summary>
    /// <remarks>
    /// Pinned to the master database with <see cref="FixedRavenDatabaseResolver"/>, exactly as
    /// <see cref="RavenMasterSettingStore"/> is: Job is installation-wide, so the ambient tenant routing must not
    /// apply. Without the override a job would be written to the CURRENT TENANT's database and be invisible to
    /// the background scheduler, which runs with no tenant at all.
    /// </remarks>
    internal sealed class RavenJobStore : RavenFrameworkStore<JobAggregate>, IJobStore
    {
        public RavenJobStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IJobLogStore"/> — the MASTER side. See <see cref="RavenJobStore"/>.
    /// </summary>
    internal sealed class RavenJobLogStore : RavenFrameworkStore<JobLogAggregate>, IJobLogStore
    {
        public RavenJobLogStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IThemeStore"/> — the MASTER side. See <see cref="RavenJobStore"/>.
    /// </summary>
    /// <remarks>
    /// Pinned to the master database with <see cref="FixedRavenDatabaseResolver"/>, exactly as
    /// <see cref="RavenJobStore"/> and <see cref="RavenMasterSettingStore"/> are: Theme is installation-wide, so
    /// the ambient tenant routing must not apply. Without the override a theme would be written to the CURRENT
    /// TENANT's database and be invisible to every other tenant and to the installer's own theme sync.
    /// </remarks>
    internal sealed class RavenThemeStore : RavenFrameworkStore<ThemeAggregate>, IThemeStore
    {
        public RavenThemeStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IModuleDefinitionStore"/> — the MASTER side. See <see cref="RavenThemeStore"/>.
    /// </summary>
    /// <remarks>
    /// Pinned to the master database with <see cref="FixedRavenDatabaseResolver"/>, exactly as
    /// <see cref="RavenThemeStore"/>, <see cref="RavenJobStore"/> and <see cref="RavenMasterSettingStore"/> are:
    /// ModuleDefinition is installation-wide, so the ambient tenant routing must not apply. Without the override a
    /// module definition would be written to the CURRENT TENANT's database and be invisible to every other tenant
    /// and to the installer's own module-discovery sync.
    /// </remarks>
    internal sealed class RavenModuleDefinitionStore : RavenFrameworkStore<ModuleDefinitionAggregate>, IModuleDefinitionStore
    {
        public RavenModuleDefinitionStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="ITenantStore"/> — the MASTER side, half of the routing core. See
    /// <see cref="RavenThemeStore"/>.
    /// </summary>
    /// <remarks>
    /// Pinned to the master database with <see cref="FixedRavenDatabaseResolver"/>, exactly as
    /// <see cref="RavenThemeStore"/>, <see cref="RavenJobStore"/> and <see cref="RavenMasterSettingStore"/> are:
    /// Tenant is installation-wide, so the ambient tenant routing must not apply — the resolver reads it BEFORE a
    /// tenant is even known. Without the override a tenant record would be written to the current tenant's own
    /// database, which is circular and invisible to resolution.
    /// </remarks>
    internal sealed class RavenTenantStore : RavenFrameworkStore<TenantAggregate>, ITenantStore
    {
        public RavenTenantStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IAliasStore"/> — the MASTER side, the other half of the routing core. See
    /// <see cref="RavenTenantStore"/>.
    /// </summary>
    /// <remarks>
    /// Pinned to the master database with <see cref="FixedRavenDatabaseResolver"/> exactly as
    /// <see cref="RavenTenantStore"/>: Alias is installation-wide routing state read on every request, so it must not
    /// be scoped to the ambient tenant. Note what does NOT carry over: the relational UNIQUE index on <c>name</c>
    /// (<c>ix_alias</c>) has no document-store equivalent, so RavenDB does not reject a duplicate alias name — the
    /// same inherited-divergence shape as every other unique index on the seam. The routing lookup itself is INERT to
    /// RavenDB's case-insensitive equality because it never runs as a query: <c>AliasRepository.GetAliasAsync(url)</c>
    /// matches the cached list in memory with <c>OrdinalIgnoreCase</c>, over names already lower-cased on write.
    /// </remarks>
    internal sealed class RavenAliasStore : RavenFrameworkStore<AliasAggregate>, IAliasStore
    {
        public RavenAliasStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
            : base(
                store,
                tenantContext,
                aggregateTracker,
                serializers,
                changeVectors,
                new FixedRavenDatabaseResolver(options.MasterDatabase),
                idGenerator)
        {
        }
    }

    /// <summary>
    /// The shared body of every RavenDB framework store: one aggregate, one
    /// <see cref="RavenRepository{TAggregate, TId}"/>, forwarded. Exists so the stores differ only in the
    /// interface they close over and the database they bind to — the delegation itself cannot drift between
    /// them.
    /// </summary>
    internal abstract class RavenFrameworkStore<TAggregate>(
        IDocumentStore store,
        ITenantContext tenantContext,
        IAggregateTracker aggregateTracker,
        IEnumerable<IDomainEventSerializer> serializers,
        RavenChangeVectorTracker changeVectors,
        IRavenTenantDatabaseResolver databaseResolver,
        IRavenIdGenerator idGenerator) : IRepository<TAggregate, int>
        where TAggregate : AggregateRoot<int>
    {
        private readonly IRepository<TAggregate, int> _repository = new RavenRepository<TAggregate, int>(
            store,
            tenantContext,
            aggregateTracker,
            serializers,
            changeVectors,
            databaseResolver,
            fieldProtector: null,
            idGenerator: idGenerator);

        public Task<TAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(TAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);
    }

    /// <summary>
    /// The database a tenant-side session opens on: the one the resolver names for the current tenant, else the
    /// framework database (the installation's own). Every tenant-side Raven read that is not a <see cref="RavenRepository{TAggregate, TId}"/>
    /// asks here - the session factories, the search facet, the Identity user store.
    /// </summary>
    internal static class RavenTenantRouting
    {
        public static string DatabaseOf(IRavenTenantDatabaseResolver resolver, ITenantContext tenantContext, RavenFrameworkStoreOptions options) =>
            resolver?.Resolve(tenantContext?.CurrentTenantId) is { Length: > 0 } routed
                ? routed
                : (string.IsNullOrEmpty(options.FrameworkDatabase) ? null : options.FrameworkDatabase);
    }

    /// <summary>
    /// Pins every session to one database, ignoring the ambient tenant. Used by
    /// <see cref="RavenMasterSettingStore"/>: the master database is installation-wide, so tenant routing must
    /// not apply to it.
    /// </summary>
    internal sealed class FixedRavenDatabaseResolver(string? database) : IRavenTenantDatabaseResolver
    {
        public string? Resolve(TenantId? tenantId) => database;
    }

    /// <summary>
    /// The RavenDB <see cref="IFrameworkStoreSessionFactory"/>: the seam's read path over the database of the tenant a
    /// session is opened for - the same database <see cref="RavenFrameworkStore{TAggregate}"/> writes to, so a read
    /// and a write can never address two databases.
    /// </summary>
    internal sealed class RavenFrameworkStoreSessionFactory : IFrameworkStoreSessionFactory
    {
        private readonly RavenDocumentSessionFactory _factory;

        public RavenFrameworkStoreSessionFactory(
            IDocumentStore store,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator,
            ITenantContext tenantContext,
            IRavenTenantDatabaseResolver databaseResolver)
        {
            _factory = new RavenDocumentSessionFactory(
                store,
                storeName => storeName.Equals(StoreNames.Main, StringComparison.OrdinalIgnoreCase)
                    ? RavenTenantRouting.DatabaseOf(databaseResolver, tenantContext, options)
                    : throw new InvalidOperationException($"No store is bound to the name '{storeName}'."),
                idGenerator,
                options.WaitForNonStaleResults);
        }

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }

    /// <summary>
    /// The RavenDB <see cref="IMasterStoreSessionFactory"/>: the read half of the Setting cluster's master
    /// store, bound to the MASTER database.
    /// </summary>
    /// <remarks>
    /// The SECOND factory D23 requires, and the reason <see cref="RavenDocumentSessionFactory"/> takes a
    /// database map rather than assuming the store's default. <c>SettingRepository</c> picks between this and
    /// <see cref="IFrameworkStoreSessionFactory"/> per read by <c>IsMaster(entityName)</c> and merges the two
    /// results in memory — a merge that spans two RavenDB DATABASES here, as it spans two contexts on the
    /// relational path.
    /// </remarks>
    internal sealed class RavenMasterStoreSessionFactory : IMasterStoreSessionFactory
    {
        private readonly RavenDocumentSessionFactory _factory;

        public RavenMasterStoreSessionFactory(
            IDocumentStore store,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator)
        {
            _factory = new RavenDocumentSessionFactory(
                store,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [StoreNames.Main] = options.MasterDatabase
                },
                idGenerator,
                options.WaitForNonStaleResults);
        }

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }
}
