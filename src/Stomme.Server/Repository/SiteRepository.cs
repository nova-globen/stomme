using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Enums;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Module = Stomme.Models.Module;

namespace Stomme.Repository
{
    public interface ISiteRepository
    {
        Task<IEnumerable<Site>> GetSitesAsync();
        Task<Site> AddSiteAsync(Site site);
        Task<Site> UpdateSiteAsync(Site site);
        Task<Site> GetSiteAsync(int siteId);
        Task<Site> GetSiteAsync(int siteId, bool tracking);
        Task DeleteSiteAsync(int siteId);

        Task<string> ProcessSiteMigrationsAsync(Alias alias, Site site);
        Task<string> ProcessPageTemplatesAsync(Alias alias, Site site, string version);
        Task ProcessAdminPageTemplatesAsync(Alias alias, Site site);
        Task CreatePagesAsync(Site site, List<PageTemplate> pageTemplates, Alias alias);
    }

    /// <summary>
    /// The Site cluster on the persistence seam. Site-table access goes through
    /// <see cref="ISiteStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// The site-provisioning orchestration below (<see cref="CreateSiteAsync"/>, <see cref="CreatePagesAsync"/>,
    /// <see cref="ProcessSiteMigrationsAsync"/>, <see cref="ProcessPageTemplatesAsync"/>) is deliberately OUT of this
    /// cluster: it composes the Pages, Modules, Roles, Profiles, Folders and Settings clusters through their
    /// own repositories, which are still on the <c>IDatabase</c>/<c>TenantDBContext</c> path. Coexistence is
    /// the D3-safe plan; those calls move when their own clusters do.
    /// <para>
    /// WRITES resolve a FRESH store per write (<see cref="NewStore"/>), the per-write-store pattern of UrlMapping, Job
    /// and JobLog - BL-193, found 2026-10-02 clicking an admin page: an Interactive Server circuit is ONE DI scope
    /// for as long as the page is open, and the site's server twin (<c>SiteService</c>) writes through this repository
    /// from it. With the store constructor-injected, its one context kept the first save's aggregate and the second save
    /// attached another instance with the same key: "SiteAggregate cannot be tracked because another instance with the
    /// same key value is already being tracked", every second save of a settings page, in production since the Site
    /// cluster moved to the seam. <see cref="ISiteStore"/> is transient and owns its context, so each write starts with an
    /// empty change tracker. <c>SiteCircuitWriteTests</c> saves twice through one scope.
    /// </para>
    /// </remarks>
    public class SiteRepository : ISiteRepository
    {
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IRoleRepository _roleRepository;
        private readonly IProfileRepository _profileRepository;
        private readonly IFolderRepository _folderRepository;
        private readonly IPageRepository _pageRepository;
        private readonly IModuleRepository _moduleRepository;
        private readonly IPageModuleRepository _pageModuleRepository;
        private readonly IModuleDefinitionRepository _moduleDefinitionRepository;
        private readonly IThemeRepository _themeRepository;
        private readonly ISettingRepository _settingRepository;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfigurationRoot _config;
        private readonly ITenantManager _tenantManager;
        private readonly ICacheManager _cache;
        private readonly ILogManager _logger;
        private readonly IFeatureConfigurationSource _featureConfig;

        public SiteRepository(IFrameworkStoreSessionFactory sessions, IRoleRepository roleRepository, IProfileRepository profileRepository, IFolderRepository folderRepository, IPageRepository pageRepository,
            IModuleRepository moduleRepository, IPageModuleRepository pageModuleRepository, IModuleDefinitionRepository moduleDefinitionRepository, IThemeRepository themeRepository, ISettingRepository settingRepository,
            IServiceProvider serviceProvider, IConfigurationRoot config, ITenantManager tenantManager, ICacheManager cache, ILogManager logger, IFeatureConfigurationSource featureConfig)
        {
            _sessions = sessions;
            _roleRepository = roleRepository;
            _profileRepository = profileRepository;
            _folderRepository = folderRepository;
            _pageRepository = pageRepository;
            _moduleRepository = moduleRepository;
            _pageModuleRepository = pageModuleRepository;
            _moduleDefinitionRepository = moduleDefinitionRepository;
            _themeRepository = themeRepository;
            _settingRepository = settingRepository;
            _serviceProvider = serviceProvider;
            _config = config;
            _tenantManager = tenantManager;
            _cache = cache;
            _logger = logger;
            _featureConfig = featureConfig;
        }

