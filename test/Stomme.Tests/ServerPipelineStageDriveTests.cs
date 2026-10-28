using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;
using PipelineHooks = Stomme.Extensions.ApplicationBuilderExtensions;

namespace Stomme.Tests
{
    /// <summary>
    /// The opt-in pipeline hooks (<see cref="IServerPipelineStartup"/>): an app or module puts middleware EARLIER than
    /// <see cref="IServerStartup.Configure"/> - before the static files, before tenant resolution, before the site-JWT
    /// middleware, before routing. Driven on a real installed host: each stage's middleware runs where it says, a
    /// BeforeTenantResolution hook answers a legacy URL with a 301 before the host resolves a site for it, and a BeforeJwt
    /// hook that moves <c>?access_token=</c> into <c>Authorization</c> is what lets the JWT middleware sign the request in
    /// (the same move one stage later does not).
    /// </summary>
    /// <remarks>
    /// THE TRAP this file is built around: the probe hooks below live in Stomme.Tests, an assembly whose name makes the
    /// host's scan discover them in EVERY host this test run composes. Each one therefore does nothing unless the host it
    /// is configuring holds a <see cref="PipelineStageProbe"/> singleton, which only this class registers (through
    /// <see cref="GateDrive.InstallAsync(ScratchGateInstall, ITestOutputHelper, Action{WebApplicationBuilder})"/>'s
    /// builder hook). The switch is per HOST, not per process, so a host composed in parallel by another class cannot
    /// see it.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ServerPipelineStageDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        internal const string TraceHeader = "X-Stage-Trace";
        internal const string ProbeHeader = "X-Stage-Probe";
        internal const string LegacyPath = "/legacy-stage-probe";
        internal const string LegacyTarget = "/";

        [Fact]
        public void Hooks_run_by_stage_then_order_then_type_name_never_by_discovery_order()
        {
            var hooks = new IServerPipelineStartup[]
            {
                new FakeHookB(ServerPipelineStage.BeforeRouting, 0),
                new FakeHookA(ServerPipelineStage.BeforeJwt, 5),
                new FakeHookB(ServerPipelineStage.BeforeJwt, 0),
                new FakeHookA(ServerPipelineStage.BeforeJwt, 0),
                new FakeHookA(ServerPipelineStage.BeforeStaticFiles, 9),
            };

            var ordered = PipelineHooks.OrderPipelineStartups(hooks)
                .Select(hook => $"{hook.Stage}:{hook.Order}:{hook.GetType().Name}").ToList();

            Assert.Equal(new[]
            {
                "BeforeStaticFiles:9:FakeHookA",
                "BeforeJwt:0:FakeHookA",
                "BeforeJwt:0:FakeHookB",
                "BeforeJwt:5:FakeHookA",
                "BeforeRouting:0:FakeHookB",
            }, ordered);

            // The same answer whatever order the scan found them in.
            Assert.Equal(ordered, PipelineHooks.OrderPipelineStartups(hooks.Reverse())
                .Select(hook => $"{hook.Stage}:{hook.Order}:{hook.GetType().Name}").ToList());
        }

        [Fact]
        public void A_stage_calls_only_its_own_hooks()
        {
            var called = new List<string>();
            var hooks = new IServerPipelineStartup[]
            {
                new FakeHookA(ServerPipelineStage.BeforeJwt, 0, called),
                new FakeHookB(ServerPipelineStage.BeforeRouting, 0, called),
            };
            var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());

            app.UseServerPipelineStage(ServerPipelineStage.BeforeJwt, hooks, env: null!);

