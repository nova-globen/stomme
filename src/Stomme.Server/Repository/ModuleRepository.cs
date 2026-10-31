using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Module = Stomme.Models.Module;

namespace Stomme.Repository
{
    public interface IModuleRepository
    {
        Task<IEnumerable<Module>> GetModulesAsync(int siteId);
        Task<Module> AddModuleAsync(Module module);
        Task<Module> UpdateModuleAsync(Module module);
        Task<Module> GetModuleAsync(int moduleId);
        Task<Module> GetModuleAsync(int moduleId, bool tracking);
        Task DeleteModuleAsync(int moduleId);
        Task<string> ExportModuleAsync(int moduleId);
        Task<string> ExportModuleAsync(Module module, string IPortableContext);
        Task<bool> ImportModuleAsync(int moduleId, string content);
        Task<bool> ImportModuleAsync(Module module, string content, string IPortableContext);
    }

    /// <summary>
    /// The Module cluster on the persistence seam. Module-table access goes through
    /// <see cref="IModuleStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// Deliberately OUT of this cluster, mirroring the Sites/Pages precedent: a module's PermissionList is
    /// denormalized onto the wire contract by <see cref="IPermissionRepository"/>, its settings are read and
    /// deleted through <see cref="ISettingRepository"/>, and Import/Export compose with
    /// <see cref="IModuleDefinitionRepository"/> and the module's own <c>IPortable</c> implementation (which
    /// owns its own module-specific <c>DbContext</c> — a different seam question entirely). Each is a
    /// different aggregate with its own root, its own repository and its own brief, all still on the
    /// <c>IDatabase</c>/<c>TenantDBContext</c> path. Coexistence is the D3-safe plan; those calls move when
    /// their own clusters do.
    /// </remarks>
    public class ModuleRepository : IModuleRepository
    {
        private readonly IModuleStore _moduleStore;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IPermissionRepository _permissions;
        private readonly ISettingRepository _settings;
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly IServiceProvider _serviceProvider;

        public ModuleRepository(IModuleStore moduleStore, IFrameworkStoreSessionFactory sessions, IPermissionRepository permissions, ISettingRepository settings, IModuleDefinitionRepository moduleDefinitions, IServiceProvider serviceProvider)
        {
            _moduleStore = moduleStore;
            _sessions = sessions;
            _permissions = permissions;
            _settings = settings;
            _moduleDefinitions = moduleDefinitions;
            _serviceProvider = serviceProvider;
        }

        // The read path on the seam: the IQueryable is materialized inside this method, so it never leaves
        // the repository. ToList/Where/FirstOrDefault are plain LINQ rather than EF Core's async
        // operators, which would bind these reads to the relational provider.
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

        public async Task<IEnumerable<Module>> GetModulesAsync(int siteId)
        {
            // Filtered in the DATABASE on the module's own SiteId column, exactly as db.Module.Where did.
            // PageModuleRepository.GetPageModules depends on this: it is what keeps that method's site filter
            // out of memory now that the PageModule.Module navigation is gone. See src/Stomme.Server/README.md (persistence seam).
            return await QueryAsync(session => session.Query<ModuleAggregate>()
                .Where(item => item.SiteId == siteId)
                .ToList()
                .ConvertAll(ModuleMapper.ToModel));
        }

        public async Task<Module> AddModuleAsync(Module module)
        {
            var aggregate = ModuleMapper.ToAggregate(module, assignId: false);
            await _moduleStore.AddAsync(aggregate);
            ModuleMapper.CopyPersistedValues(aggregate, module);
            await _permissions.UpdatePermissionsAsync(module.SiteId, EntityNames.Module, module.ModuleId, module.PermissionList);
            return module;
        }

        public async Task<Module> UpdateModuleAsync(Module module)
        {
            var aggregate = ModuleMapper.ToAggregate(module, assignId: true);
            await _moduleStore.UpdateAsync(aggregate);
            ModuleMapper.CopyPersistedValues(aggregate, module);
            await _permissions.UpdatePermissionsAsync(module.SiteId, EntityNames.Module, module.ModuleId, module.PermissionList);
            return module;
        }

        public async Task<Module> GetModuleAsync(int moduleId)
        {
            return await GetModuleAsync(moduleId, true);
        }

        public async Task<Module> GetModuleAsync(int moduleId, bool tracking)
        {
            // note that the tracking parameter is no longer relevant: the seam hands back a detached
            // aggregate and ModuleMapper projects it onto a fresh wire contract either way.
            //
            // Read through the query session, NOT _moduleStore.GetByIdAsync — that is load-bearing, not a
            // style choice. The store's IRepository.GetByIdAsync TRACKS the aggregate in the store's
            // DbContext, and UpdateAsync then attaches a DIFFERENT instance rebuilt from the wire contract
            // with the same key, which EF rejects:
            //   "The instance of entity type 'ModuleAggregate' cannot be tracked because another instance
            //    with the same key value for {'Id'} is already being tracked."
            // ModuleController.Put walks straight into it: GetModule(module.ModuleId, false) to authorize
            // and to compare AllPages, then UpdateModule(module) — both on this same repository instance,
            // hence the same store and the same context. The query session opens its own context, so reads
            // leave the store's change tracker clean. Pinned by
            // ModuleStoreBehaviourTests.A_session_read_then_a_store_write_persists.
            var module = await QueryAsync(session => ModuleMapper.ToModel(session.Query<ModuleAggregate>()
                .FirstOrDefault(item => item.Id == moduleId)));
            if (module != null)
            {
                module.PermissionList = (await _permissions.GetPermissionsAsync(module.SiteId, EntityNames.Module, module.ModuleId))?.ToList();
            }
            return module;
        }

