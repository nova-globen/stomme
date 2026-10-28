#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models;
using Stomme.Modules.Pipelines.Startup;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95 increment (a), the tenant bridge: on a REAL install, the engine's tenants are the install's sites, a request
    /// runs inside its site's engine tenant (the middleware after Stomme's tenant resolution), and a run DISPATCHED from
    /// inside a site lands under that site - the worker restores the tenant from the command header through the Tenants
    /// feature, which the conformance suite measured a dispatch losing without it.
    /// </summary>
    public sealed class PipelinesTenantBridgeTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_engine_lists_the_installs_sites_and_a_request_and_a_dispatch_run_inside_the_sites_tenant()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);

                // 1. The engine's tenants are the install's sites.
                var tenants = app.Services.GetRequiredService<ITenantService>();
                await tenants.RefreshAsync();
                var listed = (await tenants.ListAsync()).Select(t => t.Id).ToList();
                var siteTenant = Assert.Single(listed);
                Assert.True(EngineTenantIds.TryParse(siteTenant, out var tenantId, out var siteId), $"'{siteTenant}' is not a site tenant id");
                Assert.DoesNotContain(string.Empty, listed);

                // 2. A request enters its site's engine tenant and leaves it afterwards.
                using var scope = app.Services.CreateScope();
                var accessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                context.Items[Constants.HttpContextAliasKey] = new Alias { TenantId = tenantId, SiteId = siteId, Name = Host };
                string? seen = null;
                var middleware = new EngineTenantMiddleware(_ =>
                {
                    seen = accessor.TenantId;
                    return Task.CompletedTask;
                });
                await middleware.Invoke(context);
                Assert.Equal(siteTenant, seen);
                Assert.Equal(string.Empty, accessor.TenantId);

                // A request with no alias (the installer state) enters nothing.
                seen = "untouched";
                await middleware.Invoke(new DefaultHttpContext { RequestServices = scope.ServiceProvider });
                Assert.Equal(string.Empty, seen);

                // 3. A definition published and a run dispatched from inside the site land under the site.
                var correlation = "bridge-" + Guid.NewGuid().ToString("N");
                var bridge = scope.ServiceProvider.GetRequiredService<IEngineTenantBridge>();
                var site = await bridge.ResolveAsync(tenantId, siteId);
                Assert.Equal(siteTenant, site.Id);
                using (bridge.Enter(site))
                {
                    var publisher = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionPublisher>();
                    var definition = await publisher.NewAsync(new Sequence());
                    definition.Name = "Bridge test";
                    Assert.True((await publisher.PublishAsync(definition)).Succeeded);
                    Assert.Equal(siteTenant, definition.TenantId);

                    var response = await scope.ServiceProvider.GetRequiredService<IPipelineDispatcher>().DispatchAsync(
                        new DispatchPipelineDefinitionRequest(definition.Id) { CorrelationId = correlation }, options: null);
                    Assert.Null(response.Fault);

                    var instances = scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>();
                    PipelineInstance? instance = null;
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        instance = await instances.FindAsync(new PipelineInstanceFilter { CorrelationId = correlation });
                        if (instance?.Status == PipelineStatus.Finished)
                        {
                            break;
                        }

                        await Task.Delay(250);
                    }

                    Assert.NotNull(instance);
                    Assert.Equal((PipelineStatus.Finished, siteTenant), (instance.Status, instance.TenantId));
                }

                // Outside the site, the same run is invisible: it belongs to the site, not to the default tenant.
                Assert.Null(await scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { CorrelationId = correlation }));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