            Assert.Equal(new[] { "FakeHookA" }, called);
        }

        [Fact]
        public async Task A_host_without_the_probe_gets_no_middleware_from_the_test_hooks()
        {
            // The discovery scan DOES find the probe hooks (they are in a Stomme assembly); what keeps every other host of
            // the run unaffected is that each adds nothing without the probe service.
            var discovered = PipelineHooks.DiscoverPipelineStartups();
            var probes = discovered.Where(hook => hook.GetType().DeclaringType == typeof(ProbeHooks)).ToList();
            Assert.True(probes.Count >= 6, $"the scan found {probes.Count} probe hook(s); expected at least 6");

            var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
            foreach (var hook in probes)
            {
                hook.Configure(app, env: null!);
            }

            var context = new DefaultHttpContext();
            context.Request.Path = LegacyPath;
            context.Request.Headers[ProbeHeader] = "1";
            var reachedEnd = false;
            app.Run(_ => { reachedEnd = true; return Task.CompletedTask; });
            await app.Build()(context);

            Assert.True(reachedEnd, "a probe hook acted on a host that holds no PipelineStageProbe");
            Assert.False(context.Response.Headers.ContainsKey(TraceHeader));
        }

        [Fact]
        public async Task Each_stage_runs_at_its_point_in_a_real_installed_host()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<PipelineStageProbe>());
            try
            {
                // ---- a page: every stage runs, in pipeline order, and the tenant appears exactly between the second and third
                using (var client = GateDrive.NewClient(app, Host))
                {
                    var trace = await TraceAsync(client, "/", "page");
                    Assert.Equal(
                        "BeforeStaticFiles(alias=False)|BeforeTenantResolution(alias=False)|BeforeJwt(alias=True)|BeforeRouting(alias=True)",
                        trace.Trace);
                }

                // ---- a static file: only the stage before the static files sees it (the file answers before the rest)
                // (the scratch host's web root is its own, so the probe writes the file it asks for)
                Assert.False(string.IsNullOrEmpty(app.Environment.WebRootPath), "the installed host has no web root");
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(app.Environment.WebRootPath, "stage-probe.txt"), "probe");
                using (var client = GateDrive.NewClient(app, Host))
                {
                    var trace = await TraceAsync(client, "/stage-probe.txt", "static file");
                    Assert.Equal(HttpStatusCode.OK, trace.Status);
                    Assert.Equal("BeforeStaticFiles(alias=False)", trace.Trace);
                }

                // ---- a legacy URL: a BeforeTenantResolution hook answers 301, and nothing after it runs
                using (var client = GateDrive.NewClient(app, Host))
                {
                    var trace = await TraceAsync(client, LegacyPath, "legacy redirect");
                    Assert.Equal(HttpStatusCode.MovedPermanently, trace.Status);
                    Assert.Equal(LegacyTarget, trace.Location);
                    Assert.Equal("BeforeStaticFiles(alias=False)|BeforeTenantResolution(alias=False)", trace.Trace);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_BeforeJwt_hook_that_moves_the_query_token_into_the_header_signs_the_request_in_and_one_stage_later_does_not()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<PipelineStageProbe>());
            try
            {
                const string secret = "pipeline-stage-drive-secret-0123456789abcdef";
                var userId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var token = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting
                    {
                        EntityName = EntityNames.Site,
                        EntityId = alias.SiteId,
                        SettingName = "JwtOptions:Secret",
                        SettingValue = secret,
                        IsPrivate = true
                    });
                    var user = await sp.GetRequiredService<IUserManager>().GetUserAsync(userId, alias.SiteId);
                    return sp.GetRequiredService<IJwtManager>().GenerateToken(alias, UserSecurity.CreateClaimsIdentity(alias, user), secret, "", "", 20);
                });
                Assert.False(string.IsNullOrEmpty(token));

                // Control: the token itself is good - sent as a header, the JWT middleware signs it in.
                Assert.Equal((true, GateDrive.HostUsername), await AuthenticateAsync(app, "/api/user/authenticate", token, "header"));

                // Control: nothing sent, nobody signed in.
                Assert.Equal((false, ""), await AuthenticateAsync(app, "/api/user/authenticate", null, "anonymous"));

                // The hook at BeforeJwt moves ?access_token= into Authorization; the JWT middleware then reads it.
                Assert.Equal((true, GateDrive.HostUsername), await AuthenticateAsync(app, "/api/user/authenticate?access_token=" + Uri.EscapeDataString(token), null, "?access_token= (BeforeJwt)"));

                // The SAME move at BeforeRouting - after the JWT middleware - is too late: the request stays anonymous.
                Assert.Equal((false, ""), await AuthenticateAsync(app, "/api/user/authenticate?late_token=" + Uri.EscapeDataString(token), null, "?late_token= (BeforeRouting)"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<(HttpStatusCode Status, string Trace, string Location)> TraceAsync(HttpClient client, string path, string label)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(ProbeHeader, "1");
            var response = await client.SendAsync(request);
            var trace = response.Headers.TryGetValues(TraceHeader, out var values) ? string.Join(",", values) : "";
            var location = response.Headers.Location?.OriginalString ?? "";
            output.WriteLine($"[{label}] GET {path} -> {(int)response.StatusCode} {TraceHeader}: {trace} Location: {location}");
            return (response.StatusCode, trace, location);
        }

        private async Task<(bool IsAuthenticated, string Username)> AuthenticateAsync(WebApplication app, string path, string? bearer, string label)
        {
            using var client = GateDrive.NewClient(app, Host);
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (bearer != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            }
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"[{label}] GET {path.Split('?')[0]} -> {(int)response.StatusCode} {GateDrive.Truncate(body)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            return (root.GetProperty("isAuthenticated").GetBoolean(), root.GetProperty("username").GetString() ?? "");
        }

        // The host's scan finds the fakes too (Stomme.Tests is a Stomme assembly) and creates them with Activator, so each
        // needs a parameterless constructor; created that way it records nothing and adds no middleware.
        private sealed class FakeHookA(ServerPipelineStage stage, int order, List<string>? called = null) : IServerPipelineStartup
        {
            public FakeHookA() : this(ServerPipelineStage.BeforeRouting, 0) { }
            public ServerPipelineStage Stage => stage;
            public int Order => order;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => called?.Add(nameof(FakeHookA));
        }

        private sealed class FakeHookB(ServerPipelineStage stage, int order, List<string>? called = null) : IServerPipelineStartup
        {
            public FakeHookB() : this(ServerPipelineStage.BeforeRouting, 0) { }
            public ServerPipelineStage Stage => stage;
            public int Order => order;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => called?.Add(nameof(FakeHookB));
        }
    }

    /// <summary>The per-host switch: only a host that registers this singleton gets the probe hooks' middleware.</summary>
    public sealed class PipelineStageProbe
    {
    }

    /// <summary>
    /// The probe hooks the host's scan discovers. NESTED in this class so a test can find exactly them; each is a
    /// no-op unless its host holds a <see cref="PipelineStageProbe"/>.
    /// </summary>
    public static class ProbeHooks
    {
        private static bool Probed(IApplicationBuilder app) => app.ApplicationServices.GetService<PipelineStageProbe>() != null;

        /// <summary>Appends "Stage(alias=...)" to the trace header on a request that asks for a trace.</summary>
        private static void Record(IApplicationBuilder app, ServerPipelineStage stage)
        {
            if (!Probed(app))
            {
                return;
            }

            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey(ServerPipelineStageDriveTests.ProbeHeader))
                {
                    var entry = $"{stage}(alias={context.Items.ContainsKey(Constants.HttpContextAliasKey)})";
                    var existing = context.Response.Headers[ServerPipelineStageDriveTests.TraceHeader].ToString();
                    context.Response.Headers[ServerPipelineStageDriveTests.TraceHeader] = string.IsNullOrEmpty(existing) ? entry : existing + "|" + entry;
                }
                await next();
            });
        }

        /// <summary>Moves the named query value into the Authorization header, as a media module does for &lt;video&gt;.</summary>
        private static void MoveQueryToken(IApplicationBuilder app, string queryName)
        {
            if (!Probed(app))
            {
                return;
            }

            app.Use(async (context, next) =>
            {
                var token = context.Request.Query[queryName].ToString();
                if (!string.IsNullOrEmpty(token) && !context.Request.Headers.ContainsKey("Authorization"))
                {
                    context.Request.Headers["Authorization"] = "Bearer " + token;
                }
                await next();
            });
        }

        public sealed class RecordBeforeStaticFiles : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeStaticFiles;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => Record(app, Stage);
        }

        public sealed class RecordBeforeTenantResolution : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeTenantResolution;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => Record(app, Stage);
        }

        /// <summary>Order 1: runs after the recorder of its stage, so the trace shows the stage before the redirect ends it.</summary>
        public sealed class LegacyRedirect : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeTenantResolution;
            public int Order => 1;

            public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
            {
                if (!Probed(app))
                {
                    return;
                }

                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.Equals(ServerPipelineStageDriveTests.LegacyPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                        context.Response.Headers.Location = ServerPipelineStageDriveTests.LegacyTarget;
                        return;
                    }
                    await next();
                });
            }
        }

        public sealed class RecordBeforeJwt : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeJwt;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => Record(app, Stage);
        }

        public sealed class AccessTokenBeforeJwt : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeJwt;
            public int Order => 1;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => MoveQueryToken(app, "access_token");
        }

        public sealed class RecordBeforeRouting : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeRouting;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => Record(app, Stage);
        }

        public sealed class LateTokenBeforeRouting : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeRouting;
            public int Order => 1;
            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) => MoveQueryToken(app, "late_token");
        }
    }
}
