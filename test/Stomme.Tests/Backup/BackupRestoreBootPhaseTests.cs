#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Models;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 (D106 item 7, gate <c>BackupRestoreBootPhaseTests</c>) - the engine end to end on an INSTALLED host, across a
    /// real restart (a second host over the same store runs <c>Program.InstallAsync</c>, exactly as <c>Main</c> does before
    /// <c>RunAsync</c>): a staged restore is applied in the boot phase and the site renders; a restore that fails half-way is
    /// rolled back to the SAFETY snapshot; when that fails too the host refuses to serve, and keeps refusing; a pre-flight
    /// that fails - or the wrong passphrase - stages nothing and writes nothing. A setting (the framework part) and a
    /// guestbook entry (a module part) stand for the data.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class BackupRestoreBootPhaseTests
    {
        private const string Host = "localhost";
        private const string Passphrase = "boot phase passphrase";

        private readonly ITestOutputHelper _output;

        protected BackupRestoreBootPhaseTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        private Task<ScratchGateInstall> ScratchAsync() =>
            GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } });

        [Fact]
        public async Task A_staged_restore_is_applied_at_the_next_boot_and_the_site_renders()
        {
            using var scratch = await ScratchAsync();
            Guid snapshot;
            var first = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            // A host that has RUN: its module stores exist (their initializers are hosted services) - the jobs stay off.
            await GateDrive.StartNonJobHostedServicesAsync(first);
            try
            {
                await SetAsync(first, "BeforeTheSnapshot", "kept");
                await SetAsync(first, BackupEngine.SearchWatermarkSetting, DateTime.UtcNow.ToString("O"));
                await SignAsync(first, "before the snapshot");
                snapshot = await Engine(first).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
                await SetAsync(first, "AfterTheSnapshot", "gone after the restore");
                await SignAsync(first, "after the snapshot");

                Assert.True((await Engine(first).PreflightAsync(snapshot, Passphrase)).Ok);
                Assert.False((await Engine(first).PreflightAsync(snapshot, "not the passphrase at all")).Ok);

                var staged = await Engine(first).StageRestoreAsync(snapshot, Passphrase);
                Assert.True(staged.Ok, string.Join("; ", staged.Problems));
                Assert.Equal(BackupSnapshotStatus.Staged, (await Engine(first).GetSnapshotAsync(snapshot))!.Status);
                Assert.True(System.IO.File.Exists(Engine(first).RequestFile));
                Assert.DoesNotContain(Passphrase, await System.IO.File.ReadAllTextAsync(Engine(first).RequestFile), StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            // The restart: the boot phase applies the restore before anything is served.
            var second = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: true);
            try
            {
                Assert.Equal("kept", await GetAsync(second, "BeforeTheSnapshot"));
                Assert.Null(await GetAsync(second, "AfterTheSnapshot"));
                Assert.Equal(new[] { "before the snapshot" }, await EntriesAsync(second));

                // The search index was cleared, so its watermark must go too - or the job would skip everything older.
                Assert.Null(await GetAsync(second, BackupEngine.SearchWatermarkSetting));

                var catalogue = await Engine(second).ListSnapshotsAsync();
                var restored = catalogue.Single(row => row.Id == snapshot);
                Assert.Equal(BackupSnapshotStatus.Available, restored.Status);
                Assert.NotNull(restored.LastRestoredOnUtc);
                Assert.Contains(catalogue, row => row.RetentionTag == RetentionTag.PreRestore && row.Status == BackupSnapshotStatus.Available);
                Assert.False(System.IO.File.Exists(Engine(second).RequestFile));

                using var client = GateDrive.NewClient(second, Host);
                var home = await GateDrive.GetAsync(client, null, Host, "/", _output, "home after the restore");
                Assert.Equal(HttpStatusCode.OK, home.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        [Fact]
        public async Task A_restore_that_fails_half_way_is_rolled_back_to_the_safety_snapshot()
        {
            using var scratch = await ScratchAsync();
            Exploding.Reset(explodeOn: "target");
            Guid snapshot;
            var first = await GateDrive.InstallAsync(scratch, _output, Exploding.Register, start: false);
            // A host that has RUN: its module stores exist (their initializers are hosted services) - the jobs stay off.
            await GateDrive.StartNonJobHostedServicesAsync(first);
            try
            {
                await SetAsync(first, "BeforeTheSnapshot", "kept");
                snapshot = await Engine(first).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);   // Exploding writes "target"
                await SetAsync(first, "AfterTheSnapshot", "back after the rollback");
                Assert.True((await Engine(first).StageRestoreAsync(snapshot, Passphrase)).Ok);          // the safety part says "safety"
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            var second = await GateDrive.InstallAsync(scratch, _output, Exploding.Register, start: false);
            try
            {
                Assert.True(Exploding.Replaced > 0, "the exploding participant was never asked to replace.");
                Assert.Equal("back after the rollback", await GetAsync(second, "AfterTheSnapshot"));
                Assert.Equal("kept", await GetAsync(second, "BeforeTheSnapshot"));
                var target = (await Engine(second).GetSnapshotAsync(snapshot))!;
                Assert.Equal(BackupSnapshotStatus.RestoreFailed, target.Status);
                Assert.Equal(nameof(InvalidOperationException), target.FailureReason);
                Assert.False(System.IO.File.Exists(Engine(second).RequestFile));
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        [Fact]
        public async Task When_the_safety_snapshot_fails_too_the_host_refuses_to_serve_and_keeps_refusing()
        {
            using var scratch = await ScratchAsync();
            Exploding.Reset(explodeOn: "*");
            Guid snapshot;
            var first = await GateDrive.InstallAsync(scratch, _output, Exploding.Register, start: false);
            // A host that has RUN: its module stores exist (their initializers are hosted services) - the jobs stay off.
            await GateDrive.StartNonJobHostedServicesAsync(first);
            try
            {
                snapshot = await Engine(first).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
                Assert.True((await Engine(first).StageRestoreAsync(snapshot, Passphrase)).Ok);
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            foreach (var attempt in new[] { "the restart", "the restart after it" })
            {
                var app = await Stomme.Server.Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder =>
                {
                    builder.WebHost.UseTestServer();
                    Exploding.Register(builder);
                });
                try
                {
                    var install = await Stomme.Server.Program.InstallAsync(app);
                    _output.WriteLine($"[{attempt}] Success={install.Success} Message={install.Message}");
                    Assert.False(install.Success);
                    Assert.Contains("maintenance", install.Message, StringComparison.Ordinal);
                    Assert.Contains(snapshot.ToString(), install.Message, StringComparison.Ordinal);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }
        }

        [Fact]
        public async Task A_failed_preflight_stages_nothing_and_writes_nothing()
        {
            using var scratch = await ScratchAsync();
            Exploding.Reset(explodeOn: "never", invalid: true);
            var app = await GateDrive.InstallAsync(scratch, _output, Exploding.Register, start: false);
            // A host that has RUN: its module stores exist (their initializers are hosted services) - the jobs stay off.
            await GateDrive.StartNonJobHostedServicesAsync(app);
            try
            {
                var snapshot = await Engine(app).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
                await SetAsync(app, "AfterTheSnapshot", "stays");
                var before = await Engine(app).ListSnapshotsAsync();

                var report = await Engine(app).StageRestoreAsync(snapshot, Passphrase);
                Assert.False(report.Ok);
                Assert.Contains(report.Problems, problem => problem.Contains("test:exploding", StringComparison.Ordinal));
                Assert.Equal(0, Exploding.Replaced);

                Assert.False(System.IO.File.Exists(Engine(app).RequestFile));
                Assert.Equal(before.Select(row => (row.Id, row.Status)), (await Engine(app).ListSnapshotsAsync()).Select(row => (row.Id, row.Status)));
                Assert.Equal("stays", await GetAsync(app, "AfterTheSnapshot"));
                await Assert.ThrowsAsync<ArgumentException>(() => Engine(app).CreateSnapshotAsync("too short", RetentionTag.Manual));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_snapshot_and_a_host_with_different_participants_are_refused_both_ways()
        {
            using var scratch = await ScratchAsync();
            Exploding.Reset(explodeOn: "never");
            Guid withoutPart;
            Guid withPart;
            var plain = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(plain);
            try
            {
                withoutPart = await Engine(plain).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
            }
            finally
            {
                await GateDrive.ShutdownAsync(plain);
            }

            var planted = await GateDrive.InstallAsync(scratch, _output, Exploding.Register, start: false);
            try
            {
                var missingPart = await Engine(planted).StageRestoreAsync(withoutPart, Passphrase);
                Assert.Contains(missingPart.Problems, problem => problem.Contains("added to this host after", StringComparison.Ordinal));
                withPart = await Engine(planted).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
            }
            finally
            {
                await GateDrive.ShutdownAsync(planted);
            }

            var again = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var missingParticipant = await Engine(again).StageRestoreAsync(withPart, Passphrase);
                Assert.Contains(missingParticipant.Problems, problem => problem.Contains("has no participant on this host", StringComparison.Ordinal));
                Assert.False(System.IO.File.Exists(Engine(again).RequestFile));
            }
            finally
            {
                await GateDrive.ShutdownAsync(again);
            }
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private static Task SetAsync(WebApplication app, string name, string value) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = name, SettingValue = value, IsPrivate = false });
                return 0;
            });

        private static Task<string?> GetAsync(WebApplication app, string name) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) => (await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Site, alias.SiteId, name))?.SettingValue);

        private static Task SignAsync(WebApplication app, string message) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().AddAsync(GuestbookEntryAggregate.Sign(sp.GetRequiredService<ITenantContext>().CurrentTenantId!, 3, "Ada", message));
                return 0;
            });

        private static Task<string[]> EntriesAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().GetAllAsync()).Select(entry => entry.Message).OrderBy(m => m, StringComparer.Ordinal).ToArray());

        /// <summary>
        /// A planted participant: its part says which snapshot it was (the first export "target", later ones "safety"), and it
        /// throws on replace when that part is the one it was told to explode on - LAST in restore order, so everything else
        /// has already been replaced when it throws. Static: both hosts of a test live in this process.
        /// </summary>
        internal sealed class Exploding : IBackupParticipant
        {
            private static int _exports;
            private static string _explodeOn = "never";
            private static bool _invalid;

            public static int Replaced { get; private set; }

            public static void Reset(string explodeOn, bool invalid = false)
            {
                _exports = 0;
                _explodeOn = explodeOn;
                _invalid = invalid;
                Replaced = 0;
            }

            public static void Register(WebApplicationBuilder builder) => builder.Services.AddScoped<IBackupParticipant, Exploding>();

            public string Name => "test:exploding";

            public string SchemaVersion => "1";

            public int RestoreOrder => 900;

            public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken) =>
                await writer.Content.WriteAsync(Encoding.UTF8.GetBytes(Interlocked.Increment(ref _exports) == 1 ? "target" : "safety"), cancellationToken);

            public Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken) =>
                Task.FromResult(_invalid ? BackupPartValidation.Invalid("planted: this part is refused") : BackupPartValidation.Valid);

            public async Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
            {
                Replaced++;
                using var content = new StreamReader(reader.OpenRead());
                var which = await content.ReadToEndAsync(cancellationToken);
                if (_explodeOn == "*" || which == _explodeOn)
                {
                    throw new InvalidOperationException("planted: this replace fails half-way");
                }
            }
        }
    }

    public sealed class SqliteBackupRestoreBootPhaseTests(ITestOutputHelper output) : BackupRestoreBootPhaseTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresBackupRestoreBootPhaseTests(ITestOutputHelper output) : BackupRestoreBootPhaseTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenBackupRestoreBootPhaseTests(ITestOutputHelper output) : BackupRestoreBootPhaseTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }
}
