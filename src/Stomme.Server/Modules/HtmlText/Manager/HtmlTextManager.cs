using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Interfaces;
using Stomme.Migrations.Framework;
using Stomme.Models;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;

// ReSharper disable ConvertToUsingDeclaration

namespace Stomme.Modules.HtmlText.Manager
{
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class HtmlTextManager : MigratableModuleBase, IInstallable, IPortable, ISynchronizable, ISearchable
    {
        private readonly IHtmlTextRepository _htmlTextRepository;
        private readonly IDBContextDependencies _DBContextDependencies;
        private readonly ISqlRepository _sqlRepository;
        private readonly ITenantManager _tenantManager;
        private readonly ICacheManager _cache;

        public HtmlTextManager(IHtmlTextRepository htmlTextRepository, IDBContextDependencies DBContextDependencies, ISqlRepository sqlRepository, ITenantManager tenantManager, ICacheManager cache)
        {
            _htmlTextRepository = htmlTextRepository;
            _DBContextDependencies = DBContextDependencies;
            _sqlRepository = sqlRepository;
            _tenantManager = tenantManager;
            _cache = cache;
        }

        // IInstallable implementation
        public async Task<bool> InstallAsync(Tenant tenant, string version)
        {
            // Provider-aware (D55). EF migrations ARE the relational schema (D3); a document store is schemaless and
            // reuses the framework tenant database D54 already ensured, so its bootstrap is a NO-OP — the aggregate's
            // dynamic Where+OrderBy auto-indexes on first read. This guard is load-bearing: MigrateModulesAsync calls
            // InstallAsync UNCONDITIONALLY and SWALLOWS the throw (the confirmed D51 mechanism), so an unguarded EF
            // migrator would throw on Raven (the context has no relational provider configured) and the site would
            // silently ship without its content module, the failure surfacing only at render.
            if (!FrameworkStoreProviderSelection.IsRelationalProvider(tenant.DBType))
            {
                return true;
            }

            if (tenant.DBType == Constants.DefaultDBType && version == "1.0.1")
            {
                // version 1.0.0 used SQL scripts rather than migrations, so we need to seed the migration history table
                await _sqlRepository.ExecuteNonQueryAsync(tenant, MigrationUtils.BuildInsertScript("HtmlText.01.00.00.00"));
            }
            return await MigrateAsync(new HtmlTextContext(_DBContextDependencies), tenant, MigrationType.Up);
        }

        public async Task<bool> UninstallAsync(Tenant tenant)
        {
            // Symmetric with InstallAsync: nothing relational to tear down on a document store (D55).
            if (!FrameworkStoreProviderSelection.IsRelationalProvider(tenant.DBType))
            {
                return true;
            }
            return await MigrateAsync(new HtmlTextContext(_DBContextDependencies), tenant, MigrationType.Down);
        }

        // IPortable implementation
        public async Task<string> ExportModuleAsync(Module module)
        {
            string content = "";
            var htmltext = await _htmlTextRepository.GetHtmlTextAsync(module.ModuleId);
            if (htmltext != null)
            {
                content = WebUtility.HtmlEncode(htmltext.Content);
            }
            return content;
        }

        public async Task ImportModuleAsync(Module module, string content, string version)
        {
            await SaveModuleContentAsync(module, content);
        }

        // ISynchronizable implementation
        public async Task<string> ExtractModuleAsync(Module module, DateTime lastSynchronizedOn)
        {
            string content = "";
            var htmltext = await _htmlTextRepository.GetHtmlTextAsync(module.ModuleId);
            if (htmltext != null && htmltext.CreatedOn > lastSynchronizedOn)
            {
                content = WebUtility.HtmlEncode(htmltext.Content);
            }
            return content;
        }

        public async Task LoadModuleAsync(Module module, string content)
        {
            await SaveModuleContentAsync(module, content);
        }

        private async Task SaveModuleContentAsync(Module module, string content)
        {
            content = WebUtility.HtmlDecode(content);
            var htmlText = new Models.HtmlText();
            htmlText.ModuleId = module.ModuleId;
            htmlText.Content = content;
            await _htmlTextRepository.AddHtmlTextAsync(htmlText);

            //clear the cache for the module
            await _cache.RemoveCacheAsync(await _tenantManager.GetAliasAsync(), $"HtmlText:{module.ModuleId}");
        }

        // ISearchable implementation
        public async Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn)
        {
            var searchContents = new List<SearchContent>();

            var htmltext = await _htmlTextRepository.GetHtmlTextAsync(pageModule.ModuleId);
            if (htmltext != null && htmltext.CreatedOn >= lastIndexedOn)
            {
                searchContents.Add(new SearchContent
                {
                    Body = htmltext.Content,
                    ContentModifiedBy = htmltext.CreatedBy,
                    ContentModifiedOn = htmltext.CreatedOn
                });
            }

            return searchContents;
        }
    }
}
