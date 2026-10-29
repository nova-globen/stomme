#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Help.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantId = Stomme.Tenancy.Domain.TenantId;

namespace Stomme.Tests.Help
{
    /// <summary>One site of one tenant, as the help suites address it.</summary>
    internal sealed record HelpSite(Tenant Tenant, TenantId TenantId, Alias Alias);

    /// <summary>
    /// The help suites' host helpers: the sanctioned tenant-then-DI-scope order, a second tenant on the same site id, and
    /// a runner pass - shared so the reconciler and provider suites address a site exactly the same way.
    /// </summary>
    internal static class HelpDrive
    {
        public const string Host = "localhost";
        public const string SecondHost = "help-second.localhost";

        public static Task<SiteContentReconciliationReport> RunReconcilersAsync(WebApplication app) =>
            new SiteContentReconciliationRunner(
                app.Services.GetRequiredService<IServiceScopeFactory>(),
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test")).RunAsync();

        public static async Task<HelpSite> SiteAsync(WebApplication app, string host)
        {
            var alias = await GateDrive.AliasAsync(app, host);
            using var scope = app.Services.CreateScope();
            var tenant = (await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Single(item => item.TenantId == alias.TenantId);
            return new HelpSite(tenant, FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId)!, alias);
        }

        /// <summary>A SECOND tenant over the SAME database, with an alias onto the SAME site id (the metering suites' shape).</summary>
        public static async Task<(HelpSite First, HelpSite Second)> AddSecondTenantAsync(WebApplication app)
        {
            var first = await SiteAsync(app, Host);

            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var second = await sp.GetRequiredService<ITenantRepository>().AddTenantAsync(new Tenant
                {
                    Name = "HelpSecond",
                    DBConnectionString = first.Tenant.DBConnectionString,
                    DBType = first.Tenant.DBType,
                    Version = first.Tenant.Version,
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = SecondHost,
                    TenantId = second.TenantId,
                    SiteId = first.Alias.SiteId,
                    IsDefault = true,
                });

                // The boot and install path decides a RavenDB tenant's database; this row was added behind it, so it
                // is told what that path decides for a tenant naming the installation's own database - to share it.
                app.Services.GetService<Stomme.Repository.Stores.RavenTenantDatabases>()?.Set(second.TenantId, null);
            }

            return (first, await SiteAsync(app, SecondHost));
        }

        /// <summary>
        /// The sanctioned order: the TENANT scope is entered BEFORE the DI scope, then the alias is set - so the scoped
        /// tenant context and the repository's site both answer for this site of this tenant.
        /// </summary>
        public static async Task<T> InSiteAsync<T>(WebApplication app, HelpSite site, Func<IServiceProvider, Task<T>> work)
        {
            using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(site.TenantId.Value);
            using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(site.Alias);
            return await work(scope.ServiceProvider);
        }

        public static Task<IReadOnlyList<HelpTopicAggregate>> TopicsAsync(WebApplication app, HelpSite site) =>
            InSiteAsync(app, site, sp => sp.GetRequiredService<IHelpTopicRepository>().ListAsync());
    }
}
