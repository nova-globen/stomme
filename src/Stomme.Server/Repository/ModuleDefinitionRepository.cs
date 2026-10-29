using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface IModuleDefinitionRepository
    {
        Task<IEnumerable<ModuleDefinition>> GetModuleDefinitionsAsync();
        Task<IEnumerable<ModuleDefinition>> GetModuleDefinitionsAsync(int siteId);
        Task<ModuleDefinition> GetModuleDefinitionAsync(int moduleDefinitionId, int siteId);
        Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition);
        Task DeleteModuleDefinitionAsync(int moduleDefinitionId);
        Task<List<string>> GetAssembliesAsync(int siteId);
    }

    /// <summary>
    /// The ModuleDefinition cluster on the persistence seam — the MASTER half of the master-bookkeeping
    /// wave. ModuleDefinition-table access goes through <see cref="IModuleDefinitionStore"/> (writes) and the
    /// MASTER read path <see cref="IMasterStoreSessionFactory"/> (reads); this class holds no <c>DbContext</c>
    /// and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MASTER, not tenant (D23's master half): ModuleDefinition is installation-wide — the registry of installed
    /// module types, read during module discovery at startup — so reads go through
    /// <see cref="IMasterStoreSessionFactory"/> (NOT the tenant <see cref="IFrameworkStoreSessionFactory"/>) and
    /// writes through a store bound to <see cref="MasterDBContext"/>. The table persists only six columns
    /// (module_definition_name, name, description, categories, the version string and the audit quartet); the rest
    /// of the <see cref="ModuleDefinition"/> contract is rebuilt here from the loaded module assemblies and from
    /// Settings/Permissions.
    /// </para>
    /// <para>
    /// Reads NEVER go through the store: a query session opens its own context, so the store's change
    /// tracker stays clean for the read-then-write module-discovery sync pass in
    /// <see cref="ProcessModuleDefinitionsAsync"/> (read the DB definitions through a session, then Add/Delete
    /// through the store). The one read through the store is <see cref="DeleteModuleDefinitionAsync"/>, which
    /// deletes the very instance it loaded — tracking is what we want there.
    /// </para>
    /// <para>
    /// Honest negatives (D52 survey, measured): the harvested startup path passes a <c>-1</c> sentinel siteId,
    /// but it is a C# BRANCH (<c>if (siteId != -1)</c>), never a query filter — the DB read is an unconditional
    /// <c>Query&lt;ModuleDefinitionAggregate&gt;()</c>, so the D45 <c>-1</c> fold cannot bite. And no navigation
    /// into this aggregate exists anywhere — <c>Module.ModuleDefinition</c> is <c>[NotMapped]</c> — so no reader
    /// breaks.
    /// </para>
    /// </remarks>
    public class ModuleDefinitionRepository : IModuleDefinitionRepository
    {
        private readonly IModuleDefinitionStore _store;
        private readonly IMasterStoreSessionFactory _sessions;
        private readonly ICacheManager _cache;
        private readonly IPermissionRepository _permissions;
        private readonly ITenantManager _tenants;
        private readonly ISettingRepository _settings;
        private readonly ILogManager _logger;
        private readonly string settingprefix = "SiteEnabled:";

        // The logger is OPTIONAL at the constructor so the seam's unit fixtures compose this repository without
        // one, and so a host that has not registered ILogManager still boots. It is used for one thing only:
        // naming a permission-key reconciliation that FAILED. The admin-page reconciliation lesson is exactly
        // this - CreatePagesAsync logs its per-page failure only when an alias was passed, so calling it the
        // other way made every failure silent (CLAUDE.md).
        public ModuleDefinitionRepository(IModuleDefinitionStore store, IMasterStoreSessionFactory sessions, ICacheManager cache, IPermissionRepository permissions, ITenantManager tenants, ISettingRepository settings, ILogManager logger = null)
        {
            _store = store;
            _sessions = sessions;
            _cache = cache;
            _permissions = permissions;
            _tenants = tenants;
            _settings = settings;
            _logger = logger;
        }

        // The read path on the seam: reads go through a MASTER query session, never the store, so the
        // store's tracker stays clean for the read-then-write module-discovery sync pass. The IQueryable is
        // materialized inside this method, so it never leaves the repository. Plain LINQ rather than EF's
        // async operators, which would bind these reads to the relational provider.
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

        public async Task<IEnumerable<ModuleDefinition>> GetModuleDefinitionsAsync()
        {
            return await LoadModuleDefinitionsAsync(-1); // used only during startup
        }

        public async Task<IEnumerable<ModuleDefinition>> GetModuleDefinitionsAsync(int siteId)
        {
            return await LoadModuleDefinitionsAsync(siteId);
        }

        public async Task<ModuleDefinition> GetModuleDefinitionAsync(int moduleDefinitionId, int siteId)
        {
            List<ModuleDefinition> moduledefinitions = await LoadModuleDefinitionsAsync(siteId);
            return moduledefinitions.Find(item => item.ModuleDefinitionId == moduleDefinitionId);
        }

        public async Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition)
        {
            // The harvested code set _db.Entry(moduleDefinition).State = Modified and saved; on the seam that is
            // an Update through the store, which attaches the mapped columns (ModuleDefinitionName/Name/
            // Description/Categories/version/audit) as Modified. DbContextUtils re-stamps ModifiedBy/On and
            // preserves the CreatedBy/On the definition round-tripped.
            await _store.UpdateAsync(ModuleDefinitionMapper.ToAggregate(moduleDefinition, assignId: true));
            // allowDeclaredKeyRows: the definition's PermissionList carries its LIFTED keys as foreign rows, and
            // ModuleDefinitionController.Put has already refused any row that is not a declared, non-framework key.
            await _permissions.UpdatePermissionsAsync(moduleDefinition.SiteId, EntityNames.ModuleDefinition, moduleDefinition.ModuleDefinitionId, moduleDefinition.PermissionList, allowDeclaredKeyRows: true);

            var settingname = $"{settingprefix}{(await _tenants.GetAliasAsync()).SiteKey}";
            var setting = await _settings.GetSettingAsync(EntityNames.ModuleDefinition, moduleDefinition.ModuleDefinitionId, settingname);
            if (setting == null)
            {
                await _settings.AddSettingAsync(new Setting { EntityName = EntityNames.ModuleDefinition, EntityId = moduleDefinition.ModuleDefinitionId, SettingName = settingname, SettingValue = moduleDefinition.IsEnabled.ToString(), IsPrivate = true });
            }
            else
            {
                setting.SettingValue = moduleDefinition.IsEnabled.ToString();
                await _settings.UpdateSettingAsync(setting);
            }

            await _cache.RemoveCacheAsync(await _tenants.GetAliasAsync(), "ModuleDefinitions");
        }

        public async Task DeleteModuleDefinitionAsync(int moduleDefinitionId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it loaded,
            // so tracking is what we want and no second instance is ever attached. The harvested code threw a
            // NullReferenceException for an unknown id (Remove(null)); returning silently matches DeleteTheme/
            // DeleteJob's strictly-safer shape.
            var aggregate = await _store.GetByIdAsync(moduleDefinitionId);
            await _settings.DeleteSettingsAsync(EntityNames.ModuleDefinition, moduleDefinitionId);
            if (aggregate != null)
            {
                await _store.DeleteAsync(aggregate);
            }
            await _cache.RemoveCacheAsync(await _tenants.GetAliasAsync(), "ModuleDefinitions");
        }

        public async Task<List<string>> GetAssembliesAsync(int siteId)
        {
            var assemblies = new List<string>();
            foreach (var moduleDefinition in await GetModuleDefinitionsAsync(siteId))
            {
                if (moduleDefinition.IsEnabled)
                {
                    // build list of assemblies for site
                    if (!assemblies.Contains(moduleDefinition.AssemblyName))
                    {
                        assemblies.Add(moduleDefinition.AssemblyName);
                    }
                    if (!string.IsNullOrEmpty(moduleDefinition.Dependencies))
                    {
                        foreach (var assembly in moduleDefinition.Dependencies.Replace(".dll", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Reverse())
                        {
                            if (!assemblies.Contains(assembly.Trim()))
                            {
                                assemblies.Insert(0, assembly.Trim());
                            }
                        }
                    }
                }
            }
            return assemblies;
        }

        public async Task<List<ModuleDefinition>> LoadModuleDefinitionsAsync(int siteId)
        {
            // get module definitions
            List<ModuleDefinition> moduleDefinitions;
            if (siteId != -1)
            {
                moduleDefinitions = await _cache.GetCacheAsync(await _tenants.GetAliasAsync(), "ModuleDefinitions", async entry =>
                {
                    return await ProcessModuleDefinitionsAsync(siteId);
                }, TimeSpan.MaxValue, TimeSpan.MinValue); // skip distributed caching as app restart must reload modules
            }
            else // called during startup without any tenant context
            {
                return await ProcessModuleDefinitionsAsync(-1);
            }

            return moduleDefinitions;
        }

        private async Task<List<ModuleDefinition>> ProcessModuleDefinitionsAsync(int siteId)
        {
            // get module assemblies 
            List<ModuleDefinition> ModuleDefinitions = LoadModuleDefinitionsFromAssemblies();

            // get module definitions in database — read through a MASTER query session (untracked), mapped
            // to the ModuleDefinition contract so the sync algorithm below is unchanged from the harvested code.
            List<ModuleDefinition> moduledefinitions = (await QueryAsync(session => session.Query<ModuleDefinitionAggregate>().ToList())).ConvertAll(ModuleDefinitionMapper.ToModel);

            // sync module assemblies with database
            foreach (ModuleDefinition ModuleDefinition in ModuleDefinitions)
            {
                // manage releaseversions in cases where it was not provided or is lower than the module version
                if (string.IsNullOrEmpty(ModuleDefinition.ReleaseVersions) || (!string.IsNullOrEmpty(ModuleDefinition.Version) && Version.Parse(ModuleDefinition.Version).CompareTo(Version.Parse(ModuleDefinition.ReleaseVersions.Split(',').Last())) > 0))
                {
                    ModuleDefinition.ReleaseVersions = ModuleDefinition.Version;
                }

                ModuleDefinition moduledefinition = moduledefinitions.Where(item => item.ModuleDefinitionName == ModuleDefinition.ModuleDefinitionName).FirstOrDefault();

                if (moduledefinition == null)
                {
                    // new module definition — insert through the store, then copy the generated id and the
                    // DbContextUtils audit stamps back onto the model, which the "load db properties" block below
                    // reads. Only ModuleDefinitionName is set at insert, exactly as the harvested code did; the
                    // user-customizable columns stay default until UpdateModuleDefinitionAsync writes them.
                    moduledefinition = new ModuleDefinition { ModuleDefinitionName = ModuleDefinition.ModuleDefinitionName };
                    var aggregate = ModuleDefinitionMapper.ToAggregate(moduledefinition, assignId: false);
                    await _store.AddAsync(aggregate);
                    ModuleDefinitionMapper.CopyPersistedValues(aggregate, moduledefinition);
                    // version is explicitly not set because it is updated as part of module migrations at startup
                    ModuleDefinition.Version = "";
                }
                else
                {
                    // override user customizable property values
                    ModuleDefinition.Name = (!string.IsNullOrEmpty(moduledefinition.Name)) ? moduledefinition.Name : ModuleDefinition.Name;
                    ModuleDefinition.Description = (!string.IsNullOrEmpty(moduledefinition.Description)) ? moduledefinition.Description : ModuleDefinition.Description;
                    ModuleDefinition.Categories = (!string.IsNullOrEmpty(moduledefinition.Categories)) ? moduledefinition.Categories : ModuleDefinition.Categories;
                    // get current version
                    ModuleDefinition.Version = moduledefinition.Version;

                    // remove module definition from list as it is already synced
                    moduledefinitions.Remove(moduledefinition);
                }

                // load db properties
                ModuleDefinition.ModuleDefinitionId = moduledefinition.ModuleDefinitionId;
                ModuleDefinition.CreatedBy = moduledefinition.CreatedBy;
                ModuleDefinition.CreatedOn = moduledefinition.CreatedOn;
                ModuleDefinition.ModifiedBy = moduledefinition.ModifiedBy;
                ModuleDefinition.ModifiedOn = moduledefinition.ModifiedOn;
                ModuleDefinition.Fingerprint = Stomme.Infrastructure.ResourceFingerprint.Of(moduledefinition.ModifiedOn, ModuleDefinition.Version);
            }

            // any remaining module definitions are orphans
            foreach (ModuleDefinition moduledefinition in moduledefinitions)
            {
                // delete through the store (attach a fresh aggregate, since the row was read via session).
                await _store.DeleteAsync(ModuleDefinitionMapper.ToAggregate(moduledefinition, assignId: true));
            }

            if (siteId != -1)
            {
                var siteKey = (await _tenants.GetAliasAsync()).SiteKey;
                var dbType = (await _tenants.GetTenantAsync()).DBType;

                // get all module definition permissions for site
                List<Permission> permissions = (await _permissions.GetPermissionsAsync(siteId, EntityNames.ModuleDefinition)).ToList();

                // get settings for site
                var settings = (await _settings.GetSettingsAsync(EntityNames.ModuleDefinition)).ToList();

                // populate module definition site settings and permissions
                foreach (ModuleDefinition moduledefinition in ModuleDefinitions)
                {
                    moduledefinition.SiteId = siteId;

                    var setting = settings.FirstOrDefault(item => item.EntityId == moduledefinition.ModuleDefinitionId && item.SettingName == $"{settingprefix}{siteKey}");
                    if (setting != null)
                    {
                       moduledefinition.IsEnabled = bool.Parse(setting.SettingValue);
                    }
                    else
                    {
                        moduledefinition.IsEnabled = moduledefinition.IsAutoEnabled;
                    }

                    // check if module supports tenant database
                    if (moduledefinition.IsEnabled)
                    {
                        moduledefinition.IsEnabled = string.IsNullOrEmpty(moduledefinition.Databases);
                        if (!string.IsNullOrEmpty(moduledefinition.Databases))
                        {
                            foreach (var database in moduledefinition.Databases.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (dbType.ToLower().Contains(database.ToLower()))
                                {
                                    moduledefinition.IsEnabled = true;
                                }
                            }
                        }
                    }

                    if (permissions.Count == 0)
                    {
                        // no module definition permissions exist for this site
                        moduledefinition.PermissionList = ClonePermissions(siteId, moduledefinition.PermissionList);
                        // allowDeclaredKeyRows: the seed carries the lifted-key rows AppendDeclaredKeyPermissions added.
                        await _permissions.UpdatePermissionsAsync(siteId, EntityNames.ModuleDefinition, moduledefinition.ModuleDefinitionId, moduledefinition.PermissionList, allowDeclaredKeyRows: true);
                    }
                    else
                    {
                        if (permissions.Any(item => item.EntityId == moduledefinition.ModuleDefinitionId))
                        {
                            moduledefinition.PermissionList = permissions.Where(item => item.EntityId == moduledefinition.ModuleDefinitionId).ToList();
                        }
                        else
                        {
                            // permissions for module definition do not exist for this site
                            moduledefinition.PermissionList = ClonePermissions(siteId, moduledefinition.PermissionList);
                            // allowDeclaredKeyRows: as above - the seed carries the definition's lifted-key rows.
                            await _permissions.UpdatePermissionsAsync(siteId, EntityNames.ModuleDefinition, moduledefinition.ModuleDefinitionId, moduledefinition.PermissionList, allowDeclaredKeyRows: true);
                        }
                    }
                }

                // The permission-KEY half of the per-site permissions. Order matters: reconcile
                // first so a key added by a newer release HAS rows, then merge, so what the grid renders is
                // what the guard will decide from.
                await ReconcileDeclaredKeyPermissionsAsync(siteId, ModuleDefinitions);
                await MergeDeclaredEntityPermissionsAsync(siteId, ModuleDefinitions);

                // clean up any orphaned permissions
                var ids = new HashSet<int>(ModuleDefinitions.Select(item => item.ModuleDefinitionId));
                foreach (var permission in permissions.Where(item => !ids.Contains(item.EntityId)))
                {
                    try
                    {
                        await _permissions.DeletePermissionAsync(permission.PermissionId);
                    }
                    catch
                    {
                        // multi-threading can cause a race condition to occur
                    }
                }
            }

            return ModuleDefinitions;
        }

        /// <summary>
        /// The DB-free half of module discovery: reflects the loaded module assemblies into their
        /// <see cref="ModuleDefinition"/>s (IModule properties incl. <see cref="ModuleDefinition.ConfigSettingsType"/>
        /// and the derived <see cref="ModuleDefinition.ModuleDefinitionName"/>) WITHOUT touching the master DB, so the
        /// installer can enumerate bundled modules while the app is still in the uninstalled state. The
        /// <c>ModuleDefinitionName</c> it computes is byte-identical to the one the install pipeline persists, so a
        /// name is a stable key from the installer's config-collection through to the master settings write.
        /// <para>
        /// STATIC and deliberately OFF <see cref="IModuleDefinitionRepository"/>: it does no store I/O, and D33 bans
        /// sync members on store-touching interfaces — genuinely synchronous work belongs off the interface (the same
        /// treatment <c>FilterModuleDefinition</c> gets).
        /// </para>
        /// </summary>
        public static List<ModuleDefinition> GetModuleDefinitionsFromAssemblies()
        {
            return LoadModuleDefinitionsFromAssemblies();
        }

        private static List<ModuleDefinition> LoadModuleDefinitionsFromAssemblies()
        {
            List<ModuleDefinition> moduleDefinitions = new List<ModuleDefinition>();

            // iterate through Stomme module assemblies
            var assemblies = AppDomain.CurrentDomain.GetStommeAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                if (System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), Utilities.GetTypeName(assembly.FullName) + ".dll")))
                {
                    moduleDefinitions = LoadModuleDefinitionsFromAssembly(moduleDefinitions, assembly);
                }
            }

            return moduleDefinitions;
        }

        private static List<ModuleDefinition> LoadModuleDefinitionsFromAssembly(List<ModuleDefinition> moduledefinitions, Assembly assembly)
        {
            ModuleDefinition moduledefinition;

            // A Name declared through IModule is authoritative — the module.<id>.enabled config knob
            // (ModuleFeatureFlags) derives from it, so letting the Index control's Title overwrite it silently
            // re-keys the flag under a name the author never wrote. The Title remains the name source for
            // convention-only modules (no IModule), which have no declared name. Creation and title
            // application for a module always happen within one call (its controls live in its own assembly),
            // so this set never needs to survive across assemblies.
            var declaredNames = new HashSet<string>();

            Type[] moduletypes = assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(IModule))).ToArray();
            Type[] modulecontroltypes = assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(IModuleControl))).ToArray();

            foreach (Type modulecontroltype in modulecontroltypes)
            {
                // Check if type should be ignored
                if (modulecontroltype.IsStommeIgnore()) continue;

                // create namespace root typename
                string qualifiedModuleType = modulecontroltype.Namespace + ", " + modulecontroltype.Assembly.GetName().Name;

                int index = moduledefinitions.FindIndex(item => item.ModuleDefinitionName == qualifiedModuleType);
                if (index == -1)
                {
                    // determine if this component is part of a module which implements IModule
                    Type moduletype = moduletypes.FirstOrDefault(item => item.Namespace == modulecontroltype.Namespace);

                    if (moduletype != null)
                    {
                        // get property values from IModule
                        var moduleobject = Activator.CreateInstance(moduletype) as IModule;
                        moduledefinition = moduleobject.ModuleDefinition;
                        if (!string.IsNullOrEmpty(moduledefinition.Name))
                        {
                            declaredNames.Add(qualifiedModuleType);
                        }
                    }
                    else
                    {
                        // set default property values
                        moduledefinition = new ModuleDefinition
                        {
                            Name = Utilities.GetTypeNameLastSegment(modulecontroltype.Namespace, 0),
                            Description = Utilities.GetTypeNameLastSegment(modulecontroltype.Namespace, 0),
                            Categories = ((qualifiedModuleType.StartsWith("Stomme.Modules.Admin.")) ? "Admin" : "")
                        };
                    }

                    // set internal properties
                    moduledefinition.ModuleDefinitionName = qualifiedModuleType;
                    moduledefinition.ControlTypeTemplate = modulecontroltype.Namespace + "." + Constants.ActionToken + ", " + modulecontroltype.Assembly.GetName().Name;
                    moduledefinition.AssemblyName = assembly.GetName().Name;
                    if (moduledefinition.Resources != null)
                    {
                        foreach (var resource in moduledefinition.Resources)
                        {
                            if (!string.IsNullOrEmpty(resource.Url) && resource.Url.StartsWith("~"))
                            {
                                resource.Url = resource.Url.Replace("~", "/Modules/" + Utilities.GetTypeName(moduledefinition.ModuleDefinitionName) + "/").Replace("//", "/");
                            }
                        }
                    }

                    moduledefinition.IsPortable = false;
                    if (!string.IsNullOrEmpty(moduledefinition.ServerManagerType))
                    {
                        Type servertype = Type.GetType(moduledefinition.ServerManagerType);
                        if (servertype != null && servertype.GetInterface(nameof(IPortable)) != null)
                        {
                            moduledefinition.IsPortable = true;
                        }
                    }

                    if (string.IsNullOrEmpty(moduledefinition.Categories))
                    {
                        moduledefinition.Categories = "Common";
                    }

                    // default permissions
                    if (moduledefinition.Categories == "Admin")
                    {
                        var shortName = moduledefinition.ModuleDefinitionName.Replace("Stomme.Modules.Admin.", "").Replace(", Stomme.Client", "");
                        if (Constants.DefaultHostModuleTypes.Contains(shortName))
                        {
                            moduledefinition.PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.Utilize, RoleNames.Host, true)
                            };
                        }
                        else
                        {
                            moduledefinition.PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.Utilize, RoleNames.Admin, true)
                            };
                        }
                    }
                    else
                    {
                        moduledefinition.PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.Utilize, RoleNames.Admin, true),
                            new Permission(PermissionNames.Utilize, RoleNames.Registered, true)
                        };
                    }

                    AppendDeclaredKeyPermissions(moduledefinition);

                    Debug.WriteLine($"Stomme Info: Registering Module {moduledefinition.ModuleDefinitionName}");
                    moduledefinitions.Add(moduledefinition);
                    index = moduledefinitions.FindIndex(item => item.ModuleDefinitionName == qualifiedModuleType);
                }

                moduledefinition = moduledefinitions[index];

                // actions
                var modulecontrolobject = Activator.CreateInstance(modulecontroltype) as IModuleControl;
                string actions = modulecontrolobject.Actions;
                if (!string.IsNullOrEmpty(actions))
                {
                    foreach (string action in actions.Split(','))
                    {
                        moduledefinition.ControlTypeRoutes += (action + "=" + modulecontroltype.FullName + ", " + modulecontroltype.Assembly.GetName().Name + ";");
                    }
                }
                // module title — names a convention-only module; never overrides an IModule-declared Name
                if (modulecontroltype.Name == Constants.DefaultAction && !string.IsNullOrEmpty(modulecontrolobject.Title) && !declaredNames.Contains(qualifiedModuleType))
                {
                    moduledefinition.Name = modulecontrolobject.Title;
                    moduledefinition.Description = "Manage " + moduledefinition.Name;
                }

                // check for Page attribute
                var routeAttributes = modulecontroltype.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>();
                if (routeAttributes != null && routeAttributes.Any())
                {
                    var route = routeAttributes.First().Template;
                    if (!string.IsNullOrEmpty(route))
                    {
                        // @page "/path" or @page "alias/path" (note that nested paths are not permitted)
                        var pageTemplate = new PageTemplate();
                        if (route.StartsWith("/"))
                        {
                            pageTemplate.AliasName = "*"; // all sites
                            pageTemplate.Path = route.Substring(1);
                        }
                        else // route contains an alias name
                        {
                            var lastSlash = route.LastIndexOf('/');
                            pageTemplate.AliasName = route.Substring(0, lastSlash);
                            pageTemplate.Path = route.Substring(lastSlash + 1);
                        }
                        // path can contain a suffix indicating the page order "Path:1"
                        if (pageTemplate.Path.Contains(":"))
                        {
                            pageTemplate.Order = int.Parse(pageTemplate.Path.Split(":")[1]);
                            pageTemplate.Path = pageTemplate.Path.Split(":")[0];
                        }
                        pageTemplate.Version = "*";
                        pageTemplate.Update = false;
                        pageTemplate.PageTemplateModules = new List<PageTemplateModule>();

                        // check for Authorize attributes
                        var permissionList = new List<Permission>();
                        var authorizeAttributes = modulecontroltype.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>();
                        if (authorizeAttributes != null && authorizeAttributes.Any())
                        {
                            foreach (var authorizeAttribute in authorizeAttributes)
                            {
                                if (string.IsNullOrEmpty(authorizeAttribute.Roles))
                                {
                                    // [Authorize]
                                    permissionList.Add(new Permission(PermissionNames.View, RoleNames.Registered, true));
                                }
                                else
                                {
                                    // [Authorize(Roles = "role1, permission:role2")]
                                    foreach (var role in authorizeAttribute.Roles.Split(','))
                                    {
                                        var permissionName = PermissionNames.View;
                                        var roleName = role.Trim();
                                        if (roleName.Contains(":"))
                                        {
                                            permissionName = roleName.Substring(0, roleName.IndexOf(":") - 1);
                                            roleName = roleName.Substring(roleName.IndexOf(":") + 1);
                                        }
                                        permissionList.Add(new Permission(permissionName, roleName, true));
                                    }
                                }
                            }
                        }
                        else
                        {
                            // view permission 
                            permissionList.Add(new Permission(PermissionNames.View, RoleNames.Everyone, true));
                        }

                        // assign page permissions
                        foreach (var permission in permissionList)
                        {
                            if (!pageTemplate.PermissionList.Any(item => item.PermissionName == permission.PermissionName && item.RoleName == permission.RoleName))
                            {
                                pageTemplate.PermissionList.Add(permission);
                            }
                        }

                        // add module instance
                        var pageTemplateModule = new PageTemplateModule();
                        pageTemplateModule.Title = route.Substring(1);
                        // assign module permissions
                        foreach (var permission in permissionList)
                        {
                            if (!pageTemplateModule.PermissionList.Any(item => item.PermissionName == permission.PermissionName && item.RoleName == permission.RoleName))
                            {
                                pageTemplateModule.PermissionList.Add(permission.Clone());
                            }
                        }
                        pageTemplate.PageTemplateModules.Add(pageTemplateModule);

                        // if PageTemplates was not already defined in IModule
                        if (moduledefinition.PageTemplates == null)
                        {
                            moduledefinition.PageTemplates = new List<PageTemplate> { pageTemplate };
                        }
                    }
                }

                moduledefinitions[index] = moduledefinition;
            }

            // process modules without UI components
            foreach (var moduletype in moduletypes.Where(m1 => !modulecontroltypes.Any(m2 => m1.Namespace == m2.Namespace)))
            {
                // get property values from IModule
                var moduleobject = Activator.CreateInstance(moduletype) as IModule;
                moduledefinition = moduleobject.ModuleDefinition;
                moduledefinition.ModuleDefinitionName = moduletype.Namespace + ", " + moduletype.Assembly.GetName().Name;
                moduledefinition.AssemblyName = assembly.GetName().Name;
                moduledefinition.Categories = "Headless";
                moduledefinition.PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Utilize, RoleNames.Host, true)
                };
                AppendDeclaredKeyPermissions(moduledefinition);
                moduledefinitions.Add(moduledefinition);
            }

            return moduledefinitions;
        }

        /// <summary>
        /// Appends the Permission rows a module's declared permission KEYS imply to the definition's default
        /// permission list, at SCAN time: one row per (key, default role), carrying the key's
        /// resource as the EntityName and <see cref="PermissionKeys.KeyEntityId"/> as the EntityId - the key
        /// names a resource, not an instance of one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// KEYS ONLY, deliberately. The same 3-segment declaration form has always carried the framework's own
        /// API policies (<c>User:Write:Administrators</c> on the stock Users module), and those have never been
        /// materialised as rows - the dynamic policy handler falls back to role membership for them. Seeding
        /// them here would put new rows into a stock install's permission table and change its behaviour, which
        /// D99 requires stay byte-for-byte unchanged. <see cref="PermissionKeys.ParseKeys"/> is the
        /// discriminator: an entry whose resource is an <see cref="EntityNames"/> constant is a framework
        /// policy and is left alone.
        /// </para>
        /// <para>
        /// The list these rows join is the DEFAULT list a fresh site materialises through
        /// <c>ClonePermissions</c> + <c>UpdatePermissionsAsync</c>, which stamps EntityId -1 for any row whose
        /// EntityName differs from the entity being written and resolves the RoleId from the role name.
        /// </para>
        /// </remarks>
        private static void AppendDeclaredKeyPermissions(ModuleDefinition moduledefinition)
        {
            foreach (var declaration in PermissionKeys.ParseKeys(moduledefinition.PermissionNames))
            {
                foreach (var role in declaration.Roles)
                {
                    if (!moduledefinition.PermissionList.Any(item => item.EntityName == declaration.Resource
                        && item.PermissionName == declaration.Operation && item.RoleName == role))
                    {
                        moduledefinition.PermissionList.Add(new Permission(-1, declaration.Resource, PermissionKeys.KeyEntityId, declaration.Operation, role, null, true));
                    }
                }
            }
        }

        /// <summary>
        /// The ADDITIVE per-site reconciliation of declared permission keys (F7): a key that has
        /// NO rows on this site gets its declared default rows; a key that already has rows is not touched.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it exists.</b> The per-definition materialisation above runs only when the definition has no
        /// rows on the site at all, so a key added by a LATER release never reaches an existing site - and the
        /// bridge DENIES a key with no rows, so the new capability would be dead for everyone, administrators
        /// included. This is the admin-page upgrade class, and it gets the same answer.
        /// </para>
        /// <para>
        /// <b>Only-missing is the whole contract.</b> Re-processing a key that already has rows would revert an
        /// operator's grant to a custom role on every boot. The existence test is therefore per (resource,
        /// operation) at <see cref="PermissionKeys.KeyEntityId"/>, and the write goes through
        /// <c>UpdatePermissionsAsync</c> - which resolves the RoleId a bare AddPermission would leave null (a
        /// row with neither RoleId nor UserId is DROPPED on read, so it would authorize nobody and be invisible).
        /// Because the key has no rows at that point, that call's delete pass has nothing to delete.
        /// </para>
        /// <para>
        /// A failure is caught PER KEY and logged by name: a reconciliation that throws would take down the
        /// whole module-definition read path, and one that swallowed silently would be indistinguishable from
        /// one that had nothing to do.
        /// </para>
        /// <para>
        /// READ-THEN-WRITE WITH NO LOCK, deliberately, exactly like the orphan cleanup below it: two requests
        /// racing the same cache miss can both see no rows and both insert, so the key can end up with
        /// duplicate grants. The DECISION is unaffected (deny-first, then grant, over a list that now carries
        /// the same grant twice); the table is untidy. A lock across a per-site read path costs more than the
        /// defect.
        /// </para>
        /// </remarks>
        private async Task ReconcileDeclaredKeyPermissionsAsync(int siteId, List<ModuleDefinition> moduleDefinitions)
        {
            foreach (var moduledefinition in moduleDefinitions)
            {
                // A hand-written declaration (a module installed as a .nupkg never passes through
                // PermissionKeys.Declare, and the reflective gate only sees in-tree assemblies) can carry an
                // entry ParseKeys has to drop - a half over the column bound, say. The key then has no rows
                // and is denied everywhere, which is the fail-closed answer but an invisible one.
                foreach (var invalid in PermissionKeys.InvalidKeyEntries(moduledefinition.PermissionNames))
                {
                    _logger?.Log(Shared.LogLevel.Error, this, LogFunction.Security,
                        "Permission Declaration {Entry} By {ModuleDefinitionName} Is Not A Usable Key - It Seeds No Row And Therefore DENIES Everyone",
                        invalid, moduledefinition.ModuleDefinitionName);
                    Debug.WriteLine($"Stomme Error: Unusable Permission Declaration {invalid} In {moduledefinition.ModuleDefinitionName}");
                }

                foreach (var declaration in PermissionKeys.ParseKeys(moduledefinition.PermissionNames))
                {
                    try
                    {
                        var existing = (await _permissions.GetPermissionsAsync(siteId, declaration.Resource, declaration.Operation))
                            .Where(item => item.EntityId == PermissionKeys.KeyEntityId)
                            .ToList();
                        if (existing.Count != 0)
                        {
                            continue;
                        }

                        var rows = declaration.Roles
                            .Select(role => new Permission(siteId, declaration.Resource, PermissionKeys.KeyEntityId, declaration.Operation, role, null, true))
                            .ToList();
                        if (rows.Count == 0)
                        {
                            continue;
                        }

                        await _permissions.UpdatePermissionsAsync(siteId, declaration.Resource, PermissionKeys.KeyEntityId, rows);

                        // A row whose RoleName resolves to no role on this site gets a null RoleId, and a row
                        // with neither RoleId nor UserId is DROPPED on read - so the key would still look
                        // ungranted and this reconciliation would insert the same row again on every boot. A
                        // declaration built by PermissionKeys.Declare cannot reach this (Administrators is
                        // always among its roles and exists on every site); a hand-written one can, and it is
                        // named here rather than left to grow silently.
                        var visible = (await _permissions.GetPermissionsAsync(siteId, declaration.Resource, declaration.Operation))
                            .Any(item => item.EntityId == PermissionKeys.KeyEntityId);
                        if (!visible)
                        {
                            _logger?.Log(Shared.LogLevel.Error, this, LogFunction.Security,
                                "Permission Key {Key} Declared By {ModuleDefinitionName} Grants Only Roles That Do Not Exist On Site {SiteId} ({Roles}) - The Key Remains Ungranted",
                                declaration.Key, moduledefinition.ModuleDefinitionName, siteId, string.Join(";", declaration.Roles));
                            Debug.WriteLine($"Stomme Error: Permission Key {declaration.Key} Remains Ungranted On Site {siteId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log(Shared.LogLevel.Error, this, LogFunction.Security,
                            "Error Reconciling Permission Key {Key} For Module {ModuleDefinitionName} On Site {SiteId} {Error}",
                            declaration.Key, moduledefinition.ModuleDefinitionName, siteId, ex.Message);
                        Debug.WriteLine($"Stomme Error: Reconciling Permission Key {declaration.Key} For {moduledefinition.ModuleDefinitionName} - {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Merges the FOREIGN-entity permission rows a definition declares back into its PermissionList (F9) -
        /// the ':'-merge <c>PageModuleRepository</c> already performs for a module instance, ported.
        /// </summary>
        /// <remarks>
        /// Without it the definition read path returns only the <see cref="EntityNames.ModuleDefinition"/> rows,
        /// so the admin grid renders an EMPTY cell for every declared key and a save - which submits what the
        /// grid holds - DELETES the seeded Administrators row through <c>UpdatePermissionsAsync</c>'s delete
        /// pass. The rows are looked up at <see cref="PermissionKeys.KeyEntityId"/>, which is what
        /// <c>UpdatePermissionsAsync</c> stamps a foreign-entity row with.
        /// <para>
        /// KEYS ONLY (<see cref="PermissionKeys.ParseKeys"/>), like the seed and the reconcile. Merging the
        /// framework's own 3-segment entries put FOREIGN rows into a list whose consumers filter on
        /// <c>PermissionName</c> ALONE (<c>IsAuthorizedAsync(User, PermissionNames.Utilize, PermissionList)</c>
        /// in Get/Post here, and the module View/Edit checks over the same shape) - so only the casing of
        /// 'utilize' vs 'Utilize' kept a merged row from deciding the wrong question. It also paid for a
        /// permission read per framework entry on every ModuleDefinitions cache miss, for pairs that can never
        /// have rows.
        /// </para>
        /// </remarks>
        private async Task MergeDeclaredEntityPermissionsAsync(int siteId, List<ModuleDefinition> moduleDefinitions)
        {
            foreach (var moduledefinition in moduleDefinitions)
            {
                foreach (var declaration in PermissionKeys.ParseKeys(moduledefinition.PermissionNames))
                {
                    var rows = (await _permissions.GetPermissionsAsync(siteId, declaration.Resource, declaration.Operation))
                        .Where(item => item.EntityId == PermissionKeys.KeyEntityId);

                    foreach (var row in rows)
                    {
                        if (!moduledefinition.PermissionList.Any(item => item.EntityName == row.EntityName
                            && item.PermissionName == row.PermissionName && item.RoleName == row.RoleName && item.UserId == row.UserId))
                        {
                            moduledefinition.PermissionList.Add(row);
                        }
                    }
                }
            }
        }

        private List<Permission> ClonePermissions(int siteId, List<Permission> permissionList)
        {
            var permissions = new List<Permission>();
            if (permissionList != null)
            {
                foreach (var p in permissionList)
                {
                    var permission = new Permission();
                    permission.SiteId = siteId;
                    permission.EntityName = p.EntityName;
                    permission.EntityId = p.EntityId;
                    permission.PermissionName = p.PermissionName;
                    permission.RoleId = null;
                    permission.RoleName = p.RoleName;
                    permission.UserId = p.UserId;
                    permission.IsAuthorized = p.IsAuthorized;
                    permissions.Add(permission);
                }
            }
            return permissions;
        }
    }

    /// <summary>
    /// The pure half of the ModuleDefinition surface: projection over a definition the caller ALREADY has.
    /// </summary>
    /// <remarks>
    /// <c>FilterModuleDefinition</c> lives here rather than on <see cref="IModuleDefinitionRepository"/>
    /// because it is a property-by-property copy of the object passed in — it performs no I/O and has
    /// nothing to await. D32 bans sync members on store-touching interfaces; the honest response for
    /// genuinely synchronous work is to take it OFF the store-touching interface, not to wrap it in
    /// <c>Task.FromResult</c> and call it async. An extension on <see cref="IModuleDefinitionRepository"/>
    /// keeps every existing call site (<c>_moduleDefinitions.FilterModuleDefinition(...)</c>) compiling
    /// unchanged.
    /// </remarks>
    public static class ModuleDefinitionRepositoryExtensions
    {
        /// <summary>
        /// Returns a copy of <paramref name="moduleDefinition"/> carrying only the client-side properties.
        /// </summary>
        public static ModuleDefinition FilterModuleDefinition(this IModuleDefinitionRepository repository, ModuleDefinition moduleDefinition)
        {
            ModuleDefinition ModuleDefinition = null;

            if (moduleDefinition != null)
            {
                // only include required client-side properties
                ModuleDefinition = new ModuleDefinition();
                ModuleDefinition.ModuleDefinitionId = moduleDefinition.ModuleDefinitionId;
                ModuleDefinition.SiteId = moduleDefinition.SiteId;
                ModuleDefinition.ModuleDefinitionName = moduleDefinition.ModuleDefinitionName;
                ModuleDefinition.Name = moduleDefinition.Name;
                ModuleDefinition.Runtimes = moduleDefinition.Runtimes;
                ModuleDefinition.PermissionNames = moduleDefinition.PermissionNames;
                ModuleDefinition.ControlTypeRoutes = moduleDefinition.ControlTypeRoutes;
                ModuleDefinition.DefaultAction = moduleDefinition.DefaultAction;
                ModuleDefinition.SettingsType = moduleDefinition.SettingsType;
                ModuleDefinition.ConfigSettingsType = moduleDefinition.ConfigSettingsType;
                ModuleDefinition.ControlTypeTemplate = moduleDefinition.ControlTypeTemplate;
                ModuleDefinition.IsPortable = moduleDefinition.IsPortable;
                ModuleDefinition.Resources = moduleDefinition.Resources;
                ModuleDefinition.IsEnabled = moduleDefinition.IsEnabled;
                ModuleDefinition.PackageName = moduleDefinition.PackageName;
                ModuleDefinition.Fingerprint = moduleDefinition.Fingerprint;
            }

            return ModuleDefinition;
        }
    }
}
