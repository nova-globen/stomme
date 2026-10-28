#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Infrastructure;
using Stomme.Repository;
using Stomme.Transport.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The health page's live update (BL-83 / D104): a real hub connection, a real push, on an installed host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What may arrive, and where.</b> The push carries the site's worst state as one word and the instant - nothing
    /// else - and it reaches only the connections of THAT site: the hub puts each connection in a group named by the site
    /// key of the principal that opened it. The hub itself now requires <c>health:view</c> (it was authenticated-only,
    /// with nothing to broadcast); a plain registered user and an anonymous caller are refused at negotiate.
    /// </para>
    /// <para>
    /// <b>Who pushes.</b> "Run probes now" pushes after its run, driven here over HTTP; the job pushes after each site's
    /// pass, driven here through <c>ExecuteJobAsync</c> against a recording port, because the job's own per-minute loop
    /// would otherwise race the assertion.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HealthRealtimePushTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public HealthRealtimePushTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_health_viewer_is_pushed_the_state_word_after_a_run_and_only_for_its_own_site()
        {
            // The job OFF, so its per-minute push cannot arrive ahead of the run's and be mistaken for it.
            var settings = new JsonObject
            {
                ["Transport"] = new JsonObject { ["SignalR"] = new JsonObject { ["Enabled"] = true } },
                ["Health"] = new JsonObject { ["Enabled"] = false },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var alias = await GateDrive.AliasAsync(app, Host);

                var received = new BlockingCollection<JsonElement>();
                await using var hub = Connect(app, cookies);
                hub.On<JsonElement>(HealthHub.StateMethod, message => received.Add(message.Clone()));
                await hub.StartAsync();
                _output.WriteLine($"[hub] connected as host, site key {alias.SiteKey}");

                // ---- run probes now pushes, and the push is the state word and the instant, NOTHING else -------------------
                var run = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Health/run", new { }, _output, "run now");
                Assert.Equal(HttpStatusCode.OK, run.Status);
                string worst;
                using (var parsed = JsonDocument.Parse(run.Body))
                {
                    worst = parsed.RootElement.GetProperty("worst").ToString();
                }

                var pushed = Next(received, "the push after run-now");
                _output.WriteLine($"[hub] received {pushed}");
                Assert.Equal(new[] { "checkedUtc", "state" }, pushed.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
                Assert.Equal(StateWord(worst), pushed.GetProperty("state").GetString());

                // ---- another site's push, and another tenant's, never arrive: ordered delivery on one connection means the
                //      first message after them must be the one addressed to THIS site. ----------------------------------------
                var push = app.Services.GetRequiredService<IHealthRealtimePush>();
                await push.PushAsync(alias.TenantId, alias.SiteId + 1, new HealthRealtimeMessage("Unhealthy", DateTime.UtcNow));
                await push.PushAsync(alias.TenantId + 1, alias.SiteId, new HealthRealtimeMessage("Unhealthy", DateTime.UtcNow));
                await push.PushAsync(alias.TenantId, alias.SiteId, new HealthRealtimeMessage("Degraded", DateTime.UtcNow));
                Assert.Equal("Degraded", Next(received, "this site's push").GetProperty("state").GetString());

                // ---- the hub is health:view, not "any authenticated user" --------------------------------------------------
                await GateDrive.AddUserAsync(app, Host, "health-hub-plain", "Pla1n!HealthHub$2026", "health-hub-plain@gate.test");
                var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, "health-hub-plain", "Pla1n!HealthHub$2026", _output);
                await using (var plainHub = Connect(app, plainCookies))
                {
                    var refused = await Assert.ThrowsAnyAsync<Exception>(() => plainHub.StartAsync());
                    _output.WriteLine($"[hub] plain user refused: {refused.GetType().Name}: {GateDrive.Truncate(refused.Message)}");
                }

                await using (var anonymousHub = Connect(app, cookies: null))
                {
                    await Assert.ThrowsAnyAsync<Exception>(() => anonymousHub.StartAsync());
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_job_pushes_each_site_ONCE_with_its_state_word()
        {
            var recording = new RecordingPush();
            var settings = new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);

            // Registered BEFORE AddStomme composes the host, so it stands in for the SignalR adapter (which is not
            // registered at all here - the transport is off).
            var app = await GateDrive.InstallAsync(scratch, _output, builder => builder.Services.AddSingleton<IHealthRealtimePush>(recording), start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var alias = await GateDrive.AliasAsync(app, Host);

                string log;
                using (var scope = app.Services.CreateScope())
                {
                    await scope.ServiceProvider.GetRequiredService<Stomme.Infrastructure.ITenantManager>().SetAliasAsync(alias);
                    log = await new HealthProbeJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(scope.ServiceProvider);
                }

                _output.WriteLine($"[job] {log}");
                var pushed = Assert.Single(recording.Pushes);
                Assert.Equal((alias.TenantId, alias.SiteId), (pushed.TenantId, pushed.SiteId));
                Assert.True(Enum.TryParse<HealthState>(pushed.Message.State, out _), pushed.Message.State);
                Assert.Equal(DateTimeKind.Utc, pushed.Message.CheckedUtc.Kind);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // The run's JSON carries the enum as a number or a name depending on the serializer's options; the push is always
        // the NAME, which is the one word the page shows.
        private static string StateWord(string worst) =>
            int.TryParse(worst, out var number) ? ((HealthState)number).ToString() : worst;

        private static JsonElement Next(BlockingCollection<JsonElement> received, string what)
        {
            Assert.True(received.TryTake(out var message, TimeSpan.FromSeconds(15)), $"Nothing was pushed within 15 s: {what}.");
            return message;
        }

        private static HubConnection Connect(WebApplication app, System.Net.CookieContainer? cookies)
        {
            var url = new Uri("http://" + Host + HubRoutes.Health);
            return new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                    var header = cookies?.GetCookieHeader(url);
                    if (!string.IsNullOrEmpty(header))
                    {
                        options.Headers["Cookie"] = header;
                    }
                })
                .Build();
        }

        private sealed class RecordingPush : IHealthRealtimePush
        {
            public ConcurrentQueue<(int TenantId, int SiteId, HealthRealtimeMessage Message)> Queue { get; } = new();

            public IReadOnlyList<(int TenantId, int SiteId, HealthRealtimeMessage Message)> Pushes => Queue.ToList();

            public Task PushAsync(int tenantId, int siteId, HealthRealtimeMessage message, CancellationToken cancellationToken = default)
            {
                Queue.Enqueue((tenantId, siteId, message));
                return Task.CompletedTask;
            }
        }
    }
}