        public async Task DeleteModuleAsync(int moduleId)
        {
            var aggregate = await _moduleStore.GetByIdAsync(moduleId);
            if (aggregate == null)
            {
                return;
            }

            await _permissions.DeletePermissionsAsync(aggregate.SiteId, EntityNames.Module, moduleId);
            await _settings.DeleteSettingsAsync(EntityNames.Module, moduleId);
            await _moduleStore.DeleteAsync(aggregate);
        }

        public async Task<string> ExportModuleAsync(int moduleId)
        {
            Module module = await GetModuleAsync(moduleId);
            return await ExportModuleAsync(module, "Export Module");
        }

        public async Task<string> ExportModuleAsync(Module module, string IPortableContext)
        {
            string content = "";
            try
            {
                if (module != null)
                {
                    List<ModuleDefinition> moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(module.SiteId)).ToList();
                    ModuleDefinition moduledefinition = moduledefinitions.FirstOrDefault(item => item.ModuleDefinitionName == module.ModuleDefinitionName);
                    if (moduledefinition != null)
                    {
                        var settings = await _settings.GetSettingsAsync(EntityNames.Module, module.ModuleId);

                        ModuleContent modulecontent = new ModuleContent();
                        modulecontent.ModuleDefinitionName = moduledefinition.ModuleDefinitionName;
                        modulecontent.Version = moduledefinition.Version;
                        modulecontent.Settings = settings.Where(item => !item.IsPrivate).ToDictionary(x => x.SettingName, x => x.SettingValue);
                        modulecontent.Content = "";

                        if (moduledefinition.ServerManagerType != "")
                        {
                            Type moduletype = Type.GetType(moduledefinition.ServerManagerType);
                            if (moduletype != null && moduletype.GetInterface(nameof(IPortable)) != null)
                            {
                                try
                                {
                                    module.IPortableContext = IPortableContext;
                                    module.Settings = settings.ToDictionary(x => x.SettingName, x => x.SettingValue);
                                    var moduleobject = ActivatorUtilities.CreateInstance(_serviceProvider, moduletype);
                                    modulecontent.Content = await ((IPortable)moduleobject).ExportModuleAsync(module);
                                }
                                catch (Exception ex)
                                {
                                    // An error in the module's IPortable: the export carries no content, and the error is logged rather
                                    // than swallowed (BL-251, BL-239's B1-5).
                                    _serviceProvider.GetService<ILogManager>()?.Log(LogLevel.Error, this, LogFunction.Read, ex, "Error Exporting Content For Module {ModuleId} Through {ManagerType}", module.ModuleId, moduledefinition.ServerManagerType);
                                }
                            }
                        }

                        content = JsonSerializer.Serialize(modulecontent);
                    }
                }
            }
            catch
            {
                // error occurred during export
            }

            return content;
        }

        public async Task<bool> ImportModuleAsync(int moduleId, string content)
        {
            Module module = await GetModuleAsync(moduleId);
            return await ImportModuleAsync(module, content, "Import Module");
        }

        public async Task<bool> ImportModuleAsync(Module module, string content, string IPortableContext)
        {
            bool success = false;
            try
            {
                if (module != null)
                {
                    List<ModuleDefinition> moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(module.SiteId)).ToList();
                    ModuleDefinition moduledefinition = moduledefinitions.Where(item => item.ModuleDefinitionName == module.ModuleDefinitionName).FirstOrDefault();
                    if (moduledefinition != null)
                    {
                        var modulecontent = new ModuleContent();
                        if (content.StartsWith("{") && content.EndsWith("}"))
                        {
                            // content was exported as a serialized ModuleContent object
                            modulecontent = JsonSerializer.Deserialize<ModuleContent>(content.Replace("\n", ""));
                        }
                        else
                        {
                            // raw content
                            modulecontent.ModuleDefinitionName = moduledefinition.ModuleDefinitionName;
                            modulecontent.Version = moduledefinition.Version;
                            modulecontent.Content = content;
                        }
                        if (modulecontent.ModuleDefinitionName == moduledefinition.ModuleDefinitionName)
                        {
                            if (modulecontent.Settings != null)
                            {
                                var settings = await _settings.GetSettingsAsync(EntityNames.Module, module.ModuleId);
                                foreach (var kvp in modulecontent.Settings)
                                {
                                    var setting = settings.FirstOrDefault(item => item.SettingName == kvp.Key);
                                    if (setting == null)
                                    {
                                        setting = new Setting { EntityName = EntityNames.Module, EntityId = module.ModuleId, SettingName = kvp.Key, SettingValue = kvp.Value, IsPrivate = false };
                                        await _settings.AddSettingAsync(setting);
                                    }
                                    else
                                    {
                                        if (setting.SettingValue != kvp.Value)
                                        {
                                            setting.SettingValue = kvp.Value;
                                            await _settings.UpdateSettingAsync(setting);
                                        }
                                    }
                                }
                            }

                            if (moduledefinition.ServerManagerType != "")
                            {
                                Type moduletype = Type.GetType(moduledefinition.ServerManagerType);
                                if (moduletype != null && moduletype.GetInterface(nameof(IPortable)) != null)
                                {
                                    try
                                    {
                                        module.IPortableContext = IPortableContext;
                                        module.Settings = (await _settings.GetSettingsAsync(EntityNames.Module, module.ModuleId)).ToDictionary(x => x.SettingName, x => x.SettingValue);
                                        var moduleobject = ActivatorUtilities.CreateInstance(_serviceProvider, moduletype);
                                        await ((IPortable)moduleobject).ImportModuleAsync(module, modulecontent.Content, modulecontent.Version);
                                        success = true;
                                    }
                                    catch 
                                    {
                                        // error in IPortable implementation
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // error occurred during import
            }

            return success;
        }
    }
}
