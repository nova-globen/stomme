#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using File = System.IO.File;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-258 (infra ruling C30): <c>Backup:Schedule:IntervalHours</c>, <c>RetentionDays</c> and <c>KeepLast</c>, given as
    /// ENVIRONMENT VARIABLES the way a compose file declares them, seed the installation's first backup schedule at boot and
    /// enable the Backup Schedule Job - so an unattended host takes backups with nobody at admin/backup. A restart seeds
    /// nothing twice and keeps what the Host changed; an invalid schedule refuses the boot by name with nothing written.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ConfiguredBackupScheduleTests
    {
        private const string Root = "localhost";
        private const string Password = "B4ckup!Config$2026";
        private const string Passphrase = "the seeded schedule's passphrase";

        private readonly ITestOutputHelper _output;

        public ConfiguredBackupScheduleTests(ITestOutputHelper output) => _output = output;

        // ---- the checks, before anything is written -------------------------------------------------------------------

        private static BackupOptions Options(bool enabled = true, int? interval = 12, int? days = null, int? keep = null, string passphrase = Passphrase) => new()
        {
            Enabled = enabled,
            Schedule = new BackupOptions.ScheduleOptions { IntervalHours = interval, RetentionDays = days, KeepLast = keep, Passphrase = passphrase }
        };

        [Fact]
        public void A_valid_schedule_has_no_problems_and_an_absent_or_switched_off_one_is_not_checked()
        {
            Assert.Empty(ConfiguredBackupSchedule.Problems(Options()));
            Assert.Empty(ConfiguredBackupSchedule.Problems(Options(days: 14, keep: 3)));
            Assert.Empty(ConfiguredBackupSchedule.Problems(Options(interval: null, passphrase: "")));      // nothing configured
            Assert.Empty(ConfiguredBackupSchedule.Problems(Options(enabled: false, interval: 0)));         // Backup:Enabled is the master switch
            Assert.False(ConfiguredBackupSchedule.IsConfigured(Options(interval: null)));
        }

        [Theory]
        [InlineData("no-interval", "Backup:Schedule:IntervalHours is required")]
        [InlineData("interval-zero", "Backup:Schedule:IntervalHours is 1-")]
        [InlineData("interval-huge", "Backup:Schedule:IntervalHours is 1-")]
        [InlineData("days", "Backup:Schedule:RetentionDays is at least 1")]
        [InlineData("keep", "Backup:Schedule:KeepLast is at least 1")]
        [InlineData("passphrase", "Backup:Schedule:Passphrase is not set")]
        public void An_invalid_schedule_is_refused_by_the_settings_name(string fault, string expected)
        {
            var options = fault switch
            {
                "no-interval" => Options(interval: null, keep: 3),
                "interval-zero" => Options(interval: 0),
                "interval-huge" => Options(interval: BackupScheduleAggregate.MaximumIntervalHours + 1),
                "days" => Options(days: 0),
                "keep" => Options(keep: 0),
                _ => Options(passphrase: "short")
            };

            var problems = ConfiguredBackupSchedule.Problems(options);
            Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
            Assert.DoesNotContain(problems, problem => problem.Contains("short", StringComparison.Ordinal)); // never a value
        }

        // ---- on a real boot ----------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_first_boot_seeds_the_schedule_and_enables_the_job_and_a_restart_keeps_the_hosts_edit()
        {
            using var scratch = new Scratch();
            using var env = new EnvironmentVariables(new Dictionary<string, string>
            {
                ["Backup__Enabled"] = "true",
                ["Backup__Schedule__IntervalHours"] = "12",
                ["Backup__Schedule__PassphraseFile"] = scratch.PassphraseFile
            });

            var first = await BootAsync(scratch, expectSuccess: true);
            try
            {
                var seeded = Assert.Single(await SchedulesAsync(first));
                Assert.True(seeded.Enabled);
                Assert.Equal((12, ConfiguredBackupSchedule.DefaultRetentionDays, ConfiguredBackupSchedule.DefaultKeepLast), (seeded.IntervalHours, seeded.RetentionDays, seeded.KeepLast));
                Assert.True((await JobAsync(first))?.IsEnabled, "the Backup Schedule Job was not enabled");

                // The job, run as HostedServiceBase runs it, takes the scheduled snapshot: the seeded schedule is due.
                var notes = await TickAsync(first);
                _output.WriteLine(notes);
                Assert.Contains("Snapshot", notes, StringComparison.Ordinal);
                var snapshot = Assert.Single(await Engine(first).ListSnapshotsAsync());
                Assert.Equal(BackupSnapshotStatus.Available, snapshot.Status);
                Assert.Equal(RetentionTag.Scheduled, snapshot.RetentionTag);
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(scratch.ContentRoot, "Data"), "*", SearchOption.AllDirectories)
                    .Where(file => file.Contains("backups", StringComparison.OrdinalIgnoreCase)));

                // The Host edits the schedule (as admin/backup reads it now: the tick marked it run) and switches the job off.
                var edited = Assert.Single(await SchedulesAsync(first));
                Assert.NotNull(edited.LastRunOnUtc);
                edited.Redefine(48, 10, 3, enabled: false);
                await Engine(first).SaveScheduleAsync(edited);
                await DisableJobAsync(first);
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            var restarted = await BootAsync(scratch, expectSuccess: true);
            try
            {
                var kept = Assert.Single(await SchedulesAsync(restarted));
                Assert.Equal((48, 10, 3, false), (kept.IntervalHours, kept.RetentionDays, kept.KeepLast, kept.Enabled));
                Assert.False((await JobAsync(restarted))?.IsEnabled, "a restart switched the Host's disabled job back on");
            }
            finally
            {
                await GateDrive.ShutdownAsync(restarted);
            }
        }

        [Fact]
        public async Task An_invalid_schedule_refuses_the_boot_by_name_and_writes_nothing()
        {
            using var scratch = new Scratch();
            using var env = new EnvironmentVariables(new Dictionary<string, string>
            {
                ["Backup__Enabled"] = "true",
                ["Backup__Schedule__IntervalHours"] = "0",
                ["Backup__Schedule__Passphrase"] = "too short"
            });

            var app = await BootAsync(scratch, expectSuccess: false);
            try
            {
                Assert.Empty(await SchedulesAsync(app));
                Assert.False((await JobAsync(app))?.IsEnabled ?? false, "the job was enabled beside an invalid schedule");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------------------------------

        private async Task<WebApplication> BootAsync(Scratch scratch, bool expectSuccess)
        {
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            _output.WriteLine($"[boot] success={install.Success} message='{install.Message}'");
            if (expectSuccess)
            {
                Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
                // The stores' own start-up work (the feature store's tables among it), without the job loops: the test runs
                // the job's tick itself, as BackupScheduleJobTests does.
                await GateDrive.StartNonJobHostedServicesAsync(app);
            }
            else
            {
                Assert.False(install.Success);
                Assert.Contains("The configured backup schedule is not valid, so nothing was seeded", install.Message);
                Assert.Contains("Backup:Schedule:IntervalHours is 1-", install.Message);
                Assert.Contains("Backup:Schedule:Passphrase is not set", install.Message);
                Assert.DoesNotContain("too short", install.Message); // a passphrase is never echoed
            }

            return app;
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private static async Task<List<BackupScheduleAggregate>> SchedulesAsync(WebApplication app) => (await Engine(app).ListSchedulesAsync()).ToList();

        private static async Task<Job?> JobAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetJobAsync(Utilities.GetFullTypeName(typeof(BackupScheduleJob).AssemblyQualifiedName));
        }

        private static async Task DisableJobAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var job = await jobs.GetJobAsync(Utilities.GetFullTypeName(typeof(BackupScheduleJob).AssemblyQualifiedName));
            job.IsEnabled = false;
            await jobs.UpdateJobAsync(job);
        }

        /// <summary>One tick, as <c>HostedServiceBase</c> runs it: per tenant, its tenant scope, a fresh DI scope, SetTenant.</summary>
        private static async Task<string> TickAsync(WebApplication app)
        {
            List<Tenant> tenants;
            using (var listing = app.Services.CreateScope())
            {
                tenants = (await listing.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
            }

            var notes = new System.Text.StringBuilder();
            foreach (var tenant in tenants)
            {
                using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId)!.Value);
                using var jobScope = app.Services.CreateScope();
                jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes.Append(await new BackupScheduleJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(jobScope.ServiceProvider));
            }

            return notes.ToString();
        }

        /// <summary>Process environment variables for one test, restored after it (the collection runs one at a time).</summary>
        private sealed class EnvironmentVariables : IDisposable
        {
            private readonly Dictionary<string, string?> _previous = new();

            public EnvironmentVariables(Dictionary<string, string> values)
            {
                foreach (var (key, value) in values)
                {
                    _previous[key] = Environment.GetEnvironmentVariable(key);
                    Environment.SetEnvironmentVariable(key, value);
                }
            }

            public void Dispose()
            {
                foreach (var (key, value) in _previous)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }

        /// <summary>A content root whose override file holds the installation's own settings (the password as a file).</summary>
        private sealed class Scratch : IDisposable
        {
            private readonly string? _previousOverridePath;

            public Scratch()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-backup-schedule-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "Data"));
                File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(ContentRoot, "appsettings.json"));
                var passwordFile = Path.Combine(ContentRoot, "host-password");
                File.WriteAllText(passwordFile, Password + "\n");
                PassphraseFile = Path.Combine(ContentRoot, "backup-passphrase");
                File.WriteAllText(PassphraseFile, Passphrase + "\n");

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = "Data Source=|DataDirectory|/root.db;" },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = Root,
                        ["HostEmail"] = GateDrive.HostEmail,
                        ["HostPasswordFile"] = passwordFile
                    }
                };
                File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public string PassphraseFile { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);
                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort - SQLite may still hold a file
                }
            }
        }
    }
}
