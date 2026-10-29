using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Pages
{
    [AllowAnonymous]
    [OutputCache(Duration = 300, Tags = [Constants.SitemapOutputCacheTag])]
    public class SitemapModel : PageModel
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IPageRepository _pages;
        private readonly IPageModuleRepository _pageModules;
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly ISettingRepository _settings;
        private readonly IUserPermissions _userPermissions;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;
        private Alias _alias;

        public SitemapModel(IServiceProvider serviceProvider, IPageRepository pages, IPageModuleRepository pageModules, IModuleDefinitionRepository moduleDefinitions, ISettingRepository settings, IUserPermissions userPermissions, IUrlMappingRepository urlMappings, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _serviceProvider = serviceProvider;
            _pages = pages;
            _pageModules = pageModules;
            _moduleDefinitions = moduleDefinitions;
            _settings = settings;
            _userPermissions = userPermissions;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            _alias = await _tenantManager.GetAliasAsync();

            var sitemap = new List<Sitemap>();

            // build site map
            var rooturl = _alias.Protocol + (string.IsNullOrEmpty(_alias.Path) ? _alias.Name : _alias.Name.Substring(0, _alias.Name.IndexOf("/")));
            var moduleDefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(_alias.SiteId)).ToList();
            var pageModules = await _pageModules.GetPageModulesAsync(_alias.SiteId);
            foreach (var page in await _pages.GetPagesAsync(_alias.SiteId))
            {
                if (await _userPermissions.IsAuthorizedAsync(null, PermissionNames.View, page.PermissionList) && !Constants.InternalPagePaths.Contains(page.Path))
                {
                    var pageurl = rooturl;
                    if (string.IsNullOrEmpty(page.Url))
                    {
                        pageurl += Utilities.NavigateUrl(_alias.Path, page.Path, "");
                    }
                    else
                    {
                        pageurl += (page.Url.StartsWith("/") ? "" : "/") + page.Url;
                    }
                    sitemap.Add(new Sitemap { Url = pageurl, ModifiedOn = DateTime.UtcNow });

                    foreach (var pageModule in pageModules.Where(item => item.PageId == page.PageId))
                    {
                        if (await _userPermissions.IsAuthorizedAsync(null, PermissionNames.View, pageModule.Module.PermissionList))
                        {
                            var moduleDefinition = moduleDefinitions.Where(item => item.ModuleDefinitionName == pageModule.Module.ModuleDefinitionName).FirstOrDefault();
                            if (moduleDefinition != null && moduleDefinition.ServerManagerType != "")
                            {
                                Type moduletype = Type.GetType(moduleDefinition.ServerManagerType);
                                if (moduletype != null && moduletype.GetInterface(nameof(ISitemap)) != null)
                                {
                                    try
                                    {
                                        pageModule.Module.Settings = (await _settings.GetSettingsAsync(EntityNames.Module, pageModule.ModuleId)).ToDictionary(x => x.SettingName, x => x.SettingValue);
                                        var moduleobject = ActivatorUtilities.CreateInstance(_serviceProvider, moduletype);
                                        var urls = await ((ISitemap)moduleobject).GetUrlsAsync(_alias.Path, page.Path, pageModule.Module);
                                        foreach (var url in urls)
                                        {
                                            // The module's own last-change date when it gives one.
                                            sitemap.Add(new Sitemap { Url = rooturl + url.Url, ModifiedOn = url.ModifiedOn == default ? DateTime.UtcNow : url.ModifiedOn });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Log(LogLevel.Error, this, LogFunction.Other, ex, "Error Retrieving SiteMap For {Name} Module", moduleDefinition.Name);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // write XML
            var builder = new StringBuilder();
            var stringWriter = new StringWriterWithEncoding(builder, Encoding.UTF8);

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = Environment.NewLine,
                CloseOutput = true,
                WriteEndDocumentOnClose = true
            };

            using (var writer = XmlWriter.Create(stringWriter, settings))
            {
                writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
                foreach (var url in sitemap)
                {
                    writer.WriteStartElement("url");
                    writer.WriteElementString("loc", url.Url);
                    writer.WriteElementString("lastmod", url.ModifiedOn.ToString("yyyy-MM-dd"));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.Close();
            }

            return Content(builder.ToString(), "application/xml");
        }
    }

    public class StringWriterWithEncoding : StringWriter
    {
        private readonly Encoding _encoding;

        public StringWriterWithEncoding(StringBuilder builder, Encoding encoding) : base(builder)
        {
            this._encoding = encoding;
        }

        public override Encoding Encoding
        {
            get
            {
                return this._encoding;
            }
        }
    }
}
