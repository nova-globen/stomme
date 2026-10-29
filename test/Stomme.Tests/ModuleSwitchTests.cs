#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Infrastructure;
using Stomme.Infrastructure;
using Stomme.Modules;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stomme.Extensions;
using Xunit;

// This test assembly is scanned like a module (its name holds "Stomme"): its switch is "stomme-tests".
[assembly: ModuleSwitch("stomme-tests")]

namespace Stomme.Tests
{
    /// <summary>
    /// <c>module.&lt;id&gt;.enabled=false</c> switches a module assembly's hosted services and endpoint
    /// contributors off on a host, so the one image both hosts run can carry a module (for example one with 30 Hz game loops) that runs on
    /// one host only. Silent configuration leaves everything on. This assembly stands in for the module: it declares the
    /// switch "stomme-tests", a hosted service (<see cref="ProbeHostedService"/>) and an endpoint contributor
    /// (<see cref="TransportContributorIndependenceTests.SignalRContributor"/>, whose "on" side that suite proves).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ModuleSwitchTests
    {
        private const string Key = "FeatureManagement:module.stomme-tests.enabled";

        /// <summary>A module's hosted service: does nothing, so its presence in any test host is harmless.</summary>
        public sealed class ProbeHostedService : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private static IConfiguration Config(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [Key] = value })
            .Build();

        [Fact]
        public void The_switch_ids_are_the_assemblys_declared_ones_and_only_a_definite_false_turns_it_off()
        {
            var assembly = typeof(ModuleSwitchTests).Assembly;
            Assert.Equal(new[] { "stomme-tests" }, ModuleFeatureFlags.SwitchIds(assembly));
            Assert.True(ModuleFeatureFlags.IsSwitchedOff(FeatureConfigurationSources.FromConfiguration(Config("false")), assembly));
            Assert.False(ModuleFeatureFlags.IsSwitchedOff(FeatureConfigurationSources.FromConfiguration(Config("true")), assembly));
            Assert.False(ModuleFeatureFlags.IsSwitchedOff(FeatureConfigurationSources.FromConfiguration(Config(null)), assembly));

            // The host's own assembly declares no switch and no module: nothing can turn it off.
            Assert.Empty(ModuleFeatureFlags.SwitchIds(typeof(Program).Assembly));
            Assert.False(ModuleFeatureFlags.IsSwitchedOff(FeatureConfigurationSources.FromConfiguration(Config("false")), typeof(Program).Assembly));
        }

        [Fact]
        public async Task A_switched_off_module_maps_no_hub()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transport:SignalR:Enabled"] = "true",
                ["Transport:Grpc:Enabled"] = "false",
                [Key] = "false",
            });
            builder.Services.AddRouting();
            builder.Services.AddStommeTransport(builder.Configuration);

            await using var app = builder.Build();
            app.UseRouting();
            app.UseStommeTransport(builder.Configuration);
            app.MapStommeTransport(builder.Configuration);
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            var connection = new HubConnectionBuilder()
                .WithUrl(url + TransportContributorIndependenceTests.SignalRContributor.HubRoute, o => o.Transports = HttpTransportType.LongPolling)
                .Build();
            await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync()); // the hub's route does not exist
            await connection.DisposeAsync();
        }

        [Fact]
        public async Task A_switched_off_module_has_no_hosted_service_in_the_real_composition_and_an_unswitched_one_has()
        {
            var on = await HostedServicesAsync(switchedOff: false);
            Assert.Contains(on.services, service => service is ProbeHostedService);
            Assert.DoesNotContain(on.switchedOff, item => item.AssemblyName == typeof(ModuleSwitchTests).Assembly.GetName().Name);
            var off = await HostedServicesAsync(switchedOff: true);
            Assert.DoesNotContain(off.services, service => service is ProbeHostedService);
            var record = Assert.Single(off.switchedOff, item => item.AssemblyName == typeof(ModuleSwitchTests).Assembly.GetName().Name);
            Assert.Equal(new[] { "stomme-tests" }, record.SwitchIds);
        }

        // The composition Program builds - the same AddStomme scan a deployed host runs - with the switch given the way a
        // compose file gives it, as an environment variable. Built, never started or installed.
        private static async Task<(List<IHostedService> services, List<SwitchedOffModuleAssembly> switchedOff)> HostedServicesAsync(bool switchedOff)
        {
            const string variable = "FeatureManagement__module.stomme-tests.enabled";
            var contentRoot = Path.Combine(Path.GetTempPath(), "stomme-module-switch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(contentRoot, "appsettings.json"));
            var previousVariable = Environment.GetEnvironmentVariable(variable);
            var previousOverride = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(variable, switchedOff ? "false" : null);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, contentRoot);
            try
            {
                var app = await Program.BuildAppAsync(new[] { "--contentRoot", contentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
                try
                {
                    return (app.Services.GetServices<IHostedService>().ToList(), app.Services.GetServices<SwitchedOffModuleAssembly>().ToList());
                }
                finally
                {
                    await app.DisposeAsync();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previousVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, previousOverride);
                ScratchSqlite.ReleaseInstall(contentRoot);
                try
                {
                    Directory.Delete(contentRoot, true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