        // A store over a fresh context for each write (see the class remarks).
        private ISiteStore NewStore() => _serviceProvider.GetRequiredService<ISiteStore>();

        public async Task<IEnumerable<Site>> GetSitesAsync()
        {
            return await _cache.GetCacheAsync(await GetCacheKeyAsync(), async entry => await LoadSitesAsync());
        }

        // The read path on the seam: IQuerySession.Query<T> is ordered and materialized here, so the
        // IQueryable never leaves this method. ToList/OrderBy are plain LINQ rather than EF Core's
        // ToListAsync, which would bind this read to the relational provider.
        private async Task<List<Site>> LoadSitesAsync()
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return session.Query<SiteAggregate>()
                    .OrderBy(item => item.Name)
                    .ToList()
                    .ConvertAll(SiteMapper.ToModel);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<Site> AddSiteAsync(Site site)
        {
            site.SiteGuid = Guid.NewGuid().ToString();
            var aggregate = SiteMapper.ToAggregate(site, assignId: false);
            await NewStore().AddAsync(aggregate);
            SiteMapper.CopyPersistedValues(aggregate, site);
            await CreateSiteAsync(site);
            await _cache.RemoveCacheAsync(await GetCacheKeyAsync());
            return site;
        }

        public async Task<Site> UpdateSiteAsync(Site site)
        {
            var aggregate = SiteMapper.ToAggregate(site, assignId: true);
            await NewStore().UpdateAsync(aggregate);
            SiteMapper.CopyPersistedValues(aggregate, site);
            await _cache.RemoveCacheAsync(await GetCacheKeyAsync());
            return site;
        }

        public async Task<Site> GetSiteAsync(int siteId)
        {
            return await GetSiteAsync(siteId, true);
        }

        public async Task<Site> GetSiteAsync(int siteId, bool tracking)
        {
            // note that tracking parameter is no longer relevant
            return (await GetSitesAsync()).FirstOrDefault(item => item.SiteId == siteId);
        }

        public async Task DeleteSiteAsync(int siteId)
        {
            foreach (var role in await _roleRepository.GetRolesAsync(siteId, false))
            {
                await _roleRepository.DeleteRoleAsync(role.RoleId);
            }

            // One store for the read and the delete: the delete removes the very instance it loaded.
            var store = NewStore();
            var aggregate = await store.GetByIdAsync(siteId);
            if (aggregate != null)
            {
                await store.DeleteAsync(aggregate);
            }
            await _cache.RemoveCacheAsync(await GetCacheKeyAsync());
        }


        // ISiteMigration.Up is a synchronous extensibility contract implemented outside the framework, so this
        // method has nothing to await even though a migration's Up() may well reach a store through the
        // IServiceProvider it resolves. The async signature is the repository's, not the migration's.
        public async Task<string> ProcessSiteMigrationsAsync(Alias alias, Site site)
        {
            var version = site.Version;
            var assemblies = AppDomain.CurrentDomain.GetStommeAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes(typeof(ISiteMigration)))
                {
                    if (Attribute.IsDefined(type, typeof(SiteMigrationAttribute)))
                    {
                        var attribute = (SiteMigrationAttribute)Attribute.GetCustomAttribute(type, typeof(SiteMigrationAttribute));
                        if (attribute.AliasName == "*" || attribute.AliasName == alias.Name)
                        {
                            if (string.IsNullOrEmpty(site.Version) || attribute.Version == "*" || Version.Parse(attribute.Version) > Version.Parse(site.Version))
                            {
                                try
                                {
                                    var obj = ActivatorUtilities.CreateInstance(_serviceProvider, type) as ISiteMigration;
                                    if (obj != null)
                                    {
                                        obj.Up(site, alias);
                                        _logger.Log(LogLevel.Information, "Site Migration", LogFunction.Other, "Site Migrated Successfully For {Alias} And Version {version}", alias.Name, attribute.Version);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Log(LogLevel.Error, "Site Migration", LogFunction.Other, ex, "An Error Occurred Executing Site Migration {Type} For {Alias} And Version {Version}", type, alias.Name, attribute.Version);
                                }
                                if (attribute.Version != "*" && (string.IsNullOrEmpty(version) || Version.Parse(attribute.Version) > Version.Parse(version)))
                                {
                                    version = attribute.Version;
                                }
                            }
                        }
                    }
                }
            }
            return version;
        }

