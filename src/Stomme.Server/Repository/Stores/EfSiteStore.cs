using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The relational (EF Core) <see cref="ISiteStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/>.
    /// </summary>
    /// <remarks>
    /// The context is created per store instance from <see cref="IDbContextFactory{TContext}"/> rather than
    /// resolved from the container. That is not incidental. <b>(Premise corrected 2026-09-20; the conclusion
    /// stands.)</b> This used to say <c>HostedServiceBase</c> runs every scheduled job for every tenant inside
    /// ONE DI scope, calling <c>ITenantManager.SetTenant(...)</c> between tenants. Since <c>ff19714</c> (BL-107,
    /// 2026-09-17) it does not - each tenant gets its own ambient tenant scope AND its own DI scope. The rule
    /// here is KEPT regardless, because it does not depend on that caller being well behaved: a scoped/cached
    /// <see cref="DbContext"/> binds to the FIRST tenant's connection it sees, so any caller that reuses a scope
    /// across tenants would silently read tenant #1's data. <see cref="TenantDBContext"/> resolves its
    /// connection lazily in <c>OnConfiguring</c>, so a fresh context per (transient) store instance keeps
    /// the seam tenant-correct — the same reason every unmigrated repository calls
    /// <c>CreateDbContext()</c> per operation.
    /// </remarks>
    internal sealed class EfSiteStore : ISiteStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<SiteAggregate, int> _repository;

        public EfSiteStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<SiteAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<SiteAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<SiteAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(SiteAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(SiteAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(SiteAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="IPageStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/>. Transient, owning a
    /// per-instance context, for the tenant-correctness reason documented on <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class EfPageStore : IPageStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<PageAggregate, int> _repository;

        public EfPageStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<PageAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<PageAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<PageAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(PageAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(PageAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(PageAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="IModuleStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/>. Transient, owning a
    /// per-instance context, for the tenant-correctness reason documented on <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class EfModuleStore : IModuleStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<ModuleAggregate, int> _repository;

        public EfModuleStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<ModuleAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<ModuleAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<ModuleAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(ModuleAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(ModuleAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(ModuleAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="IUserAccountStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/>. Transient, owning a
    /// per-instance context, for the tenant-correctness reason documented on <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class EfUserAccountStore : IUserAccountStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<UserAggregate, int> _repository;

        public EfUserAccountStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<UserAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<UserAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<UserAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(UserAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(UserAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(UserAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="ISecurityRoleStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/>. Transient, owning a
    /// per-instance context, for the tenant-correctness reason documented on <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class EfSecurityRoleStore : ISecurityRoleStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<RoleAggregate, int> _repository;

        public EfSecurityRoleStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<RoleAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<RoleAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<RoleAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(RoleAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(RoleAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(RoleAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="IUserRoleAssignmentStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/>. Transient, owning a
    /// per-instance context, for the tenant-correctness reason documented on <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class EfUserRoleAssignmentStore : IUserRoleAssignmentStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<UserRoleAggregate, int> _repository;

        public EfUserRoleAssignmentStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<UserRoleAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<UserRoleAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<UserRoleAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(UserRoleAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(UserRoleAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(UserRoleAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="ISettingStore"/> — the TENANT half of the Setting cluster
    /// (D23). Transient, owning a per-instance context, for the tenant-correctness reason documented on
    /// <see cref="EfSiteStore"/>. That reason is at its sharpest here: <c>NotificationJob</c> and friends read
    /// site settings for every tenant of a run. (They each get their own DI scope since BL-107 / <c>ff19714</c>;
    /// the per-instance context is what makes this store correct even when a caller does not.)
    /// </summary>
    internal sealed class EfSettingStore : ISettingStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<SettingAggregate, int> _repository;

        public EfSettingStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<SettingAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<SettingAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<SettingAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(SettingAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(SettingAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(SettingAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="IMasterSettingStore"/> — the MASTER half of the Setting cluster
    /// (D23), over a <see cref="MasterDBContext"/> instead of a <see cref="TenantDBContext"/>.
    /// </summary>
    /// <remarks>
    /// The same per-instance-context rule applies, for a different reason: <see cref="MasterDBContext"/>'s
    /// connection is installation-wide, so there is no cross-tenant hazard here — but the harvested
    /// <c>SettingRepository</c> held ONE long-lived master context and both read and wrote through it, which
    /// is precisely the tracked-read/detached-write shape D19 was written about. A fresh context per store
    /// instance, with reads routed through <see cref="IMasterStoreSessionFactory"/>, removes that.
    /// </remarks>
    internal sealed class EfMasterSettingStore : IMasterSettingStore, IDisposable
    {
        private readonly MasterDBContext _context;
        private readonly IRepository<SettingAggregate, int> _repository;

        public EfMasterSettingStore(
            IDbContextFactory<MasterDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<SettingAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<SettingAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<SettingAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(SettingAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(SettingAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(SettingAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational (EF Core) <see cref="IPermissionStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over a <see cref="TenantDBContext"/> (D24). Transient,
    /// owning a per-instance context, for the tenant-correctness reason documented on
    /// <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class EfPermissionStore : IPermissionStore, IDisposable
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<PermissionAggregate, int> _repository;

        public EfPermissionStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<PermissionAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<PermissionAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<PermissionAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(PermissionAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(PermissionAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(PermissionAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The shared body of a relational framework store: one aggregate, one
    /// <see cref="EfRepository{TAggregate, TId}"/> over one <see cref="TenantDBContext"/>, forwarded — the
    /// mirror image of the RavenDB side's <c>RavenFrameworkStore&lt;TAggregate&gt;</c>, and the same shape the
    /// conformance suite's <c>TestStore&lt;TAggregate&gt;</c> already uses. Exists so the stores below differ
    /// only in the interface they close over; the delegation itself cannot drift between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context is created per store instance rather than resolved from the container, for the
    /// tenant-correctness reason documented at length on <see cref="EfSiteStore"/>: a cached context binds to
    /// the first tenant's connection it sees, so any caller that reuses one DI scope across tenants would serve
    /// tenant #1's data to every later tenant. Transient stores plus a per-instance context is what keeps the
    /// seam tenant-correct without depending on the caller. (<c>HostedServiceBase</c> was that caller until
    /// BL-107 / <c>ff19714</c>, 2026-09-17; it now scopes each tenant properly, and this still holds.)
    /// </para>
    /// <para>
    /// <b>The nine stores above predate this base and are deliberately left alone.</b> They are proven code on
    /// the render path; rewriting them onto this base would be a mechanical change to working clusters for
    /// readability alone, which is not what D44 is for. New clusters use this; the old ones can be folded in
    /// by whoever next has a reason to touch them.
    /// </para>
    /// </remarks>
    internal abstract class EfFrameworkStore<TAggregate> : IRepository<TAggregate, int>, IDisposable
        where TAggregate : AggregateRoot<int>
    {
        private readonly TenantDBContext _context;
        private readonly IRepository<TAggregate, int> _repository;

        protected EfFrameworkStore(
            IDbContextFactory<TenantDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<TAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

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

        public void Dispose() => _context.Dispose();
    }

    /// <summary>The relational <see cref="ILanguageStore"/> (D44). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfLanguageStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<LanguageAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ILanguageStore;

    /// <summary>The relational <see cref="ILogStore"/> (D44). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfLogStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<LogAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ILogStore;

    /// <summary>The relational <see cref="IProfileStore"/> (D44). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfProfileStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<ProfileAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IProfileStore;

    /// <summary>The relational <see cref="ISiteTaskStore"/> (D44). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfSiteTaskStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SiteTaskAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISiteTaskStore;

    /// <summary>The relational <see cref="INotificationStore"/> (D44). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfNotificationStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<NotificationAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), INotificationStore;

    /// <summary>The relational <see cref="ISiteGroupStore"/> (D45). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfSiteGroupStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SiteGroupAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISiteGroupStore;

    /// <summary>The relational <see cref="ISiteGroupMemberStore"/> (D45). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfSiteGroupMemberStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SiteGroupMemberAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISiteGroupMemberStore;

    /// <summary>The relational <see cref="IVisitorStore"/> (D47). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfVisitorStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<VisitorAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IVisitorStore;

    /// <summary>The relational <see cref="IUrlMappingStore"/> (D47). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfUrlMappingStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<UrlMappingAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IUrlMappingStore;

    /// <summary>The relational <see cref="IPageModuleStore"/> (D47). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfPageModuleStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<PageModuleAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IPageModuleStore;

    /// <summary>The relational <see cref="IFolderStore"/> (D48). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfFolderStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<FolderAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IFolderStore;

    /// <summary>The relational <see cref="IFileStore"/> (D49). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfFileStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<FileAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IFileStore;

    // The SearchContent cluster (D50). Four tables, tenant-side only, no master half.

    /// <summary>The relational <see cref="ISearchContentStore"/> (D50). See <see cref="EfFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfSearchContentStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SearchContentAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISearchContentStore;

    /// <summary>The relational <see cref="ISearchContentPropertyStore"/> (D50).</summary>
    internal sealed class EfSearchContentPropertyStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SearchContentPropertyAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISearchContentPropertyStore;

    /// <summary>The relational <see cref="ISearchContentWordStore"/> (D50).</summary>
    internal sealed class EfSearchContentWordStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SearchContentWordAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISearchContentWordStore;

    /// <summary>The relational <see cref="ISearchWordStore"/> (D50).</summary>
    internal sealed class EfSearchWordStore(
        IDbContextFactory<TenantDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfFrameworkStore<SearchWordAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ISearchWordStore;

    /// <summary>
    /// The MASTER-database twin of <see cref="EfFrameworkStore{TAggregate}"/>: one aggregate, one
    /// <see cref="EfRepository{TAggregate, TId}"/> over one <see cref="MasterDBContext"/>, forwarded. The same
    /// composition <see cref="EfMasterSettingStore"/> performs, hoisted to a base so the master-side clusters
    /// below differ only in the interface they close.
    /// </summary>
    /// <remarks>
    /// A fresh context per store instance, with reads routed through <see cref="IMasterStoreSessionFactory"/>,
    /// removes the tracked-read/detached-write shape D19 was written about — the same reason
    /// <see cref="EfMasterSettingStore"/> states. The master connection is installation-wide, so there is no
    /// cross-tenant hazard here; the per-instance context is about D19, not tenancy.
    /// </remarks>
    internal abstract class EfMasterFrameworkStore<TAggregate> : IRepository<TAggregate, int>, IDisposable
        where TAggregate : AggregateRoot<int>
    {
        private readonly MasterDBContext _context;
        private readonly IRepository<TAggregate, int> _repository;

        protected EfMasterFrameworkStore(
            IDbContextFactory<MasterDBContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<TAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

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

        public void Dispose() => _context.Dispose();
    }

    /// <summary>The relational <see cref="IJobStore"/> (D52) — MASTER side. See <see cref="EfMasterFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfJobStore(
        IDbContextFactory<MasterDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfMasterFrameworkStore<JobAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IJobStore;

    /// <summary>The relational <see cref="IJobLogStore"/> (D52) — MASTER side. See <see cref="EfMasterFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfJobLogStore(
        IDbContextFactory<MasterDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfMasterFrameworkStore<JobLogAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IJobLogStore;

    /// <summary>The relational <see cref="IThemeStore"/> (D52) — MASTER side. See <see cref="EfMasterFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfThemeStore(
        IDbContextFactory<MasterDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfMasterFrameworkStore<ThemeAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IThemeStore;

    /// <summary>The relational <see cref="IModuleDefinitionStore"/> (D52) — MASTER side. See <see cref="EfMasterFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfModuleDefinitionStore(
        IDbContextFactory<MasterDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfMasterFrameworkStore<ModuleDefinitionAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IModuleDefinitionStore;

    /// <summary>The relational <see cref="ITenantStore"/> (D52) — MASTER side, the routing core. See <see cref="EfMasterFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfTenantStore(
        IDbContextFactory<MasterDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfMasterFrameworkStore<TenantAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), ITenantStore;

    /// <summary>The relational <see cref="IAliasStore"/> (D52) — MASTER side, the routing core. See <see cref="EfMasterFrameworkStore{TAggregate}"/>.</summary>
    internal sealed class EfAliasStore(
        IDbContextFactory<MasterDBContext> contextFactory,
        IAggregateTracker aggregateTracker,
        ITenantContext tenantContext,
        IEnumerable<IDomainEventSerializer> serializers)
        : EfMasterFrameworkStore<AliasAggregate>(contextFactory, aggregateTracker, tenantContext, serializers), IAliasStore;

    /// <summary>
    /// The relational <see cref="ISearchQueryStore"/> (D50) — <b>today's query, kept in SQL</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>GroupBy</c>+<c>Sum</c>, the two-level join and the prefix arms all stay in the database, exactly
    /// as the harvested <c>SearchContentRepository.GetSearchContentsAsync</c> put them. Verified in the SQL
    /// PostgreSQL and SQLite actually receive: <c>WHERE ... AND word LIKE 'run%'</c>,
    /// <c>GROUP BY ... COALESCE(SUM(count), 0)</c>, and <c>HAVING entity_name IN (...)</c> for the
    /// include/exclude arms.
    /// </para>
    /// <para>
    /// <b>It is NOT verbatim, and the difference is a bug fix D50 owns.</b> The harvested code's two
    /// <c>SearchQuery.Properties</c> arms THREW on EF Core: <c>.Include()</c> after a <c>GroupBy().Select()</c>
    /// projection ("Include has been applied after 'Select'"), and <c>.Any()</c> on the projected entity
    /// ("could not be translated"). Nothing in-tree populates <c>Properties</c>, but
    /// <c>SearchResultsController.Post</c> binds it <c>[FromBody]</c>, so any client that posted one got a 500.
    /// Both arms are now a second query against <c>SearchContentPropertyAggregate</c>, intersected by
    /// SearchContentId — which is what they were trying to express. Pinned by
    /// <c>The_properties_arm_filters_and_no_longer_throws</c>.
    /// </para>
    /// <para>
    /// Reads open their own session rather than going through a store (D19).
    /// </para>
    /// </remarks>
    internal sealed class EfSearchQueryStore(IFrameworkStoreSessionFactory sessions) : ISearchQueryStore
    {
        public async Task<IReadOnlyList<SearchContent>> SearchAsync(SearchQuery searchQuery, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(searchQuery);
            await using var session = sessions.OpenQuerySession();

            var keywords = SearchUtils.GetKeywords(searchQuery.Keywords);

            // The harvested shape: join SearchContentWord -> SearchWord/SearchContent, filter, group, sum.
            // Composed against the session's IQueryable so the whole thing stays server-side.
            var words = session.Query<SearchContentWordAggregate>();
            var vocabulary = session.Query<SearchWordAggregate>();
            var contents = session.Query<SearchContentAggregate>();

            var matchedWordIds = vocabulary
                .FilterByItems(keywords, (item, keyword) => item.Word.StartsWith(keyword), true)
                .Select(item => item.Id);

            var query = from word in words
                        join content in contents on word.SearchContentId equals content.Id
                        where content.SiteId == searchQuery.SiteId && matchedWordIds.Contains(word.SearchWordId)
                        group word by new
                        {
                            content.Id,
                            content.SiteId,
                            content.EntityName,
                            content.EntityId,
                            content.Title,
                            content.Description,
                            content.Body,
                            content.Url,
                            content.Permissions,
                            content.ContentModifiedBy,
                            content.ContentModifiedOn,
                            content.AdditionalContent,
                            content.CreatedOn
                        } into grouped
                        select new SearchContent
                        {
                            SearchContentId = grouped.Key.Id,
                            SiteId = grouped.Key.SiteId,
                            EntityName = grouped.Key.EntityName,
                            EntityId = grouped.Key.EntityId,
                            Title = grouped.Key.Title,
                            Description = grouped.Key.Description,
                            Body = grouped.Key.Body,
                            Url = grouped.Key.Url,
                            Permissions = grouped.Key.Permissions,
                            ContentModifiedBy = grouped.Key.ContentModifiedBy,
                            ContentModifiedOn = grouped.Key.ContentModifiedOn,
                            AdditionalContent = grouped.Key.AdditionalContent,
                            CreatedOn = grouped.Key.CreatedOn,
                            Count = grouped.Sum(item => item.Count)
                        };

            query = SearchQueryComposition.ApplyContentFilters(query, searchQuery);

            var results = query.ToList();

            await SearchQueryComposition
                .ApplyPropertyFilterAsync(results, searchQuery, session, ct)
                .ConfigureAwait(false);

            return results;
        }
    }

    /// <summary>
    /// The filter arms both <see cref="ISearchQueryStore"/> implementations share (D50).
    /// </summary>
    /// <remarks>
    /// These are provider-neutral by construction: entity include/exclude and the date bounds are plain
    /// comparisons over already-materialized values or composable <see cref="IQueryable{T}"/> arms that every
    /// provider translates. Only the PREFIX-AND-SUM half diverges, which is why only that half lives in the
    /// two implementations. Keeping the shared arms here is what makes "same set, same Count" a property of
    /// one piece of code rather than a coincidence between two.
    /// </remarks>
    internal static class SearchQueryComposition
    {
        /// <summary>
        /// The include/exclude and date arms, composed onto the grouped projection. On EF Core these land in
        /// the <c>HAVING</c> clause; the RavenDB half applies them to the facet-matched set the same way.
        /// </summary>
        public static IQueryable<SearchContent> ApplyContentFilters(IQueryable<SearchContent> query, SearchQuery searchQuery)
        {
            if (!string.IsNullOrEmpty(searchQuery.IncludeEntities))
            {
                var include = searchQuery.IncludeEntities.Split(',', StringSplitOptions.RemoveEmptyEntries);
                query = query.Where(item => include.Contains(item.EntityName));
            }

            if (!string.IsNullOrEmpty(searchQuery.ExcludeEntities))
            {
                var exclude = searchQuery.ExcludeEntities.Split(',', StringSplitOptions.RemoveEmptyEntries);
                query = query.Where(item => !exclude.Contains(item.EntityName));
            }

            if (searchQuery.FromDate.Date != DateTime.MinValue.Date)
            {
                query = query.Where(item => item.ContentModifiedOn >= searchQuery.FromDate);
            }

            // BL-251: ToDate defaults to DateTime.MinValue, so a query without one found NOTHING; the default means "no end".
            if (searchQuery.ToDate.Date != DateTime.MaxValue.Date && searchQuery.ToDate.Date != DateTime.MinValue.Date)
            {
                query = query.Where(item => item.ContentModifiedOn <= searchQuery.ToDate);
            }

            return query;
        }

        /// <summary>
        /// The <c>SearchQuery.Properties</c> arm, as ONE session query over
        /// <see cref="SearchContentPropertyAggregate"/> rather than the two expressions that threw (D50 §7).
        /// Narrows <paramref name="results"/> in place and populates each survivor's
        /// <c>SearchContentProperties</c>, which is what the harvested <c>.Include()</c> was reaching for.
        /// </summary>
        public static async Task ApplyPropertyFilterAsync(
            List<SearchContent> results,
            SearchQuery searchQuery,
            IQuerySession session,
            CancellationToken ct)
        {
            if (searchQuery.Properties is null || !searchQuery.Properties.Any() || results.Count == 0)
            {
                return;
            }

            await Task.CompletedTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var contentIds = results.ConvertAll(item => item.SearchContentId);
            var properties = session.Query<SearchContentPropertyAggregate>()
                .Where(item => contentIds.Contains(item.SearchContentId))
                .ToList();

            var byContent = properties
                .GroupBy(item => item.SearchContentId)
                .ToDictionary(group => group.Key, group => group.ToList());

            results.RemoveAll(item =>
            {
                if (!byContent.TryGetValue(item.SearchContentId, out var owned))
                {
                    return true;
                }

                return searchQuery.Properties.Any(required =>
                    !owned.Exists(property => property.Name == required.Key && property.Value == required.Value));
            });

            foreach (var item in results)
            {
                item.SearchContentProperties = byContent[item.SearchContentId]
                    .ConvertAll(SearchContentPropertyMapper.ToModel);
            }
        }
    }

    /// <summary>
    /// The relational <see cref="IFrameworkStoreSessionFactory"/>: EF Core document/query sessions over a
    /// fresh per-session <see cref="TenantDBContext"/> (the session owns and disposes it), for the same
    /// tenant-correctness reason as <see cref="EfSiteStore"/>.
    /// </summary>
    internal sealed class FrameworkStoreSessionFactory : IFrameworkStoreSessionFactory
    {
        private readonly EfDocumentSessionFactory _factory;

        public FrameworkStoreSessionFactory(IDbContextFactory<TenantDBContext> contextFactory)
        {
            _factory = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = () => contextFactory.CreateDbContext()
            });
        }

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }

    /// <summary>
    /// The relational <see cref="IMasterStoreSessionFactory"/>: EF Core query sessions over a fresh
    /// per-session <see cref="MasterDBContext"/>. The read half of the Setting cluster's master store (D23).
    /// </summary>
    internal sealed class MasterStoreSessionFactory : IMasterStoreSessionFactory
    {
        private readonly EfDocumentSessionFactory _factory;

        public MasterStoreSessionFactory(IDbContextFactory<MasterDBContext> contextFactory)
        {
            _factory = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = () => contextFactory.CreateDbContext()
            });
        }

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }
}
