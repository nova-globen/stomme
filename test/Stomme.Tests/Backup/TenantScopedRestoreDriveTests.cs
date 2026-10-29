#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// A restore of ONE tenant of a two-tenant installation.
    /// The second tenant is provisioned the way the admin's Add-site page does it, through the real installation path,
    /// with a database of its own. Both tenants are written after the snapshot; tenant two is staged for restore alone;
    /// across the restart tenant two is back to the snapshot (its site setting, its guestbook, its files) while tenant
    /// one keeps everything it wrote after it - and the master's tenant and alias rows are untouched. A selection that
    /// names a tenant the snapshot does not hold, or that would split a shared physical database, is refused at
    /// pre-flight.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class TenantScopedRestoreDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string SecondHost = "second.localhost";
        private const string Passphrase = "tenant scoped passphrase";

        [Fact]
        public async Task A_restore_of_one_tenant_puts_that_tenant_back_and_leaves_the_other_as_it_was()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } });
            Guid snapshot;
            int firstTenantId;
            int secondTenantId;

            var first = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(first);
            try
            {
                firstTenantId = (await GateDrive.AliasAsync(first, Host)).TenantId;

                // ---- tenant two, with a database of its own, through the real installation path ----
                var provisioned = await InScopeAsync(first, sp => sp.GetRequiredService<IDatabaseManager>().InstallAsync(new InstallConfig
                {
                    TenantName = "Second",
                    IsNewTenant = true,
                    DatabaseType = new SqliteGateStore().DatabaseType,
                    ConnectionString = "Data Source=" + Path.Combine(scratch.ContentRoot, "second-tenant.db"),
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
                secondTenantId = (await GateDrive.AliasAsync(first, SecondHost)).TenantId;
                Assert.NotEqual(firstTenantId, secondTenantId);
                output.WriteLine($"[tenants] one={firstTenantId} two={secondTenantId}");

                await SetAsync(first, Host, "Marker", "one-before");
                await SetAsync(first, SecondHost, "Marker", "two-before");
                await SignAsync(first, Host, "one before");
                await SignAsync(first, SecondHost, "two before");

                snapshot = await Engine(first).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);

                await SetAsync(first, Host, "After", "one-after");
                await SetAsync(first, SecondHost, "After", "two-after");
                await SignAsync(first, Host, "one after");
                await SignAsync(first, SecondHost, "two after");

                // ---- refusals: a tenant the snapshot does not hold; a selection that splits a shared database ----
                var outside = await Engine(first).PreflightAsync(snapshot, Passphrase, new[] { 999 });
                Assert.Contains(outside.Problems, problem => problem.Contains("not in the snapshot", StringComparison.Ordinal));

                var sharing = await InScopeAsync(first, async sp =>
                {
                    var tenants = sp.GetRequiredService<ITenantRepository>();
                    var master = (await tenants.GetTenantsAsync()).Single(tenant => tenant.TenantId == firstTenantId);
                    return (await tenants.AddTenantAsync(new Tenant { Name = "SharesWithOne", DBConnectionString = master.DBConnectionString, DBType = master.DBType, Version = master.Version })).TenantId;
                });
                var split = await Engine(first).PreflightAsync(snapshot, Passphrase, new[] { firstTenantId });
                Assert.Contains(split.Problems, problem => problem.Contains("share one database", StringComparison.Ordinal));
                await InScopeAsync(first, async sp => { await sp.GetRequiredService<ITenantRepository>().DeleteTenantAsync(sharing); return 0; });

                // ---- tenant two alone ----
                var staged = await Engine(first).StageRestoreAsync(snapshot, Passphrase, new[] { secondTenantId });
                Assert.True(staged.Ok, string.Join("; ", staged.Problems));
                Assert.True(System.IO.File.Exists(Engine(first).RequestFile));
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            var second = await GateDrive.InstallAsync(scratch, output, configure: null!, start: true);
            try
            {
                // Tenant one: exactly as it was, including what came after the snapshot.
                Assert.Equal("one-before", await GetAsync(second, Host, "Marker"));
                Assert.Equal("one-after", await GetAsync(second, Host, "After"));
                Assert.Equal(new[] { "one after", "one before" }, await EntriesAsync(second, Host));

                // Tenant two: back to the snapshot.
                Assert.Equal("two-before", await GetAsync(second, SecondHost, "Marker"));
                Assert.Null(await GetAsync(second, SecondHost, "After"));
                Assert.Equal(new[] { "two before" }, await EntriesAsync(second, SecondHost));

                // The master's rows were not touched: both tenants and both aliases are there.
                var aliases = await InScopeAsync(second, async sp => (await sp.GetRequiredService<IAliasRepository>().GetAliasesAsync()).Select(alias => alias.Name).ToList());
                Assert.Contains(Host, aliases);
                Assert.Contains(SecondHost, aliases);
                var tenantIds = await InScopeAsync(second, async sp => (await sp.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Select(tenant => tenant.TenantId).OrderBy(id => id).ToList());
                Assert.Equal(new[] { firstTenantId, secondTenantId }, tenantIds);

                var catalogue = await Engine(second).ListSnapshotsAsync();
                Assert.Equal(BackupSnapshotStatus.Available, catalogue.Single(row => row.Id == snapshot).Status);
                Assert.Contains(catalogue, row => row.RetentionTag == RetentionTag.PreRestore && row.Status == BackupSnapshotStatus.Available);
                Assert.False(System.IO.File.Exists(Engine(second).RequestFile));

                // Both sites render.
                using var one = GateDrive.NewClient(second, Host);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(one, null, Host, "/", output, "tenant one's home")).Status);
                using var two = GateDrive.NewClient(second, SecondHost);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(two, null, SecondHost, "/", output, "tenant two's home")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

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