        public async Task<string> ProcessPageTemplatesAsync(Alias alias, Site site, string version)
        {
            var pageTemplates = new List<PageTemplate>();

            foreach (var moduleDefinition in await _moduleDefinitionRepository.GetModuleDefinitionsAsync(site.SiteId))
            {
                // W2: a bundled module whose config knob module.<id>.enabled is explicitly false is
                // discovered (its assembly is present) but NOT seeded — its self-declared page templates are
                // skipped, so nothing it owns lands on a page and it does not render. Config silent or true seeds
                // as before, so a stock install is byte-for-byte unchanged.
                if (ModuleFeatureFlags.IsDisabledByConfig(_featureConfig, moduleDefinition.Name))
                {
                    continue;
                }

                if (moduleDefinition.PageTemplates != null)
                {
                    foreach (var pageTemplate in moduleDefinition.PageTemplates)
                    {
                        if (pageTemplate.PageTemplateModules.Count == 0)
                        {
                            pageTemplate.PageTemplateModules.Add(new PageTemplateModule());
                        }
                        foreach (var pageTemplateModule in pageTemplate.PageTemplateModules)
                        {
                            if (string.IsNullOrEmpty(pageTemplateModule.ModuleDefinitionName))
                            {
                                pageTemplateModule.ModuleDefinitionName = moduleDefinition.ModuleDefinitionName;
                            }
                            if (string.IsNullOrEmpty(pageTemplateModule.Title))
                            {
                                pageTemplateModule.Title = moduleDefinition.Name;
                            }
                        }
                        pageTemplates.Add(pageTemplate);
                        if (pageTemplate.Version != "*" && (string.IsNullOrEmpty(version) || Version.Parse(pageTemplate.Version) > Version.Parse(version)))
                        {
                            version = pageTemplate.Version;
                        }
                    }
                }
            }
            await CreatePagesAsync(site, pageTemplates, alias);
            return version;
        }

