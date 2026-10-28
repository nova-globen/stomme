#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.RavenTenancy
{
    /// <summary>
    /// BL-176 - RavenDB's tenancy shape DRIVEN on an installed RavenDB host (infra UAT): a second tenant added the way the
    /// admin's Add-site page adds one, through the real installation path, with a database of its own in its connection
    /// string. Its site, pages, users and guestbook live in THAT database and not the installation's; the installation's
    /// DefaultConnection still names the installation's database; both sites render; and across a restart the routing is
    /// decided again the same way. And a backup carries every tenant database. (The rule for a tenant created before BL-176 is
    /// <see cref="RavenTenantDatabasePlannerTests"/>.)
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenTenantDatabaseDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string SecondHost = "second.localhost";

        [Fact]
        public async Task A_second_tenant_lives_in_the_database_its_connection_string_names_and_the_installation_keeps_its_own()
        {
            var raven = new RavenGateStore();
            using var scratch = await GateInstallTemplates.CloneAsync(raven, Host, output);
            var installationDatabase = raven.DatabaseToken;
            var secondDatabase = installationDatabase + "_second";
            try
            {
                var first = await GateDrive.InstallAsync(scratch, output);
                int secondTenantId;
                try
                {
                    var defaultConnection = first.Services.GetRequiredService<IConfigManager>().GetConnectionString(SettingKeys.ConnectionStringKey);
                    await ProvisionSecondAsync(first, raven, secondDatabase);
                    secondTenantId = (await GateDrive.AliasAsync(first, SecondHost)).TenantId;

                    // The installation's DefaultConnection is untouched - it names the installation's database.
                    Assert.Equal(defaultConnection, first.Services.GetRequiredService<IConfigManager>().GetConnectionString(SettingKeys.ConnectionStringKey));

                    await SignAsync(first, Host, "written by tenant one");
                    await SignAsync(first, SecondHost, "written by tenant two");
                    await AssertWhereAsync(installationDatabase, secondDatabase, secondTenantId);

                    var databases = first.Services.GetRequiredService<RavenTenantDatabases>();
                    Assert.True(databases.TryGet(secondTenantId, out var routed));
                    Assert.Equal(secondDatabase, routed);
                    await BothRenderAsync(first);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(first);
                }

                // Across a restart the routing is decided again from the same facts.
                var second = await GateDrive.InstallAsync(scratch, output, configure: null!, start: true);
                try
                {
                    Assert.Equal(new[] { "written by tenant one" }, await EntriesAsync(second, Host));
                    Assert.Equal(new[] { "written by tenant two" }, await EntriesAsync(second, SecondHost));
                    await BothRenderAsync(second);
                    await GateDrive.LoginAsync(second, GateDrive.NewClient(second, SecondHost), SecondHost, GateDrive.HostUsername, GateDrive.HostPassword, output);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(second);
                }
            }
            finally
            {
                await DropAsync(secondDatabase);
            }
        }

        /// <summary>Tenant two, with a database of its own, the way the admin's Add-site page adds it: the real installation path.</summary>
        private static async Task ProvisionSecondAsync(WebApplication app, RavenGateStore raven, string secondDatabase)
        {
            var provisioned = await InScopeAsync(app, sp => sp.GetRequiredService<IDatabaseManager>().InstallAsync(new InstallConfig
            {
                TenantName = "Second",
                IsNewTenant = true,
                DatabaseType = raven.DatabaseType,
                ConnectionString = $"Url={RavenGateStore.Url};Database={secondDatabase}",
                Aliases = SecondHost,
                SiteName = "Second",
                HostUsername = GateDrive.HostUsername,
                HostPassword = GateDrive.HostPassword,
                HostEmail = GateDrive.HostEmail,
                HostName = "Host",
                SiteTemplate = Constants.DefaultSiteTemplate,
                DefaultTheme = Constants.DefaultTheme,
                DefaultContainer = Constants.DefaultContainer,
                DefaultAdminContainer = string.Empty,
                RenderMode = RenderModes.Static,
                Runtime = Runtimes.Server,
            }));
            Assert.True(provisioned.Success, "tenant two was not provisioned: " + provisioned.Message);
        }

        /// <summary>
        /// A backup of a RavenDB installation carries EVERY tenant database (BL-176): the snapshot layout names tenant two's
        /// database by the tenants in it, and a whole-installation restore across a restart puts both tenants back.
        /// </summary>
        [Fact]
        public async Task A_backup_carries_each_tenant_database_and_a_restore_puts_both_tenants_back()
        {
            const string passphrase = "raven tenancy passphrase";
            var raven = new RavenGateStore();
            using var scratch = await GateInstallTemplates.CloneAsync(raven, Host, output, colocateModuleAggregates: false,
                extraSettings: new System.Text.Json.Nodes.JsonObject { ["Backup"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } });
            var secondDatabase = raven.DatabaseToken + "_second";
            try
            {
                Guid snapshot;
                var first = await GateDrive.InstallAsync(scratch, output);
                try
                {
                    await ProvisionSecondAsync(first, raven, secondDatabase);
                    await SignAsync(first, Host, "one before");
                    await SignAsync(first, SecondHost, "two before");
                    // A FRAMEWORK row in each tenant's own database (the guestbook is module data, restored by another participant).
                    await SetAsync(first, Host, "Marker", "one-before");
                    await SetAsync(first, SecondHost, "Marker", "two-before");

                    var engine = first.Services.CreateScope().ServiceProvider.GetRequiredService<Stomme.Modules.Backup.Application.BackupEngine>();
                    snapshot = await engine.CreateSnapshotAsync(passphrase, Stomme.Modules.Backup.Domain.RetentionTag.Manual);

                    await SignAsync(first, Host, "one after");
                    await SignAsync(first, SecondHost, "two after");
                    await SetAsync(first, SecondHost, "After", "two-after");
                    var staged = await engine.StageRestoreAsync(snapshot, passphrase);
                    Assert.True(staged.Ok, string.Join("; ", staged.Problems));
                }
                finally
                {
                    await GateDrive.ShutdownAsync(first);
                }

                var second = await GateDrive.InstallAsync(scratch, output);
                try
                {
                    Assert.Equal(new[] { "one before" }, await EntriesAsync(second, Host));
                    Assert.Equal(new[] { "two before" }, await EntriesAsync(second, SecondHost));
                    Assert.Equal("one-before", await GetAsync(second, Host, "Marker"));
                    Assert.Equal("two-before", await GetAsync(second, SecondHost, "Marker"));
                    Assert.Null(await GetAsync(second, SecondHost, "After"));
                    await BothRenderAsync(second);
                    var row = await second.Services.CreateScope().ServiceProvider.GetRequiredService<Stomme.Modules.Backup.Application.BackupEngine>().GetSnapshotAsync(snapshot);
                    Assert.NotNull(row!.LastRestoredOnUtc);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(second);
                }
            }
            finally
            {
                await DropAsync(secondDatabase);
            }
        }

        /// <summary>The site, its users and its guestbook entry are in tenant two's database; none of it in the installation's.</summary>
        private async Task AssertWhereAsync(string installationDatabase, string secondDatabase, int secondTenantId)
        {
            using var store = new DocumentStore { Urls = new[] { RavenGateStore.Url } }.Initialize();
            async Task<long> CountAsync(string database, string collection)
            {
                var statistics = await store.Maintenance.ForDatabase(database).SendAsync(new Raven.Client.Documents.Operations.GetCollectionStatisticsOperation());
                return statistics.Collections.TryGetValue(collection, out var count) ? count : 0;
            }

            foreach (var collection in new[] { "SiteAggregates", "PageAggregates", "RavenIdentityUsers", "GuestbookEntryAggregates" })
            {
                var inSecond = await CountAsync(secondDatabase, collection);
                var inInstallation = await CountAsync(installationDatabase, collection);
                output.WriteLine($"[where] {collection}: tenant two's database {inSecond}, the installation's {inInstallation}");
                Assert.True(inSecond > 0, $"{collection} has nothing in tenant two's database");
            }

            // One site per database: the installation's holds tenant one's only.
            Assert.Equal(1, await CountAsync(installationDatabase, "SiteAggregates"));
            Assert.Equal(1, await CountAsync(secondDatabase, "SiteAggregates"));
            output.WriteLine($"[where] tenant {secondTenantId} is in {secondDatabase}");
        }

        private async Task BothRenderAsync(WebApplication app)
        {
            using var one = GateDrive.NewClient(app, Host);
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(one, null, Host, "/", output, "tenant one's home")).Status);
            using var two = GateDrive.NewClient(app, SecondHost);
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(two, null, SecondHost, "/", output, "tenant two's home")).Status);
        }

        private static async Task DropAsync(string database)
        {
            using var store = new DocumentStore { Urls = new[] { RavenGateStore.Url } }.Initialize();
            if (await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(database)) is not null)
            {
                await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
            }
        }

        private static async Task<T> InScopeAsync<T>(WebApplication app, Func<IServiceProvider, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            return await work(scope.ServiceProvider);
        }

        private static Task SetAsync(WebApplication app, string host, string name, string value) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = name, SettingValue = value, IsPrivate = false });
                return 0;
            });

        private static Task<string?> GetAsync(WebApplication app, string host, string name) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) => (await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, alias.SiteId, name))?.SettingValue);

        private static Task SignAsync(WebApplication app, string host, string message) =>
            GateDrive.InTenantAsync(app, host, async (sp, _) =>
            {
                await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().AddAsync(GuestbookEntryAggregate.Sign(sp.GetRequiredService<ITenantContext>().CurrentTenantId!, 3, "Ada", message));
                return 0;
            });

        private static Task<string[]> EntriesAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, _) =>
                (await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().GetAllAsync()).Select(entry => entry.Message).OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }
}
