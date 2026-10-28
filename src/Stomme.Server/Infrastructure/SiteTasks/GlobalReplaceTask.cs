using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    public class GlobalReplaceTask : SiteTaskBase
    {
        public override async Task<string> ExecuteTaskAsync(IServiceProvider provider, Site site, string parameters)
        {
            string log = "";

            // get services
            var siteRepository = provider.GetRequiredService<ISiteRepository>();
            var pageRepository = provider.GetRequiredService<IPageRepository>();
            var pageModuleRepository = provider.GetRequiredService<IPageModuleRepository>();
            var moduleRepository = provider.GetRequiredService<IModuleRepository>();
            var settingRepository = provider.GetRequiredService<ISettingRepository>();
            var tenantManager = provider.GetRequiredService<ITenantManager>();
            var syncManager = provider.GetRequiredService<ISyncManager>();

            if (!string.IsNullOrEmpty(parameters))
            {
                // get parameters
                var globalReplace = JsonSerializer.Deserialize<GlobalReplace>(parameters);

                var find = globalReplace.Find;
                var replace = globalReplace.Replace;
                var comparisonType = (globalReplace.CaseSensitive) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var refresh = false;

                log += $"Replacing: '{find}' With: '{replace}' Case Sensitive: {globalReplace.CaseSensitive}<br />";

                // site properties
                site = await siteRepository.GetSiteAsync(site.SiteId);
                var changed = false;
                if (site.Name != null && site.Name.Contains(find, comparisonType))
                {
                    site.Name = site.Name.Replace(find, replace, comparisonType);
                    changed = true;
                }
                if (site.HeadContent != null && site.HeadContent.Contains(find, comparisonType))
                {
                    site.HeadContent = site.HeadContent.Replace(find, replace, comparisonType);
                    changed = true;
                }
                if (site.BodyContent != null && site.BodyContent.Contains(find, comparisonType))
                {
                    site.BodyContent = site.BodyContent.Replace(find, replace, comparisonType);
                    changed = true;
                }
                if (changed && globalReplace.Site)
                {
                    await siteRepository.UpdateSiteAsync(site);
                    log += $"Site Updated<br />";
                    refresh = true;
                }
                if (globalReplace.Site)
                {
                    if (await UpdateSettingsAsync(settingRepository, EntityNames.Site, site.SiteId, find, replace, comparisonType))
                    {
                        log += $"Site Settings Updated<br />";
                        refresh = true;
                    }
                }

                var pages = (await pageRepository.GetPagesAsync(site.SiteId)).ToList();
                var pageModules = (await pageModuleRepository.GetPageModulesAsync(site.SiteId)).ToList();

                // iterate pages
                foreach (var page in pages)
                {
                    // page properties
                    changed = false;
                    if (page.Name != null && page.Name.Contains(find, comparisonType))
                    {
                        page.Name = page.Name.Replace(find, replace, comparisonType);
                        changed = true;
                    }
                    if (page.Title != null && page.Title.Contains(find, comparisonType))
                    {
                        page.Title = page.Title.Replace(find, replace, comparisonType);
                        changed = true;
                    }
                    if (page.HeadContent != null && page.HeadContent.Contains(find, comparisonType))
                    {
                        page.HeadContent = page.HeadContent.Replace(find, replace, comparisonType);
                        changed = true;
                    }
                    if (page.BodyContent != null && page.BodyContent.Contains(find, comparisonType))
                    {
                        page.BodyContent = page.BodyContent.Replace(find, replace, comparisonType);
                        changed = true;
                    }
                    if (changed && globalReplace.Pages)
                    {
                        await pageRepository.UpdatePageAsync(page);
                        log += $"Page Updated: /{page.Path}<br />";
                        refresh = true;
                    }
                    if (globalReplace.Pages)
                    {
                        if (await UpdateSettingsAsync(settingRepository, EntityNames.Page, page.PageId, find, replace, comparisonType))
                        {
                            log += $"Page Settings Updated<br />";
                            refresh = true;
                        }
                    }

                    foreach (var pageModule in pageModules.Where(item => item.PageId == page.PageId))
                    {
                        // module properties
                        changed = false;
                        if (pageModule.Title != null && pageModule.Title.Contains(find, comparisonType))
                        {
                            pageModule.Title = pageModule.Title.Replace(find, replace, comparisonType);
                            changed = true;
                        }
                        if (pageModule.Header != null && pageModule.Header.Contains(find, comparisonType))
                        {
                            pageModule.Header = pageModule.Header.Replace(find, replace, comparisonType);
                            changed = true;
                        }
                        if (pageModule.Footer != null && pageModule.Footer.Contains(find, comparisonType))
                        {
                            pageModule.Footer = pageModule.Footer.Replace(find, replace, comparisonType);
                            changed = true;
                        }
                        if (changed && globalReplace.Modules)
                        {
                            await pageModuleRepository.UpdatePageModuleAsync(pageModule);
                            log += $"Module Updated: {pageModule.Title} Page: /{page.Path}<br />";
                            refresh = true;
                        }
                        if (globalReplace.Modules)
                        {
                            if (await UpdateSettingsAsync(settingRepository, EntityNames.Module, pageModule.ModuleId, find, replace, comparisonType))
                            {
                                log += $"Module Settings Updated<br />";
                                refresh = true;
                            }
                        }

                        // module content
                        if (globalReplace.Content)
                        {
                            var content = await moduleRepository.ExportModuleAsync(pageModule.Module, "Global Replace");
                            if (!string.IsNullOrEmpty(content) && content.Contains(WebUtility.HtmlEncode(find), comparisonType))
                            {
                                content = content.Replace(WebUtility.HtmlEncode(find), WebUtility.HtmlEncode(replace), comparisonType);
                                await moduleRepository.ImportModuleAsync(pageModule.Module, content, "Global Replace");
                                log += $"Module Content Updated: {pageModule.Title} Page: /{page.Path}<br />";
                            }
                        }
                    }
                }

                if (refresh)
                {
                    // clear cache
                    await syncManager.AddSyncEventAsync(await tenantManager.GetAliasAsync(), EntityNames.Site, site.SiteId, SyncEventActions.Refresh);
                }
            }
            else
            {
                log += $"No Criteria Provided<br />";
            }

            return log;
        }

        private async Task<bool> UpdateSettingsAsync(ISettingRepository settingRepository, string entityName, int entityId, string find, string replace, StringComparison comparisonType)
        {
            var changed = false;
            var settings = (await settingRepository.GetSettingsAsync(entityName, entityId)).ToList();
            foreach (var setting in settings)
            {
                if (setting.SettingValue != null && setting.SettingValue.Contains(find, comparisonType))
                {
                    setting.SettingValue = setting.SettingValue.Replace(find, replace, comparisonType);
                    await settingRepository.UpdateSettingAsync(setting);
                    changed = true;
                }
            }
            return changed;
        }
    }
}