        /// <summary>
        /// Ensures the ADMIN site template's pages exist on an already-installed site — the upgrade counterpart
        /// to the install path, which is the only place they were ever created.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this, a framework release that adds an admin page (an "Identity Provider" page, say) gives it
        /// only to sites installed AFTER the release: the module registers by assembly scan, so it is selectable
        /// when adding a module to a page by hand, but its own admin page never appears and the feature is
        /// invisible to every existing install. Measured on the UAT deployment, where the module definition was
        /// present and <c>admin/openiddict</c> was absent.
        /// </para>
        /// <para>
        /// <b>Purely additive by construction.</b> Only templates whose <c>Path</c> has no page on this site are
        /// passed to <see cref="CreatePagesAsync"/>. An existing admin page is never re-processed, so an
        /// operator's customisation of its title/order/permissions — and of the modules placed on it, which
        /// <see cref="CreatePagesAsync"/> reconciles regardless of <c>PageTemplate.Update</c> — cannot be
        /// overwritten by a later boot. Adding a page is the whole job; changing one is not.
        /// </para>
        /// <para>
        /// Runs per site on the normal boot/upgrade path, so it is silent (and cheap: one page read) once every
        /// admin page exists. A gated page — e.g. one the admin template seeds only when a config flag is set —
        /// is honoured automatically, because the template itself decides what to return.
        /// </para>
        /// </remarks>
        public async Task ProcessAdminPageTemplatesAsync(Alias alias, Site site)
        {
            var siteTemplateType = Type.GetType(Constants.AdminSiteTemplate);
            if (siteTemplateType == null)
            {
                return;
            }

            var siteTemplateObject = ActivatorUtilities.CreateInstance(_serviceProvider, siteTemplateType);
            var adminPageTemplates = await ((ISiteTemplate)siteTemplateObject).CreateSiteAsync(site);

            var existingPaths = (await _pageRepository.GetPagesAsync(site.SiteId))
                .Where(item => item.Path != null)
                .Select(item => item.Path.ToLowerInvariant())
                .ToHashSet();

            // A template with no Path is positional (its path is derived from Parent/Name inside
            // CreatePagesAsync); the admin template always sets one, and anything that does not is left alone
            // rather than guessed at.
            var missing = adminPageTemplates
                .Where(item => !string.IsNullOrEmpty(item.Path) && !existingPaths.Contains(item.Path.ToLowerInvariant()))
                .ToList();

            if (missing.Count == 0)
            {
                return;
            }

            _logger.Log(LogLevel.Information, "Site Template", LogFunction.Create,
                "Adding {Count} missing admin page(s) to site {SiteId}: {Paths}",
                missing.Count, site.SiteId, string.Join(", ", missing.Select(item => item.Path)));

            // CreatePagesAsync gates on `AliasName == "*" || alias == null || AliasName == alias.Name`, and the
            // admin template leaves AliasName EMPTY (the PageTemplate default) because the install path passes a
            // null alias. Admin pages are not alias-specific, so mark them "*" — which lets the REAL alias be
            // passed. That matters beyond the filter: CreatePagesAsync logs a per-page failure only
            // `if (alias != null)`, so calling it with null makes every error SILENT. Measured twice — first the
            // filter skipped every page, then a swallowed failure looked like success.
            foreach (var pageTemplate in missing)
            {
                pageTemplate.AliasName = "*";
            }

            await CreatePagesAsync(site, missing, alias);
        }

