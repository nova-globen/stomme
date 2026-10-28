#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity.Persistence;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Modules.Connectivity.Infrastructure;
using Stomme.Modules.Datasets.Domain;
using Stomme.Modules.Datasets.Infrastructure;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tests.Help;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DemoContent
{
    /// <summary>
    /// BL-94 increment 14 (D110 Q7) - demo content is OFF by default and ADDITIVE when on: a stock install carries no
    /// demo connection and no demo dataset and registers neither reconciler; with <c>DemoContent:Enabled</c> in the
    /// override file the install path writes two connections and three datasets per site (the customers set Restricted
    /// by the classifier, masked, its preview refused); a second boot changes nothing; an operator's rename and delete
    /// survive every later boot because the site is stamped; clearing the stamp is a "reset to seed" that completes the
    /// set; and a second tenant on the SAME site id receives its own rows under its own tenant (D103's two guards).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DemoContentReconciliationTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static JsonObject On() => new() { ["DemoContent"] = new JsonObject { ["Enabled"] = true } };

        [Fact]
        public async Task A_stock_install_carries_no_demo_content_and_registers_neither_reconciler()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var report = await HelpDrive.RunReconcilersAsync(app);
                output.WriteLine($"[off] visited {report.SitesVisited}, failures {report.Failures.Count}");
                Assert.Empty(report.Failures);

                var (connections, datasets, reconcilers) = await CountAsync(app, Host);
                Assert.Equal((0, 0), (connections, datasets));
                Assert.DoesNotContain(reconcilers, name => name is "Demo connections" or "Demo datasets" or "Demo dashboard" or "Demo cloud cost board");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_switch_on_the_demo_set_is_written_once_and_an_operators_edits_survive_every_later_boot()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: On());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                // The install path already ran the runner: the set is there without a second call.
                var site = await HelpDrive.SiteAsync(app, Host);
                var connections = await ConnectionsAsync(app, site);
                var datasets = await DatasetsAsync(app, site);
                output.WriteLine($"[on] connections: {string.Join(", ", connections.Select(c => $"{c.Name} ({c.ConnectorType}, {c.Status})"))}");
                output.WriteLine($"[on] datasets: {string.Join(", ", datasets.Select(d => $"{d.Name} ({d.OriginReference}, {d.Classification})"))}");
                Assert.Equal(new[] { "Demo PostgreSQL", "Demo REST API" }, connections.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
                Assert.All(connections, c => Assert.Equal(ConnectionStatus.Untested, c.Status));
                Assert.Equal(new[] { "password" }, connections.Single(c => c.Name == "Demo PostgreSQL").CredentialFields);
                Assert.Equal(new[] { "Cloud budget status", "Demo customers", "Demo inventory", "Demo sales", "Multi-cloud cost", "Underutilized resources" }, datasets.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal)); // with the cloud cost board's three (BL-97 increment 5)
                var customers = datasets.Single(d => d.Name == "Demo customers");
                Assert.Equal((DataClassification.Restricted, "demo:customers", OriginKind.DemoSeed), (customers.Classification, customers.OriginReference, customers.OriginKind));
                Assert.Equal(DataClassification.Internal, datasets.Single(d => d.Name == "Demo inventory").Classification); // the classifier's floor
                Assert.Empty(customers.ToDescriptor().Summary.Columns.SelectMany(column => column.Samples)); // masked
                Assert.False(customers.ToDescriptor().Summary.Columns.Any(column => column.Min is not null));

                // A second pass: nothing changes.
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                Assert.Equal((2, 6), ((await ConnectionsAsync(app, site)).Count, (await DatasetsAsync(app, site)).Count));

                // The operator renames a connection and deletes a dataset; the next boot leaves both edits alone.
                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    var repo = sp.GetRequiredService<IConnectionRepository>();
                    var rest = (await repo.FindByNameAsync(site.Alias.SiteId, "Demo REST API"))!;
                    rest.Reconfigure("Our REST API", Stomme.Connectivity.ConnectorEndpointKind.Url, rest.Endpoint.Value, rest.Settings.GetSettings(), rest.CredentialFields, rest.TimeoutSeconds, DateTime.UtcNow);
                    await repo.UpdateAsync(rest);
                    var datasetsRepo = sp.GetRequiredService<IDatasetRepository>();
                    var inventory = (await datasetsRepo.FindByNameAsync(site.Alias.SiteId, "Demo inventory"))!;
                    await datasetsRepo.DeleteAsync(inventory);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return 0;
                });
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                connections = await ConnectionsAsync(app, site);
                datasets = await DatasetsAsync(app, site);
                Assert.Equal(new[] { "Demo PostgreSQL", "Our REST API" }, connections.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal(new[] { "Cloud budget status", "Demo customers", "Demo sales", "Multi-cloud cost", "Underutilized resources" }, datasets.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));

                // Clearing the stamp is the operator's "reset to seed": what is missing by name or origin comes back, the rest is untouched.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    foreach (var module in new[] { ConnectionsDemoContentReconciler.Module, DatasetsDemoContentReconciler.Module })
                    {
                        var stamp = await settings.GetSettingAsync(EntityNames.Site, alias.SiteId, DemoContentStamp.SettingName(module, site.TenantId));
                        Assert.NotNull(stamp);
                        await settings.DeleteSettingAsync(EntityNames.Site, stamp.SettingId);
                    }

                    return 0;
                });
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                connections = await ConnectionsAsync(app, site);
                datasets = await DatasetsAsync(app, site);
                Assert.Equal(new[] { "Demo PostgreSQL", "Demo REST API", "Our REST API" }, connections.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal(new[] { "Cloud budget status", "Demo customers", "Demo inventory", "Demo sales", "Multi-cloud cost", "Underutilized resources" }, datasets.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal)); // with the cloud cost board's three (BL-97 increment 5)
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_second_tenant_on_the_same_site_id_receives_its_own_demo_set_under_its_own_tenant()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: On());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var (first, second) = await HelpDrive.AddSecondTenantAsync(app);
                Assert.Equal((0, 0), ((await ConnectionsAsync(app, second)).Count, (await DatasetsAsync(app, second)).Count));

                var report = await HelpDrive.RunReconcilersAsync(app);
                output.WriteLine($"[two tenants] visited {report.SitesVisited}, failures: {string.Join(" | ", report.Failures)}");
                Assert.Empty(report.Failures);

                Assert.Equal((2, 6), ((await ConnectionsAsync(app, first)).Count, (await DatasetsAsync(app, first)).Count));
                Assert.Equal((2, 6), ((await ConnectionsAsync(app, second)).Count, (await DatasetsAsync(app, second)).Count));
                Assert.NotEqual((await ConnectionsAsync(app, first)).Select(c => c.Id).OrderBy(id => id).ToList(), (await ConnectionsAsync(app, second)).Select(c => c.Id).OrderBy(id => id).ToList());
                Assert.Equal((await DatasetsAsync(app, first)).Select(d => d.TenantId).Distinct().Single(), first.TenantId);
                Assert.Equal((await DatasetsAsync(app, second)).Select(d => d.TenantId).Distinct().Single(), second.TenantId);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<IReadOnlyList<ConnectionAggregate>> ConnectionsAsync(WebApplication app, HelpSite site) =>
            HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IConnectionRepository>().ListForSiteAsync(site.Alias.SiteId));

        private static Task<IReadOnlyList<DatasetAggregate>> DatasetsAsync(WebApplication app, HelpSite site) =>
            HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IDatasetRepository>().ListForSiteAsync(site.Alias.SiteId));

        private static Task<(int Connections, int Datasets, List<string> Reconcilers)> CountAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) => (
                (await sp.GetRequiredService<IConnectionRepository>().ListForSiteAsync(alias.SiteId)).Count,
                (await sp.GetRequiredService<IDatasetRepository>().ListForSiteAsync(alias.SiteId)).Count,
                sp.GetServices<ISiteContentReconciler>().Select(reconciler => reconciler.Name).ToList()));
    }
}
