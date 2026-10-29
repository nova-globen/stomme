#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Connectivity.Health;
using Stomme.Infrastructure;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Infrastructure;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A switch set in <c>appsettings.override.json</c> is a switch that is ON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by driving a real install: <c>Health:Enabled = true</c> in the override, and the probe job logging "Health
    /// Probing Is Disabled" every minute. The jobs asked <c>IConfiguration</c>, which in this host is the web builder's
    /// default and does NOT include the override file - where a deployed install keeps every setting it writes. Every
    /// suite was green because each set the key on <c>builder.Configuration</c>, and because nothing ran a Health JOB
    /// at all: the drives call the pass directly.
    /// </para>
    /// <para>
    /// So each case here installs with the setting in the override FILE and runs the real reader - the job's own
    /// <c>ExecuteJobAsync</c>, the composed alert sink, the composed price list - in both directions.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class OverrideFileSwitchTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public OverrideFileSwitchTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_health_jobs_obey_the_override_file(bool enabled)
        {
            await WithInstallAsync(new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var probe = await RunJobAsync(app, scopes => new HealthProbeJob(scopes));
                var rollup = await RunJobAsync(app, scopes => new HealthRollupJob(scopes));
                _output.WriteLine($"[Health:Enabled={enabled}] probe: {probe} | rollup: {rollup}");

                var rows = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(0)).Count);

                if (enabled)
                {
                    Assert.DoesNotContain("Disabled", probe);
                    Assert.DoesNotContain("Disabled", rollup);
                    Assert.True(rows >= 1, "the probe job ran and recorded no subject at all");
                }
                else
                {
                    Assert.Contains("Disabled", probe);
                    Assert.Contains("Disabled", rollup);
                    Assert.Equal(0, rows);
                }
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_metering_jobs_obey_the_override_file(bool enabled)
        {
            await WithInstallAsync(new JsonObject { ["Metering"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var recovery = await RunJobAsync(app, scopes => new MeteringFoldRecoveryJob(scopes));
                var rollup = await RunJobAsync(app, scopes => new UsageRollupJob(scopes));
                _output.WriteLine($"[Metering:Enabled={enabled}] recovery: {recovery} | rollup: {rollup}");

                Assert.Equal(!enabled, recovery.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(!enabled, rollup.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_portability_build_job_obeys_the_override_file(bool enabled)
        {
            await WithInstallAsync(new JsonObject { ["Portability"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var build = await RunJobAsync(app, scopes => new Stomme.Modules.Portability.Execution.PortabilityBuildJob(scopes));
                _output.WriteLine($"[Portability:Enabled={enabled}] build: {build}");

                Assert.Equal(!enabled, build.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_cloud_cost_alert_job_obeys_the_override_file(bool enabled)
        {
            // The daily alert pass reads CloudUsage:Alerts:Enabled from the HOST's configuration.
            await WithInstallAsync(new JsonObject { ["CloudUsage"] = new JsonObject { ["Alerts"] = new JsonObject { ["Enabled"] = enabled } } }, async app =>
            {
                var pass = await RunJobAsync(app, scopes => new Stomme.Modules.CloudUsage.Application.CloudCostAlertJob(scopes));
                _output.WriteLine($"[CloudUsage:Alerts:Enabled={enabled}] pass: {pass}");

                Assert.Equal(!enabled, pass.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_collaboration_sweep_job_obeys_the_override_file(bool enabled)
        {
            // The lock sweep reads Collaboration:Enabled from the HOST's configuration each run;
            // with it on and nothing lapsed on a fresh install, the pass says so and touches nothing.
            await WithInstallAsync(new JsonObject { ["Collaboration"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var pass = await RunJobAsync(app, scopes => new Stomme.Modules.Collaboration.Infrastructure.CollaborationSweepJob(scopes));
                _output.WriteLine($"[Collaboration:Enabled={enabled}] pass: {pass}");

                Assert.Equal(!enabled, pass.Contains("Switched Off", StringComparison.Ordinal));
                Assert.Equal(enabled, pass.Contains("Nothing Lapsed", StringComparison.Ordinal));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_temp_file_cleanup_job_obeys_the_override_file(bool enabled)
        {
            // The daily sweep reads CostOptimization:Cleanup:TempFiles:Enabled from the HOST's configuration;
            // with it on and no directory named, the master tenant's pass says so and touches nothing.
            await WithInstallAsync(new JsonObject { ["CostOptimization"] = new JsonObject { ["Cleanup"] = new JsonObject { ["TempFiles"] = new JsonObject { ["Enabled"] = enabled } } } }, async app =>
            {
                var pass = await RunJobAsync(app, scopes => new Stomme.Modules.CostOptimization.Application.TempFileCleanupJob(scopes));
                _output.WriteLine($"[CostOptimization:Cleanup:TempFiles:Enabled={enabled}] pass: {pass}");

                Assert.Equal(!enabled, pass.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(enabled, pass.Contains("No Directories Configured", StringComparison.Ordinal));
            });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        public async Task The_pipeline_run_retention_obeys_the_override_file(int keepDays)
        {
            // Pipelines:Runs:KeepDays - how long a finished run is kept - is read from the HOST's configuration, where an
            // install keeps it; the retention task reads it through these options each tick. Pipelines:Forms:Enabled - whether the
            // site serves its public forms - is read through the same options, so it rides the same theory (on with retention).
            var forms = keepDays > 0;
            await WithInstallAsync(new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Runs"] = new JsonObject { ["KeepDays"] = keepDays }, ["Forms"] = new JsonObject { ["Enabled"] = forms }, ["Chats"] = new JsonObject { ["Enabled"] = forms } } }, app =>
            {
                var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Stomme.Modules.Pipelines.Triggers.PipelineTriggerOptions>>().Value;
                _output.WriteLine($"[Pipelines:Runs:KeepDays={keepDays}] read: {options.Runs.KeepDays}");
                Assert.Equal(keepDays, options.Runs.KeepDays);
                Assert.Equal(forms, options.Forms.Enabled);
                Assert.Equal(forms, options.Chats.Enabled);
                return Task.CompletedTask;
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_backup_schedule_job_obeys_the_override_file(bool enabled)
        {
            await WithInstallAsync(new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var tick = await RunJobAsync(app, scopes => new Stomme.Modules.Backup.Application.BackupScheduleJob(scopes));
                _output.WriteLine($"[Backup:Enabled={enabled}] tick: {tick}");

                Assert.Equal(!enabled, tick.Contains("Disabled", StringComparison.OrdinalIgnoreCase));
            });
        }

        [Theory]
        [InlineData(null, 2)]
        [InlineData(1, 2)]
        [InlineData(15, 1)]
        [InlineData(0, 0)]
        public async Task The_mail_server_probe_interval_obeys_the_override_file(int? minutes, int expectedConnects)
        {
            // A relay that counts connections locked a three-site host out of all mail on the job's per-minute connects
            // (Infrastructure BL-262). Two ticks back to back, against a listener that counts what reaches it.
            var health = new JsonObject { ["Enabled"] = true };
            if (minutes is not null)
            {
                health["MailServerProbeIntervalMinutes"] = minutes;
            }

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var connects = 0;
            using var stop = new System.Threading.CancellationTokenSource();
            var accepting = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    System.Threading.Interlocked.Increment(ref connects);
                }
            });

            try
            {
                await WithInstallAsync(new JsonObject { ["Health"] = health }, async app =>
                {
                    await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    {
                        var settings = sp.GetRequiredService<ISettingRepository>();
                        foreach (var (name, value) in new[] { ("SMTPHost", "127.0.0.1"), ("SMTPPort", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) })
                        {
                            await settings.AddSettingAsync(new Stomme.Models.Setting { EntityName = Stomme.Shared.EntityNames.Site, EntityId = alias.SiteId, SettingName = name, SettingValue = value, IsPrivate = true });
                        }

                        return true;
                    });

                    var first = await RunJobAsync(app, scopes => new HealthProbeJob(scopes));
                    var second = await RunJobAsync(app, scopes => new HealthProbeJob(scopes));
                    _output.WriteLine($"[{HealthSettings.MailServerProbeIntervalKey}={minutes?.ToString() ?? "absent"}] {first} | {second}");
                });

                // A connect completes before it is accepted; give the accept loop its moment.
                for (var waited = 0; waited < 20 && System.Threading.Volatile.Read(ref connects) < expectedConnects; waited++)
                {
                    await Task.Delay(100);
                }

                await Task.Delay(200);
                Assert.Equal(expectedConnects, System.Threading.Volatile.Read(ref connects));
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
                await Task.WhenAny(accepting, Task.Delay(1000));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_health_alert_sink_obeys_the_override_file(bool alerting)
        {
            await WithInstallAsync(new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = false, ["Alerting"] = alerting } }, async app =>
            {
                var sink = await GateDrive.InTenantAsync(app, Host, (sp, _) => Task.FromResult(sp.GetRequiredService<IHealthAlertSink>().GetType().Name));
                _output.WriteLine($"[Health:Alerting={alerting}] sink: {sink}");

                Assert.Equal(alerting ? nameof(NotificationHealthAlertSink) : nameof(NoOpHealthAlertSink), sink);
            });
        }

        [Fact]
        public async Task The_AI_price_list_is_read_from_the_override_file()
        {
            var pricing = new JsonObject
            {
                ["Enabled"] = true,
                ["AiPricing"] = new JsonObject
                {
                    ["testmodel"] = new JsonObject { ["InputPerMillion"] = 2.5, ["OutputPerMillion"] = 10, ["Currency"] = "sek" },
                },
            };

            await WithInstallAsync(new JsonObject { ["Metering"] = pricing }, app =>
            {
                // The premise has changed: the web builder's configuration reads the override file too now, so both agree.
                // The price list still has to come from the layered root, which is what the calculator is built from.
                Assert.Equal("sek", app.Services.GetRequiredService<IConfiguration>()["Metering:AiPricing:testmodel:Currency"]);
                Assert.Equal("sek", app.Services.GetRequiredService<IConfigurationRoot>()["Metering:AiPricing:testmodel:Currency"]);
                Assert.Same(app.Services.GetRequiredService<IConfigurationRoot>(), HostConfiguration.Resolve(app.Services));

                var (cost, currency) = app.Services.GetRequiredService<AiCostCalculator>().Price("testprovider", "testmodel", 1_000_000, 1_000_000);
                _output.WriteLine($"[pricing] {cost} {currency}");

                Assert.Equal(12.5m, cost);
                Assert.Equal("SEK", currency);
                return Task.CompletedTask;
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_connection_health_probe_obeys_the_override_file(bool enabled)
        {
            // The contributor offers its probe only when Connectivity:HealthProbes:Enabled is on in the
            // override FILE - the switch is read from this host's configuration, never the container's IConfiguration.
            await WithInstallAsync(new JsonObject { ["Connectivity"] = new JsonObject { ["HealthProbes"] = new JsonObject { ["Enabled"] = enabled } } }, async app =>
            {
                var probes = await GateDrive.InTenantAsync(app, Host, (sp, _) => Task.FromResult(
                    sp.GetServices<IHealthContributor>().Single(contributor => contributor.ContributorId == ConnectionHealthContributor.Id).Probes.Count));
                _output.WriteLine($"[Connectivity:HealthProbes:Enabled={enabled}] probes: {probes}");
                Assert.Equal(enabled ? 1 : 0, probes);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_demo_content_reconcilers_obey_the_override_file(bool enabled)
        {
            await WithInstallAsync(new JsonObject { ["DemoContent"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var (reconcilers, connections, datasets) = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => (
                    sp.GetServices<ISiteContentReconciler>().Select(reconciler => reconciler.Name).Where(name => name.StartsWith("Demo", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    (await sp.GetRequiredService<Stomme.Connectivity.Persistence.IConnectionRepository>().ListForSiteAsync(alias.SiteId)).Count,
                    (await sp.GetRequiredService<Stomme.Datasets.Persistence.IDatasetRepository>().ListForSiteAsync(alias.SiteId)).Count));
                _output.WriteLine($"[DemoContent:Enabled={enabled}] reconcilers: {string.Join(", ", reconcilers)}; connections {connections}, datasets {datasets}");

                Assert.Equal(enabled ? new[] { "Demo cloud cost board", "Demo connections", "Demo dashboard", "Demo datasets" } : Array.Empty<string>(), reconcilers);
                Assert.Equal(enabled ? (2, 6) : (0, 0), (connections, datasets));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_backup_archive_provider_is_read_from_the_override_file(bool s3)
        {
            // Backup:Archive is read from this host's configuration - the override file where a deployed install keeps it.
            var archive = s3
                ? new JsonObject
                {
                    ["Provider"] = "S3",
                    ["S3"] = new JsonObject { ["Endpoint"] = "http://s3.test:7070", ["Bucket"] = "override-bucket", ["Region"] = "home", ["AccessKeyId"] = "k", ["SecretAccessKey"] = "s" },
                }
                : new JsonObject { ["Provider"] = "Local" };
            await WithInstallAsync(new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true, ["Archive"] = archive } }, app =>
            {
                var stores = app.Services.GetRequiredService<Stomme.Modules.Backup.Infrastructure.BackupArchiveStores>();
                _output.WriteLine($"[Backup:Archive:Provider={(s3 ? "S3" : "Local")}] current: {stores.CurrentName}, {stores.Current.GetType().Name}");

                Assert.Equal(s3 ? "S3" : "Local", stores.CurrentName);
                Assert.Equal(s3 ? nameof(Stomme.Modules.Backup.Infrastructure.S3BackupArchiveStore) : nameof(Stomme.Modules.Backup.Infrastructure.LocalFileBackupArchiveStore), stores.Current.GetType().Name);
                Assert.Equal(s3 ? "override-bucket" : null, app.Services.GetRequiredService<Stomme.Modules.Backup.Infrastructure.BackupArchiveOptions>().S3.Bucket);
                return Task.CompletedTask;
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_assistant_installation_switch_is_read_from_the_override_file(bool enabled)
        {
            // AiAssistant:Enabled is read from this host's configuration, the override file included.
            await WithInstallAsync(new JsonObject { ["AiAssistant"] = new JsonObject { ["Enabled"] = enabled } }, async app =>
            {
                var options = app.Services.GetRequiredService<Stomme.Modules.AiAssistant.Application.AssistantOptions>();
                var available = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<Stomme.Modules.AiAssistant.Application.IAssistantModelAccess>().IsAvailableAsync());
                _output.WriteLine($"[AiAssistant:Enabled={enabled}] options: {options.Enabled}; available on the site: {available}");

                Assert.Equal(enabled, options.Enabled);
                Assert.False(available);   // the site's own switch is still off - a second gate
            });
        }
        private async Task WithInstallAsync(JsonObject settings, Func<WebApplication, Task> drive)
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                await drive(app);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>ExecuteJobAsync exactly as <c>HostedServiceBase</c> invokes it: one job scope per tenant, the tenant set.</summary>
        private static async Task<string> RunJobAsync(WebApplication app, Func<IServiceScopeFactory, HostedServiceBase> create)
        {
            using var scope = app.Services.CreateScope();
            var notes = string.Empty;
            foreach (var tenant in await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
            {
                scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes += await create(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(scope.ServiceProvider);
            }

            return notes;
        }
    }
}