        private async Task CreateSiteAsync(Site site)
        {
            // create default entities for site
            List<Role> roles = (await _roleRepository.GetRolesAsync(site.SiteId, true)).ToList();
            if (!roles.Where(item => item.Name == RoleNames.Everyone).Any())
            {
                await _roleRepository.AddRoleAsync(new Role {SiteId = null, Name = RoleNames.Everyone, Description = RoleNames.Everyone, IsAutoAssigned = false, IsSystem = true});
            }
            if (!roles.Where(item => item.Name == RoleNames.Unauthenticated).Any())
            {
                await _roleRepository.AddRoleAsync(new Role { SiteId = null, Name = RoleNames.Unauthenticated, Description = RoleNames.Unauthenticated, IsAutoAssigned = false, IsSystem = true });
            }
            if (!roles.Where(item => item.Name == RoleNames.Host).Any())
            {
                await _roleRepository.AddRoleAsync(new Role {SiteId = null, Name = RoleNames.Host, Description = RoleNames.Host, IsAutoAssigned = false, IsSystem = true});
            }
            await _roleRepository.AddRoleAsync(new Role {SiteId = site.SiteId, Name = RoleNames.Registered, Description = RoleNames.Registered, IsAutoAssigned = true, IsSystem = true});
            await _roleRepository.AddRoleAsync(new Role {SiteId = site.SiteId, Name = RoleNames.Admin, Description = RoleNames.Admin, IsAutoAssigned = false, IsSystem = true});

            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "FirstName", Title = "First Name", Description = "Your First Or Given Name", Category = "Name", ViewOrder = 10, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "LastName", Title = "Last Name", Description = "Your Last Or Family Name", Category = "Name", ViewOrder = 20, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "Street", Title = "Street", Description = "Street Or Building Address", Category = "Address", ViewOrder = 30, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "City", Title = "City", Description = "City", Category = "Address", ViewOrder = 40, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "Region", Title = "Region", Description = "State Or Province", Category = "Address", ViewOrder = 50, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "Country", Title = "Country", Description = "Country", Category = "Address", ViewOrder = 60, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "PostalCode", Title = "Postal Code", Description = "Postal Code Or Zip Code", Category = "Address", ViewOrder = 70, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });
            await _profileRepository.AddProfileAsync(new Profile
            { SiteId = site.SiteId, Name = "Phone", Title = "Phone Number", Description = "Phone Number", Category = "Contact", ViewOrder = 80, MaxLength = 50, DefaultValue = "", IsRequired = false, IsPrivate = false, Options = "", Rows = 1 });

            Folder folder = await _folderRepository.AddFolderAsync(new Folder
            {
                SiteId = site.SiteId, ParentId = null, Name = "Root", Type = FolderTypes.Private, Path = "", Order = 1, ImageSizes = "", Capacity = 0, IsSystem = true,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                }
            });
            await _folderRepository.AddFolderAsync(new Folder { SiteId = site.SiteId, ParentId = folder.FolderId, Name = "Public", Type = FolderTypes.Public, Path = "Public/", Order = 1, ImageSizes = "", Capacity = 0, IsSystem = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                }
            });
            await _folderRepository.AddFolderAsync(new Folder
            {
                SiteId = site.SiteId, ParentId = folder.FolderId, Name = "Users", Type = FolderTypes.Private, Path = "Users/", Order = 3, ImageSizes = "", Capacity = 0, IsSystem = true,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                }
            });

            // admin site template
            var siteTemplateType = Type.GetType(Constants.AdminSiteTemplate);
            var siteTemplateObject = ActivatorUtilities.CreateInstance(_serviceProvider, siteTemplateType);
            List<PageTemplate> adminPageTemplates = await ((ISiteTemplate)siteTemplateObject).CreateSiteAsync(site);
            await CreatePagesAsync(site, adminPageTemplates, null);

            // process site template
            if (string.IsNullOrEmpty(site.SiteTemplateType))
            {
                var section = _config.GetSection("Installation:SiteTemplate");
                if (section.Exists())
                {
                    if(string.IsNullOrEmpty(section.Value)){
                        site.SiteTemplateType = Constants.DefaultSiteTemplate;
                    }
                    else
                    {
                        site.SiteTemplateType = section.Value;
                    }
                }
                else
                {
                    site.SiteTemplateType = Constants.DefaultSiteTemplate;
                }
            }

            siteTemplateType = Type.GetType(site.SiteTemplateType);
            if (siteTemplateType != null)
            {
                siteTemplateObject = ActivatorUtilities.CreateInstance(_serviceProvider, siteTemplateType);
                List<PageTemplate> pageTemplates = await ((ISiteTemplate) siteTemplateObject).CreateSiteAsync(site);
                if (pageTemplates != null && pageTemplates.Count > 0)
                {
                    await CreatePagesAsync(site, pageTemplates, null);
                }
            }
        }

        public async Task CreatePagesAsync(Site site, List<PageTemplate> pageTemplates, Alias alias)
        {
            List<Page> pages = null;
            List<PageModule> pageModules = null;
            List<ModuleDefinition> moduleDefinitions = null;

            foreach (PageTemplate pageTemplate in pageTemplates)
            {
                if (pageTemplate.AliasName == "*" || alias == null || pageTemplate.AliasName == alias.Name)
                {
                    if (string.IsNullOrEmpty(site.Version) || pageTemplate.Version == "*" || Version.Parse(pageTemplate.Version) > Version.Parse(site.Version))
                    {
                        if (pages == null)
                        {
                            pages = (await _pageRepository.GetPagesAsync(site.SiteId)).ToList();
                        }
                        Page parent = null;
                        if (string.IsNullOrEmpty(pageTemplate.Path))
                        {
                            if (!string.IsNullOrEmpty(pageTemplate.Parent))
                            {
                                parent = pages.FirstOrDefault(item => item.Path.ToLower() == pageTemplate.Parent.ToLower());
                            }
                            pageTemplate.Path = (parent != null) ? parent.Path + "/" + pageTemplate.Name : pageTemplate.Name;
                        }
                        // home page path can be specified with "home" or "/"
                        pageTemplate.Path = (pageTemplate.Path.ToLower() == "home") ? "" : pageTemplate.Path;
                        pageTemplate.Path = (pageTemplate.Path == "/") ? "" : pageTemplate.Path;
                        var page = pages.FirstOrDefault(item => item.Path.ToLower() == pageTemplate.Path.ToLower());
                        if (page == null)
                        {
                            page = new Page();
                            page.SiteId = site.SiteId;
                            page.Path = pageTemplate.Path;
                        }
                        page.Name = pageTemplate.Name;
                        if (string.IsNullOrEmpty(page.Name))
                        {
                            page.Name = (pageTemplate.Path.Contains("/")) ? pageTemplate.Path.Substring(pageTemplate.Name.LastIndexOf("/") + 1) : pageTemplate.Path;
                        }
                        if (string.IsNullOrEmpty(pageTemplate.Parent))
                        {
                            if (pageTemplate.Path.Contains("/"))
                            {
                                parent = pages.FirstOrDefault(item => item.Path.ToLower() == pageTemplate.Path.Substring(0, pageTemplate.Path.LastIndexOf("/")).ToLower());
                            }
                        }
                        else
                        {
                            parent = pages.FirstOrDefault(item => item.Path.ToLower() == ((pageTemplate.Parent == "/") ? "" : pageTemplate.Parent.ToLower()));
                        }
                        page.ParentId = (parent != null) ? parent.PageId : null;
                        page.Path = page.Path.ToLower();
                        page.Title = pageTemplate.Title;
                        page.Order = pageTemplate.Order;
                        page.Url = pageTemplate.Url;
                        page.ThemeType = pageTemplate.ThemeType;
                        page.DefaultContainerType = pageTemplate.DefaultContainerType;
                        page.HeadContent = pageTemplate.HeadContent;
                        page.BodyContent = pageTemplate.BodyContent;
                        page.Icon = pageTemplate.Icon;
                        page.IsNavigation = pageTemplate.IsNavigation;
                        page.IsClickable = pageTemplate.IsClickable;
                        page.IsPersonalizable = pageTemplate.IsPersonalizable;
                        page.UserId = null;
                        page.IsDeleted = pageTemplate.IsDeleted;
                        page.PermissionList = pageTemplate.PermissionList;
                        try
                        {
                            if (page.PageId != 0)
                            {
                                if (pageTemplate.Update)
                                {
                                    page = await _pageRepository.UpdatePageAsync(page);
                                    if (alias != null)
                                    {
                                        _logger.Log(LogLevel.Information, "Site Template", LogFunction.Update, "Page Updated {Page}", page);
                                    }
                                    await UpdateSettingsAsync(EntityNames.Page, page.PageId, pageTemplate.Settings);
                                }
                            }
                            else
                            {
                                page = await _pageRepository.AddPageAsync(page);
                                pages.Add(page);
                                if (alias != null)
                                {
                                    _logger.Log(LogLevel.Information, "Site Template", LogFunction.Create, "Page Added {Page}", page);
                                }
                                await UpdateSettingsAsync(EntityNames.Page, page.PageId, pageTemplate.Settings);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (alias != null)
                            {
                                _logger.Log(LogLevel.Error, "Site Template", LogFunction.Other, ex, "Error Processing Page {Page}", page);
                            }
                        }

                        if (pageModules == null)
                        {
                            pageModules = (await _pageModuleRepository.GetPageModulesAsync(site.SiteId)).ToList();
                        }
                        if (moduleDefinitions == null)
                        {
                            moduleDefinitions = (await _moduleDefinitionRepository.GetModuleDefinitionsAsync(site.SiteId)).ToList();
                        }
                        foreach (PageTemplateModule pageTemplateModule in pageTemplate.PageTemplateModules)
                        {
                            var moduleDefinition = moduleDefinitions.Where(item => item.ModuleDefinitionName == pageTemplateModule.ModuleDefinitionName).FirstOrDefault();
                            // W2: the single sink where every module — site-template-placed OR
                            // self-placed — is attached to a page, so it is where the module.<id>.enabled knob is
                            // enforced. A config-disabled module is placed on NO page (the page itself, if
                            // otherwise defined, still renders — only the disabled module is withheld).
                            if (moduleDefinition != null && !ModuleFeatureFlags.IsDisabledByConfig(_featureConfig, moduleDefinition.Name))
                            {
                                var pageModule = pageModules.FirstOrDefault(item => item.PageId == page.PageId && item.Module.ModuleDefinitionName == pageTemplateModule.ModuleDefinitionName && item.Title.ToLower() == pageTemplateModule.Title.ToLower());
                                if (pageModule == null)
                                {
                                    // Title is part of the match key because one module can legitimately
                                    // be placed several times on a page under distinct titles — but Title is
                                    // also DEFAULTED from ModuleDefinition.Name, so a module rename between
                                    // releases changes the key and the additive path would place the module a
                                    // second time. A title mismatch is treated as the same placement only when
                                    // it is unambiguous: exactly one existing placement of the module on the
                                    // page AND exactly one declared in the template. Anything else (the
                                    // multi-placement shape) stays additive.
                                    var candidates = pageModules.Where(item => item.PageId == page.PageId && item.Module.ModuleDefinitionName == pageTemplateModule.ModuleDefinitionName).ToList();
                                    if (candidates.Count == 1 && pageTemplate.PageTemplateModules.Count(item => item.ModuleDefinitionName == pageTemplateModule.ModuleDefinitionName) == 1)
                                    {
                                        pageModule = candidates[0];
                                    }
                                }
                                if (pageModule == null)
                                {
                                    pageModule = new PageModule();
                                    pageModule.PageId = page.PageId;
                                    pageModule.Module = new Module();
                                    pageModule.Module.SiteId = site.SiteId;
                                    pageModule.Module.ModuleDefinitionName = pageTemplateModule.ModuleDefinitionName;
                                }
                                pageModule.Title = pageTemplateModule.Title;
                                pageModule.Pane = (string.IsNullOrEmpty(pageTemplateModule.Pane)) ? PaneNames.Default : pageTemplateModule.Pane;
                                pageModule.Order = (pageTemplateModule.Order == 0) ? 1 : pageTemplateModule.Order;
                                pageModule.ContainerType = pageTemplateModule.ContainerType;
                                pageModule.Header = pageTemplateModule.Header;
                                pageModule.Footer = pageTemplateModule.Footer;
                                pageModule.IsDeleted = pageTemplateModule.IsDeleted;
                                pageModule.Module.PermissionList = new List<Permission>();
                                foreach (var permission in pageTemplateModule.PermissionList)
                                {
                                    pageModule.Module.PermissionList.Add(permission.Clone());
                                }
                                pageModule.Module.AllPages = false;
                                pageModule.Module.IsDeleted = false;
                                try
                                {
                                    if (pageModule.ModuleId != 0)
                                    {
                                        if (pageTemplate.Update)
                                        {
                                            await _moduleRepository.UpdateModuleAsync(pageModule.Module);
                                            await _pageModuleRepository.UpdatePageModuleAsync(pageModule);
                                            if (alias != null)
                                            {
                                                _logger.Log(LogLevel.Information, "Site Template", LogFunction.Update, "Page Module Updated {PageModule}", pageModule);
                                            }
                                            await UpdateSettingsAsync(EntityNames.Module, pageModule.Module.ModuleId, pageTemplateModule.Settings);
                                        }
                                        else
                                        {
                                            pageTemplateModule.Content = ""; // do not update content
                                        }
                                    }
                                    else
                                    {
                                        Module module = null;
                                        if (pageTemplateModule.FromPagePath != "")
                                        {
                                            // used for modules shared across pages
                                            var pagePath = pageTemplateModule.FromPagePath;
                                            pagePath = (pagePath.ToLower() == "home") ? "" : pagePath;
                                            pagePath = (pagePath == "/") ? "" : pagePath;
                                            if (pages.Any(item => item.Path.ToLower() == pagePath.ToLower()))
                                            {
                                                var pageId = pages.First(item => item.Path.ToLower() == pagePath.ToLower()).PageId;
                                                if (pageModules.Any(item => item.PageId == pageId && item.Module.ModuleDefinitionName == pageTemplateModule.ModuleDefinitionName && item.Title.ToLower() == pageTemplateModule.Title.ToLower()))
                                                {
                                                    module = pageModules.FirstOrDefault(item => item.PageId == pageId && item.Module.ModuleDefinitionName == pageTemplateModule.ModuleDefinitionName && item.Title.ToLower() == pageTemplateModule.Title.ToLower()).Module;
                                                }
                                            }
                                        }
                                        if (module == null)
                                        {
                                            module = await _moduleRepository.AddModuleAsync(pageModule.Module);
                                        }

                                        pageModule.ModuleId = module.ModuleId;
                                        pageModule.Module = null; // remove tracking
                                        await _pageModuleRepository.AddPageModuleAsync(pageModule);
                                        pageModule.Module = module;
                                        pageModules.Add(pageModule);
                                        if (alias != null)
                                        {
                                            _logger.Log(LogLevel.Information, "Site Template", LogFunction.Create, "Page Module Added {PageModule}", pageModule);
                                        }
                                        await UpdateSettingsAsync(EntityNames.Module, pageModule.Module.ModuleId, pageTemplateModule.Settings);
                                    }

                                }
                                catch (Exception ex)
                                {
                                    if (alias != null)
                                    {
                                        _logger.Log(LogLevel.Error, "Site Template", LogFunction.Other, ex, "Error Processing Page Module {PageModule}", pageModule);
                                    }
                                }
                                if (!string.IsNullOrEmpty(pageTemplateModule.Content))
                                {
                                    var module = await _moduleRepository.GetModuleAsync(pageModule.ModuleId);
                                    if (!await _moduleRepository.ImportModuleAsync(module, pageTemplateModule.Content, "Site Template"))
                                    {
                                        if (alias != null)
                                        {
                                            _logger.Log(LogLevel.Error, "Site Template", LogFunction.Other, "Error Importing Content For {ModuleDefinitionName}", pageTemplateModule.ModuleDefinitionName);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (alias != null)
                                {
                                    _logger.Log(LogLevel.Error, "Site Template", LogFunction.Other, "Module Definition Does Not Exist {ModuleDefinitionName}", pageTemplateModule.ModuleDefinitionName);
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task UpdateSettingsAsync(string entityName, int entityId, List<Setting> templateSettings)
        {
            foreach (var templateSetting in templateSettings)
            {
                var setting = await _settingRepository.GetSettingAsync(entityName, entityId, templateSetting.SettingName);
                if (setting == null)
                {
                    templateSetting.EntityName = entityName;
                    templateSetting.EntityId = entityId;
                    await _settingRepository.AddSettingAsync(templateSetting);
                }
                else
                {
                    setting.SettingValue = templateSetting.SettingValue;
                    setting.IsPrivate = templateSetting.IsPrivate;
                    await _settingRepository.UpdateSettingAsync(setting);
                }
            }
        }

        private async Task<string> GetCacheKeyAsync()
        {
            var tenant = await _tenantManager.GetTenantAsync();
            return $"Tenant:{tenant?.TenantId}:Sites";
        }
    }
}
