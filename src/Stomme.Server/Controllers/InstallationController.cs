using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;
using Stomme.FeatureManagement.Application;
using System.Net;
using Stomme.Repository;
using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class InstallationController : Controller
    {
        private readonly IConfigManager _configManager;
        private readonly IInstallationManager _installationManager;
        private readonly IDatabaseManager _databaseManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly ICacheManager _cache;
        private readonly IHttpContextAccessor _accessor;
        private readonly IAliasRepository _aliases;
        private readonly ISiteRepository _sites;
        private readonly ILogger<InstallationController> _filelogger;
        private readonly ITenantManager _tenantManager;
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly IThemeRepository _themes;
        private readonly IFeatureConfigurationSource _featureConfig;

        public InstallationController(IConfigManager configManager, IInstallationManager installationManager, IDatabaseManager databaseManager, ILocalizationManager localizationManager, ICacheManager cache, IHttpContextAccessor accessor, IAliasRepository aliases, ISiteRepository sites, ILogger<InstallationController> filelogger, ITenantManager tenantManager, IModuleDefinitionRepository moduleDefinitions, IThemeRepository themes, IFeatureConfigurationSource featureConfig)
        {
            _configManager = configManager;
            _installationManager = installationManager;
            _databaseManager = databaseManager;
            _localizationManager = localizationManager;
            _cache = cache;
            _accessor = accessor;
            _aliases = aliases;
            _sites = sites;
            _filelogger = filelogger;
            _tenantManager = tenantManager;
            _moduleDefinitions = moduleDefinitions;
            _themes = themes;
            _featureConfig = featureConfig;
        }

        // GET api/<controller>/modules
        // Install-time discovery: the bundled, enabled modules that declare a module-wide config control,
        // so the installer's onboarding step can render them while the app is still uninstalled. Reflects loaded
        // assemblies only — touches no DB — so it is safe before the master store exists. Open (module metadata is
        // not sensitive, mirroring the open DatabaseController.Get the installer already consumes).
        [HttpGet("modules")]
        public List<ConfigurableModule> ConfigurableModules()
        {
            var modules = new List<ConfigurableModule>();
            foreach (var moduleDefinition in ModuleDefinitionRepository.GetModuleDefinitionsFromAssemblies())
            {
                if (!string.IsNullOrEmpty(moduleDefinition.ConfigSettingsType)
                    && !ModuleFeatureFlags.IsDisabledByConfig(_featureConfig, moduleDefinition.Name))
                {
                    modules.Add(new ConfigurableModule
                    {
                        Name = moduleDefinition.Name,
                        ModuleDefinitionName = moduleDefinition.ModuleDefinitionName,
                        ConfigSettingsType = moduleDefinition.ConfigSettingsType
                    });
                }
            }
            return modules;
        }

        // POST api/<controller>
        [HttpPost]
        public async Task<Installation> Post([FromBody] InstallConfig config)
        {
            var installation = new Installation { Success = false, Message = "" };

            // Anyone may run the browser wizard on a host that is neither installed nor configured for an unattended install;
            // after that only the Host (who adds sites through this call).
            var wizardOpen = string.IsNullOrEmpty(_configManager.GetSetting($"{SettingKeys.ConnectionStringsSection}:{SettingKeys.ConnectionStringKey}", ""))
                && !UnattendedInstall.IsConfigured(_configManager);
            if (ModelState.IsValid && (User.IsInRole(RoleNames.Host) || wizardOpen))
            {
                installation = await _databaseManager.InstallAsync(config);
            }
            else
            {
                installation.Message = "Installation Not Authorized";
            }

            return installation;
        }

        // GET api/<controller>/installed/?path=xxx
        [HttpGet("installed")]
        public async Task<Installation> IsInstalled(string path)
        {
            var installation = await _databaseManager.IsInstalledAsync();
            if (installation.Success)
            {
                path = _accessor.HttpContext.Request.Host.Value + "/" + WebUtility.UrlDecode(path);
                installation.Alias = await _aliases.GetAliasAsync(path);
            }
            return installation;
        }

        // A POST (antiforgery-checked like every other state-changing call; it was a GET, which a cross-site link could
        // trigger for a signed-in Host) that AWAITS the upgrade's hand-off, so a refusal - the pre-upgrade snapshot
        // included - reaches the page instead of only the log.
        [HttpPost("upgrade")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Installation> Upgrade([FromBody] bool backup)
        {
            var refusal = await _installationManager.UpgradeFramework(backup);
            return new Installation { Success = refusal == null, Message = refusal ?? "" };
        }

        // GET api/<controller>/restart
        [HttpPost("restart")]
        [Authorize(Roles = RoleNames.Host)]
        public void Restart()
        {
            _installationManager.RestartApplication();
        }

        // GET api/<controller>/list
        [HttpGet("list")]
        public async Task<List<string>> List()
        {
            return (await GetAssemblyList()).Select(item => item.HashedName).ToList();
        }

        private async Task<List<ClientAssembly>> GetAssemblyList()
        {
            var alias = await _tenantManager.GetAliasAsync();
            return (await _cache.GetCacheAsync(alias, "AssembliesList", async entry =>
            {
                var assemblyList = new List<ClientAssembly>();

                var site = await _sites.GetSiteAsync(alias.SiteId);
                if (site != null && (site.Runtime == Runtimes.WebAssembly || site.Hybrid))
                {
                    var binFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

                    // testmode setting is used for validating that the API is downloading the appropriate assemblies to the client
                    bool hashfilename = true;
                    if (_configManager.GetSetting($"{SettingKeys.TestModeKey}", "false") == "true")
                    {
                        hashfilename = false;
                    }

                    // get module and theme assemblies for site. The theme list is APPENDED, not Concat'd:
                    // the harvested code discarded the Concat result, so external theme assemblies never
                    // streamed to a WebAssembly client and every external theme was dead on a WASM site —
                    // pinned RED-first by WasmStreamingDriveTests.Wasm_list_streams_the_external_theme_assembly
                    // (examples/Modules/Blogs/Blogs.Drive).
                    var assemblies = await _moduleDefinitions.GetAssembliesAsync(alias.SiteId);
                    foreach (var themeAssembly in await _themes.GetAssembliesAsync(alias.SiteId))
                    {
                        if (!assemblies.Contains(themeAssembly))
                        {
                            assemblies.Add(themeAssembly);
                        }
                    }

                    // populate assembly list
                    foreach (var assembly in assemblies)
                    {
                        if (assembly != Constants.ClientId)
                        {
                            var filepath = Path.Combine(binFolder, assembly) + ".dll";
                            if (System.IO.File.Exists(filepath))
                            {
                                assemblyList.Add(new ClientAssembly(Path.Combine(binFolder, assembly + ".dll"), hashfilename));
                            }
                        }
                    }

                    // insert satellite assemblies at beginning of list
                    foreach (var culture in _localizationManager.GetInstalledCultures())
                    {
                        if (culture != Constants.DefaultCulture)
                        {
                            var assembliesFolderPath = Path.Combine(binFolder, culture);
                            if (Directory.Exists(assembliesFolderPath))
                            {
                                foreach (var assembly in assemblies)
                                {
                                    var filepath = Path.Combine(assembliesFolderPath, assembly) + ".resources.dll";
                                    if (System.IO.File.Exists(filepath))
                                    {
                                        assemblyList.Insert(0, new ClientAssembly(Path.Combine(assembliesFolderPath, assembly + ".resources.dll"), hashfilename));
                                    }
                                }
                            }
                            else
                            {
                                _filelogger.LogError(Utilities.LogMessage(this, $"The Satellite Assembly Folder For {culture} Does Not Exist"));
                            }
                        }
                    }
                }
                return assemblyList;
            })).ToList();

        }

        // GET api/<controller>/load?list=x,y
        [HttpGet("load")]
        public async Task<IActionResult> Load(string list = "*")
        {
            return File(await GetAssemblies(list), System.Net.Mime.MediaTypeNames.Application.Octet, "stomme.dll");
        }

        private async Task<byte[]> GetAssemblies(string list)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (list == "*")
            {
                return await _cache.GetCacheAsync(alias, "Assemblies", async entry =>
                {
                    return await GetZIP(list, alias);
                });
            }
            else
            {
                return await GetZIP(list, alias);
            }
        }

        private async Task<byte[]> GetZIP(string list, Alias alias)
        {
            var site = await _sites.GetSiteAsync(alias.SiteId);
            if (site != null && (site.Runtime == Runtimes.WebAssembly || site.Hybrid))
            {
                var binFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

                // get list of assemblies which should be downloaded to client
                List<ClientAssembly> assemblies = await GetAssemblyList();
                if (list != "*")
                {
                    var filter = list.Split(',').ToList();
                    assemblies.RemoveAll(item => !filter.Contains(item.HashedName));
                }

                // create zip file containing assemblies and debug symbols
                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var assembly in assemblies)
                        {
                            if (Path.GetFileNameWithoutExtension(assembly.FilePath) != Constants.ClientId)
                            {
                                if (System.IO.File.Exists(assembly.FilePath))
                                {
                                    using (var filestream = new FileStream(assembly.FilePath, FileMode.Open, FileAccess.Read))
                                    using (var entrystream = archive.CreateEntry(assembly.HashedName).Open())
                                    {
                                        filestream.CopyTo(entrystream);
                                    }
                                }
                                var pdb = assembly.FilePath.Replace(".dll", ".pdb");
                                if (System.IO.File.Exists(pdb))
                                {
                                    using (var filestream = new FileStream(pdb, FileMode.Open, FileAccess.Read))
                                    using (var entrystream = archive.CreateEntry(assembly.HashedName.Replace(".dll", ".pdb")).Open())
                                    {
                                        filestream.CopyTo(entrystream);
                                    }
                                }
                            }
                        }
                    }

                    return memoryStream.ToArray();
                }
            }
            else
            {
                // return empty zip
                using (var memoryStream = new MemoryStream())
                {
                    using (var zip = new ZipArchive(memoryStream, ZipArchiveMode.Create)) {}
                    return memoryStream.ToArray();
                }
            }
        }

        public struct ClientAssembly
        {
            public ClientAssembly(string filepath, bool hashfilename)
            {
                FilePath = filepath;
                DateTime lastwritetime = System.IO.File.GetLastWriteTime(filepath);
                if (hashfilename)
                {
                    HashedName = Utilities.GenerateSimpleHash(filepath) + "." + lastwritetime.ToString("yyyyMMddHHmmss") + Path.GetExtension(filepath);
                }
                else
                {
                    HashedName = Path.GetFileNameWithoutExtension(filepath) + "." + lastwritetime.ToString("yyyyMMddHHmmss") + Path.GetExtension(filepath);
                }
            }

            public string FilePath { get; private set; }
            public string HashedName { get; private set; }
        }
    }
}
