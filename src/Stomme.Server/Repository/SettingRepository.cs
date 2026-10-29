using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository
{
    public interface ISettingRepository
    {
        Task<IEnumerable<Setting>> GetSettingsAsync(string entityName);
        Task<IEnumerable<Setting>> GetSettingsAsync(string entityName, int entityId);
        Task<IEnumerable<Setting>> GetSettingsAsync(string entityName1, int entityId1, string entityName2, int entityId2);
        Task<Setting> AddSettingAsync(Setting setting);
        Task<Setting> UpdateSettingAsync(Setting setting);
        Task<Setting> GetSettingAsync(string entityName, int settingId);
        Task<Setting> GetSettingAsync(string entityName, int entityId, string settingName);
        Task DeleteSettingAsync(string entityName, int settingId);
        Task DeleteSettingsAsync(string entityName, int entityId);
        Task<IEnumerable<string>> GetEntityNamesAsync();
        Task<IEnumerable<int>> GetEntityIdsAsync(string entityName);

        Task<string> GetSettingValueAsync(string entityName, int entityId, string settingName, string defaultValue);
    }

    /// <summary>
    /// The pure half of the Setting surface: helpers that operate on settings the caller has ALREADY
    /// fetched, and therefore touch no store.
    /// </summary>
    /// <remarks>
    /// <c>GetSettingValue(IEnumerable&lt;Setting&gt;, ...)</c> lives here rather than on
    /// <see cref="ISettingRepository"/> because it is a <see cref="Enumerable.FirstOrDefault{T}"/> over a
    /// list the caller passes in — it performs no I/O and has nothing to await. D32 bans sync members on
    /// store-touching interfaces; the honest response for genuinely synchronous work is to take it OFF the
    /// store-touching interface, not to wrap it in <c>Task.FromResult</c> and call it async. An extension on
    /// <see cref="ISettingRepository"/> keeps every existing call site (<c>_settings.GetSettingValue(settings,
    /// name, default)</c>) compiling unchanged.
    /// Its sibling <see cref="ISettingRepository.GetSettingValueAsync"/> — the overload that takes an
    /// entityName/entityId and READS the store to find the setting — is async, and stays on the interface.
    /// </remarks>
    public static class SettingRepositoryExtensions
    {
        /// <summary>
        /// Returns the named setting's value from an already-fetched set, or <paramref name="defaultValue"/>.
        /// </summary>
        public static string GetSettingValue(this ISettingRepository repository, IEnumerable<Setting> settings, string settingName, string defaultValue)
        {
            var setting = settings.FirstOrDefault(item => item.SettingName == settingName);
            if (setting != null)
            {
                return setting.SettingValue;
            }
            else
            {
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// The Setting cluster on the persistence seam. Setting-table access goes through
    /// <see cref="ISettingStore"/>/<see cref="IMasterSettingStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads); this class holds no <c>DbContext</c> and no
    /// <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE THING THAT MAKES THIS CLUSTER DIFFERENT: Setting is the only framework-store table that lives in
    /// BOTH databases. <see cref="IsMaster"/> routes Host, Job, ModuleDefinition, Theme and <c>master:*</c>
    /// settings to the installation-wide master database and everything else to the current tenant's. That
    /// routing is unchanged harvested behaviour — it is application logic, so it stays here in the repository
    /// rather than leaking into the seam. Each of the two databases gets its own store and its own session
    /// factory over the same <see cref="SettingAggregate"/>.
    /// </para>
    /// <para>
    /// This repository injects NO other repository, and did not before. That is worth stating explicitly
    /// because <see cref="ISettingRepository"/> is depended on by nearly every other repository (Site, Page,
    /// Module, Role, Folder, the module ecosystem…): those are INBOUND edges, and a cycle needs an outbound
    /// one. Keeping this a leaf is what makes it safe for everything else to depend on it — do not inject
    /// another repository here without re-running the cycle check per edge (D22 rule 8).
    /// </para>
    /// </remarks>
    public class SettingRepository : ISettingRepository
    {
        private readonly ISettingStore _tenantStore;
        private readonly IMasterSettingStore _masterStore;
        private readonly IFrameworkStoreSessionFactory _tenantSessions;
        private readonly IMasterStoreSessionFactory _masterSessions;
        private readonly ITenantManager _tenantManager;
        private readonly ICacheManager _cache;

        public SettingRepository(ISettingStore tenantStore, IMasterSettingStore masterStore, IFrameworkStoreSessionFactory tenantSessions, IMasterStoreSessionFactory masterSessions, ITenantManager tenantManager, ICacheManager cache)
        {
            _tenantStore = tenantStore;
            _masterStore = masterStore;
            _tenantSessions = tenantSessions;
            _masterSessions = masterSessions;
            _tenantManager = tenantManager;
            _cache = cache;
        }

        // The write path: the store bound to the database this EntityName's settings live in.
        private IRepository<SettingAggregate, int> Store(string entityName)
            => IsMaster(entityName) ? _masterStore : _tenantStore;

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and UpdateSetting then attaches a DIFFERENT instance with the same key, which EF rejects:
        //   "The instance of entity type 'SettingAggregate' cannot be tracked because another instance with
        //    the same key value for {'Id'} is already being tracked."
        // SettingController.Put walks straight into it (GetSetting to authorize, then UpdateSetting), and so
        // does ModuleRepository.ImportModule (GetSettings, mutate, UpdateSetting) — both on one repository
        // instance, hence one store and one context. A query session opens its own context, so reads leave
        // the store's change tracker clean.
        //
        // The IQueryable is materialized inside this method, so it never leaves the repository. The LINQ
        // operators are plain LINQ rather than EF Core's async ones, which would bind these reads to the
        // relational provider.
        private async Task<T> QueryAsync<T>(string entityName, Func<IQuerySession, T> read)
        {
            var factory = IsMaster(entityName) ? (IDocumentSessionFactory)_masterSessions : _tenantSessions;
            var session = factory.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<Setting>> GetSettingsAsync(string entityName)
        {
            // Filtered in the DATABASE on EntityName, exactly as db.Setting.Where did. This one is read for
            // every module and every page on the render path (ModuleController/PageController denormalize
            // settings onto their responses), so an in-memory filter here would read the whole table per
            // request.
            return await QueryAsync(entityName, session => session.Query<SettingAggregate>()
                .Where(item => item.EntityName == entityName)
                .ToList()
                .ConvertAll(SettingMapper.ToModel));
        }

        public async Task<IEnumerable<Setting>> GetSettingsAsync(string entityName, int entityId)
        {
            // Both halves of the polymorphic key filtered in the DATABASE, as before.
            return await QueryAsync(entityName, session => session.Query<SettingAggregate>()
                .Where(item => item.EntityName == entityName && item.EntityId == entityId)
                .ToList()
                .ConvertAll(SettingMapper.ToModel));
        }

        public async Task<IEnumerable<Setting>> GetSettingsAsync(string entityName1, int entityId1, string entityName2, int entityId2)
        {
            // merge settings from entity2 into entity1
            // Unchanged: in-memory composition over two already-filtered database reads, not a join. It always
            // was one — the two entities can live in DIFFERENT DATABASES (Host + Site, say), so there is no
            // single query to push this down into.
            var settings1 = (await GetSettingsAsync(entityName1, entityId1)).ToList();
            foreach (var setting2 in await GetSettingsAsync(entityName2, entityId2))
            {
                var setting1 = settings1.FirstOrDefault(item => item.SettingName == setting2.SettingName);
                if (setting1 == null)
                {
                    settings1.Add(new Setting { EntityName = entityName1, EntityId = entityId1, SettingName = setting2.SettingName, SettingValue = setting2.SettingValue, IsPrivate = setting2.IsPrivate });
                }
                else
                {
                    setting1.SettingValue = setting2.SettingValue;
                    setting1.IsPrivate = setting2.IsPrivate;
                }
            }
            return settings1;
        }

        public async Task<Setting> AddSettingAsync(Setting setting)
        {
            var aggregate = SettingMapper.ToAggregate(setting, assignId: false);
            await Store(setting.EntityName).AddAsync(aggregate);
            SettingMapper.CopyPersistedValues(aggregate, setting);
            await ManageCacheAsync(setting.EntityName);
            return setting;
        }

        public async Task<Setting> UpdateSettingAsync(Setting setting)
        {
            var aggregate = SettingMapper.ToAggregate(setting, assignId: true);
            await Store(setting.EntityName).UpdateAsync(aggregate);
            SettingMapper.CopyPersistedValues(aggregate, setting);
            await ManageCacheAsync(setting.EntityName);
            return setting;
        }

        public async Task<Setting> GetSettingAsync(string entityName, int settingId)
        {
            // entityName selects the DATABASE here, it is not a filter — parity with the harvested
            // Find(settingId), which likewise did not constrain on EntityName.
            return await QueryAsync(entityName, session => SettingMapper.ToModel(session.Query<SettingAggregate>()
                .FirstOrDefault(item => item.Id == settingId)));
        }

        public async Task<Setting> GetSettingAsync(string entityName, int entityId, string settingName)
        {
            return await QueryAsync(entityName, session => SettingMapper.ToModel(session.Query<SettingAggregate>()
                .FirstOrDefault(item => item.EntityName == entityName && item.EntityId == entityId && item.SettingName == settingName)));
        }

        public async Task DeleteSettingAsync(string entityName, int settingId)
        {
            var store = Store(entityName);

            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await store.GetByIdAsync(settingId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches DeleteModule/DeleteRole/DeleteUser's shape — a deliberate, strictly-safer
                // divergence, consistent across the migrated clusters.
                return;
            }

            await store.DeleteAsync(aggregate);
            await ManageCacheAsync(entityName);
        }

        public async Task DeleteSettingsAsync(string entityName, int entityId)
        {
            // The (EntityName, EntityId) filter stays in the DATABASE (rule 9). Reading through the store
            // instead would mean GetAllAsync — the seam has no filtered read — i.e. an unfiltered scan of
            // every setting in the database, and this method is called from every DeleteX in the framework.
            // So: one filtered read through the session, then delete each detached aggregate through the
            // store (Remove attaches a keyed detached entity as Deleted). The store's tracker is clean
            // because nothing was read through it, so there is no identity conflict.
            var aggregates = await QueryAsync(entityName, session => session.Query<SettingAggregate>()
                .Where(item => item.EntityName == entityName && item.EntityId == entityId)
                .ToList());

            var store = Store(entityName);
            foreach (var aggregate in aggregates)
            {
                // One SaveChanges per setting: the seam has no bulk delete, so this is a round trip per row
                // where the harvested code did one. Parity in effect, not in cost — see src/Stomme.Server/README.md (persistence seam).
                await store.DeleteAsync(aggregate);
            }

            await ManageCacheAsync(entityName);
        }

        public async Task<IEnumerable<string>> GetEntityNamesAsync()
        {
            // NOTE: the tenant database only, with no IsMaster branch — harvested behaviour, preserved. These
            // two methods back the admin "Settings" browser, which is a tenant-scoped view.
            //
            // The projection and Distinct stay in the DATABASE, as db.Setting.Select(...).Distinct() did. The
            // ORDERING is applied after materializing, and that is deliberate: OrderBy(item => item) orders by
            // the projected scalar ITSELF, which has no member path, and a document provider cannot express
            // that ("Could not understand how to translate 'item' to a RavenDB query" — verified live against
            // RavenDB 7.2.4). Every provider can order a list it has already returned. The result is identical
            // and the cost is nil: this is the DISTINCT set of entity names, which is a handful of rows, not
            // the Setting table. See src/Stomme.Persistence.RavenDb/README.md.
            return (await QueryAsync(EntityNames.Setting, session => session.Query<SettingAggregate>()
                    .Select(item => item.EntityName)
                    .Distinct()
                    .ToList()))
                .OrderBy(item => item)
                .ToList();
        }

        public async Task<IEnumerable<int>> GetEntityIdsAsync(string entityName)
        {
            // Deliberately NOT routed on entityName: like GetEntityNames above, the harvested code always
            // read the tenant database here, even for a master EntityName. Preserved rather than "fixed" —
            // changing it is a behaviour change, not a mapping one. EntityNames.Setting is never a master
            // name, so this routes to the tenant session.
            //
            // The filter and Distinct stay in the database; the ordering is applied to the materialized
            // distinct set, for the reason documented on GetEntityNames.
            return (await QueryAsync(EntityNames.Setting, session => session.Query<SettingAggregate>()
                    .Where(item => item.EntityName == entityName)
                    .Select(item => item.EntityId)
                    .Distinct()
                    .ToList()))
                .OrderBy(item => item)
                .ToList();
        }

        public async Task<string> GetSettingValueAsync(string entityName, int entityId, string settingName, string defaultValue)
        {
            var setting = await GetSettingAsync(entityName, entityId, settingName);
            if (setting != null)
            {
                return setting.SettingValue;
            }
            else
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Which of the two databases an EntityName's settings live in. Installation-wide concepts (the Host
        /// itself, scheduled Jobs, ModuleDefinitions, Themes, and anything explicitly prefixed
        /// <c>master:</c>) live in the master database; everything else is per-tenant. Unchanged from the
        /// harvested code — this is the routing rule the whole cluster turns on.
        /// </summary>
        private bool IsMaster(string EntityName)
        {
            return EntityName == EntityNames.Host || EntityName == EntityNames.Job ||
                EntityName == EntityNames.ModuleDefinition || EntityName == EntityNames.Theme ||
                EntityName.ToLower().StartsWith("master:");
        }

        private async Task ManageCacheAsync(string EntityName)
        {
            if (EntityName == EntityNames.Site)
            {
                await _cache.RemoveCacheAsync(await _tenantManager.GetAliasAsync(), Constants.HttpContextSiteSettingsKey);
            }
        }
    }
}
