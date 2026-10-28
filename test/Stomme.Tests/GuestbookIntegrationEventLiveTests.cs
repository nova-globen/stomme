using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;
using Stomme.Mediator;
using Stomme.Modules.Guestbook.Application;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Stomme.Infrastructure.Tenancy;
using Stomme.Tenancy.Abstractions;
using Stomme.Persistence.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-36(c) — a SHIPPED module publishes a REAL integration event over the bus. Drives the whole
    /// bridge on an installed host: <c>SignGuestbookCommand</c> through the real mediator pipeline →
    /// aggregate persisted → unit of work committed → <see cref="GuestbookEntrySignedIntegrationEvent"/>
    /// published via <c>IEventBus</c> → delivered to a subscriber. Before this, the bus's producer side
    /// was proven only by test-authored events — no stock module ever published one.
    /// </summary>
    /// <remarks>
    /// The consumer is PARAMETERLESS with a static probe-keyed sink — the composed host's handler scan
    /// discovers every <c>IIntegrationEventHandler</c> in loaded Stomme assemblies (this test assembly
    /// included), and a constructor dependency on a test-local type breaks
    /// <c>HostCompositionTests</c>' <c>ValidateOnBuild</c> composition (measured by BL-36(a)).
    /// In-memory transport (the <c>Messaging:Enabled</c> default): the transport leg is BL-36(a)'s
    /// proven ground; this test's subject is the PRODUCER.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class GuestbookIntegrationEventLiveTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public GuestbookIntegrationEventLiveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Signing_the_guestbook_publishes_the_integration_event_over_the_bus()
        {
            using var scratch = new ScratchInstall();

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    // The test plays the CONSUMING module's part: a real consumer subscribes to the topic
                    // in its own startup (AddBusSubscription); the shipped producer is the code under test.
                    builder.Services.AddBusSubscription<GuestbookEntrySignedIntegrationEvent>();
                });

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException("The installer did not finish in time.");
                }
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();

                const int moduleId = 4242;
                Guid entryId;
                // BL-111: a signature carries the tenant it is signed under; a request resolves it from the alias, and
                // this direct send names the host's first tenant the way a job's scope would.
                using var ambient = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(FrameworkTenantId.ToTenantId(1).Value);
                using (var scope = app.Services.CreateScope())
                {
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    var signed = await mediator.Send(
                        new SignGuestbookCommand(moduleId, "Integration Probe", "state-of-the-art"));
                    entryId = signed.Id;
                    _output.WriteLine($"signed entry {entryId} for module {moduleId}");
                }

                var received = await GuestbookIntegrationEventProbe.For(entryId)
                    .WaitAsync(DeliveryTimeout);

                Assert.Equal(entryId, received.EntryId);
                Assert.Equal(moduleId, received.ModuleId);
                Assert.Equal("Integration Probe", received.Author);
                Assert.NotEqual(Guid.Empty, received.EventId);
            }
            finally
            {
                try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
                try { await app.DisposeAsync(); } catch { /* best effort */ }
            }
        }

        /// <summary>A throwaway SQLite install with the bus ENABLED (in-memory transport).</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-bl36c-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = $"Data Source={Path.Combine(ContentRoot, "stomme-bl36c.db")};" },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "H0st!Bus$2026",
                        ["HostEmail"] = "host@example.com",
                        ["HostName"] = "Host"
                    },
                    ["Messaging"] = new JsonObject
                    {
                        ["Enabled"] = true,
                        ["InputQueue"] = "stomme-bl36c-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    }
                };
                System.IO.File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only - BL-104
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Static, probe-id-keyed sink: see the test's remarks for why the consumer is parameterless.</summary>
    public static class GuestbookIntegrationEventProbe
    {
        private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<GuestbookEntrySignedIntegrationEvent>> _waiters = new();

        public static Task<GuestbookEntrySignedIntegrationEvent> For(Guid entryId) =>
            _waiters.GetOrAdd(entryId, _ => new TaskCompletionSource<GuestbookEntrySignedIntegrationEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public static void Record(GuestbookEntrySignedIntegrationEvent @event) =>
            _waiters.GetOrAdd(@event.EntryId, _ => new TaskCompletionSource<GuestbookEntrySignedIntegrationEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(@event);
    }

    /// <summary>Parameterless (host-composition-safe) consumer proving the shipped producer's event arrives.</summary>
    public sealed class GuestbookIntegrationEventProbeConsumer : IIntegrationEventHandler<GuestbookEntrySignedIntegrationEvent>
    {
        public Task HandleAsync(GuestbookEntrySignedIntegrationEvent @event, CancellationToken ct = default)
        {
            GuestbookIntegrationEventProbe.Record(@event);
            return Task.CompletedTask;
        }
    }
}
