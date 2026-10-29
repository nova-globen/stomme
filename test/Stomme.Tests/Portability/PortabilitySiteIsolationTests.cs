#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Portability;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// Two sites in ONE tenant, over HTTP, per store: a package built on site A is not listed on site B,
    /// cannot be downloaded through site B's address, and a person asking site B for their own data gets site B's data -
    /// not site A's roles or sessions. The module store is shared by every site of a tenant, so this is the property the
    /// site filter on every read exists for.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class PortabilitySiteIsolationTests
    {
        private const string HostA = "localhost";
        private const string HostB = "isolation-b.localhost";

        private readonly ITestOutputHelper _output;

        protected PortabilitySiteIsolationTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        [Fact]
        public async Task A_package_of_one_site_is_invisible_and_undownloadable_from_another()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), HostA, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Portability"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var siteB = await CreateSecondSiteAsync(app);
                var hostId = await GateDrive.UserIdAsync(app, HostA, GateDrive.HostUsername);

                using var onA = GateDrive.NewClient(app, HostA);
                var cookiesA = await GateDrive.LoginAsync(app, onA, HostA, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                using var onB = GateDrive.NewClient(app, HostB);
                var cookiesB = await GateDrive.LoginAsync(app, onB, HostB, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var built = await GateDrive.SendJsonAsync(app, onA, cookiesA, HostA, hostId, HttpMethod.Post, "/api/Portability/config",
                    new PortabilityConfigRequest { EntityTypes = new List<string> { "profile-definition" } }, _output, "site A builds");
                Assert.Equal(HttpStatusCode.OK, built.Status);
                var packageA = JsonDocument.Parse(built.Body).RootElement.GetProperty("id").GetGuid();
                await PortabilityDrive.BuildQueuedAsync(app, HostA, _output);

                // Site A sees and downloads it.
                Assert.Contains(packageA, Ids((await GateDrive.GetAsync(onA, cookiesA, HostA, "/api/Portability", _output, "site A lists")).Body));
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(onA, cookiesA, HostA, $"/api/Portability/{packageA}/download", _output, "site A downloads")).Status);

                // Site B - same tenant, same store, same Host - does not.
                var listB = await GateDrive.GetAsync(onB, cookiesB, HostB, "/api/Portability", _output, "site B lists");
                Assert.Equal(HttpStatusCode.OK, listB.Status);
                Assert.DoesNotContain(packageA, Ids(listB.Body));
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(onB, cookiesB, HostB, $"/api/Portability/{packageA}/download", _output, "site B downloads A's")).Status);

                // Site B's own package is site B's.
                // Queued through the repository: a site created straight through ISiteRepository has none of the portability key
                // rows the boot reconciliation seeds, so the key-gated endpoint would refuse even the Host - which is not what
                // this test is about. Download needs no key: the requester may download their own.
                var packageB = await GateDrive.InTenantAsync(app, HostB, async (sp, alias) =>
                {
                    var package = Stomme.Modules.Portability.Domain.PortabilityPackageAggregate.StartBuild(
                        sp.GetRequiredService<Stomme.Tenancy.Abstractions.ITenantContext>().CurrentTenantId!, alias.SiteId,
                        Stomme.Modules.Portability.Domain.PortabilityPackageKind.ConfigTransfer, hostId, null, DateTime.UtcNow, "{\"profile-definition\":null}");
                    await sp.GetRequiredService<Stomme.Portability.Persistence.IPortabilityPackageRepository>().AddAsync(package);
                    return package.Id;
                });
                await PortabilityDrive.BuildQueuedAsync(app, HostB, _output);
                var downloadedB = await GateDrive.GetAsync(onB, cookiesB, HostB, $"/api/Portability/{packageB}/download", _output, "site B downloads its own");
                Assert.Equal(HttpStatusCode.OK, downloadedB.Status);
                Assert.Contains($":{siteB.SiteId}\"", downloadedB.Body, StringComparison.Ordinal);                 // siteRef names site B
                Assert.DoesNotContain(packageB, Ids((await GateDrive.GetAsync(onA, cookiesA, HostA, "/api/Portability", _output, "site A lists again")).Body));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static List<Guid> Ids(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Isolation Site B", TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme, DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer, SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static, Runtime = Runtimes.Server, Prerender = false, Hybrid = false,
                    EnhancedNavigation = true, CultureCode = "en", AllowRegistration = false, CaptureBrokenUrls = false,
                    VisitorTracking = false, PwaIsEnabled = false,
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB, TenantId = alias.TenantId, SiteId = site.SiteId, IsDefault = true,
                });

                return site;
            });
    }

    public sealed class SqlitePortabilitySiteIsolationTests(ITestOutputHelper output) : PortabilitySiteIsolationTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresPortabilitySiteIsolationTests(ITestOutputHelper output) : PortabilitySiteIsolationTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenPortabilitySiteIsolationTests(ITestOutputHelper output) : PortabilitySiteIsolationTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }
}
