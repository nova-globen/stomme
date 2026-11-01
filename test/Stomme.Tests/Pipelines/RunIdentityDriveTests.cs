#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Management.Extensions;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Security.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A published run acts as a user (BL-280, the owner's answer, 2026-10-30): started by hand, as the person who started it.
    /// Driven on an installed SQLite host: a step enters its site and sends a request marked <c>[Requires(pipelines:manage)]</c>
    /// whose handler reports who it ran for; the Host publishes the pipeline and starts it, and the run - on the engine's
    /// worker, with no request behind it - is allowed and reports the Host. Before BL-280 the guard denied every request in a
    /// run ("Access denied"), whatever the step's test had shown.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class RunIdentityDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_run_acts_as_its_starter_or_else_its_publisher_and_passes_a_requires_check()
        {
            Action<WebApplicationBuilder> configure = builder =>
            {
                builder.Services.AddStep<RunUserProbe>();
                builder.Services.AddScoped<IRequestHandler<RunUserProbeQuery, string>, RunUserProbeHandler>();
            };
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output, configure);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var created = await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Who am I" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new Stomme.Models.Pipelines.PipelineGraph { Steps = { new PipelineGraphStep { Id = "probe", Type = "DriveTest.RunUserProbe", Name = "Probe" } } };
                Assert.Equal(HttpStatusCode.OK, (await Send(app, client, cookies, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph")).Status);
                Assert.Equal(HttpStatusCode.OK, (await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish")).Status);
                var started = await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "start");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var log = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body, Json)!;
                foreach (var line in log)
                {
                    output.WriteLine($"[log] {line.StepType} {line.EventName} {line.Message}");
                }

                Assert.True(run?.IsFinished, $"the run did not finish: {run?.Status} {run?.SubStatus}");
                Assert.Contains(log, line => line.StepType == "DriveTest.RunUserProbe" && line.EventName == "ActedAs" && line.Message == hostId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.DoesNotContain(log, line => line.StepType == "DriveTest.RunUserProbe" && line.EventName == "Refused");

                // The run's page says whom it acted as.
                Assert.Equal(GateDrive.HostUsername, run!.ActsAs);

                // A run no person started - dispatched as a trigger dispatches, from a scope with no request - acts as the
                // version's publisher.
                var triggeredId = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var bridge = sp.GetRequiredService<Stomme.Modules.Pipelines.Tenancy.IEngineTenantBridge>();
                    using var engineTenant = bridge.Enter((await bridge.ResolveCurrentSiteAsync())!);
                    var published = await sp.GetRequiredService<Stomme.Engine.Pipelines.Management.IPipelineDefinitionStore>()
                        .FindAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter { DefinitionId = definitionId, VersionOptions = Stomme.Engine.Common.Models.VersionOptions.Published });
                    var runIdentity = Guid.NewGuid().ToString("N");
                    var dispatched = await sp.GetRequiredService<Stomme.Engine.Pipelines.Runtime.IPipelineDispatcher>()
                        .DispatchAsync(new Stomme.Engine.Pipelines.Runtime.Requests.DispatchPipelineDefinitionRequest(published!.Id) { InstanceId = runIdentity }, options: null);
                    Assert.Null(dispatched.Fault);
                    return runIdentity;
                });
                PipelineRunView? triggered = null;
                for (var attempt = 0; attempt < 150 && triggered?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{triggeredId}", output, "the triggered run");
                    triggered = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var triggeredLog = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{triggeredId}/log", output, "the triggered run's log")).Body, Json)!;
                Assert.True(triggered?.IsFinished, $"the triggered run did not finish: {triggered?.Status} {triggered?.SubStatus}");
                Assert.Contains(triggeredLog, line => line.StepType == "DriveTest.RunUserProbe" && line.EventName == "ActedAs" && line.Message == hostId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(GateDrive.HostUsername, triggered!.ActsAs);            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<(HttpStatusCode Status, string Body)> Send(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, HttpMethod method, string path, object body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);
    }

    /// <summary>A request only a pipeline author may send; its answer is the user it ran for.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record RunUserProbeQuery : IQuery<string>;

    public sealed class RunUserProbeHandler(ICurrentUser currentUser) : IQueryHandler<RunUserProbeQuery, string>
    {
        public Task<string> Handle(RunUserProbeQuery request, CancellationToken ct) =>
            Task.FromResult(currentUser.UserId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nobody");
    }

    /// <summary>Enters its site, as a step that writes module data does, then sends the probe; logs who it acted as, or the refusal.</summary>
    [Step("DriveTest", "Tests", "Reports which user a run acts as.", DisplayName = "Run user probe")]
    public class RunUserProbe : CodeStep
    {
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            await StommeSiteScope.EnsureSiteAsync(context);
            try
            {
                var answer = await context.GetRequiredService<IMediator>().Send(new RunUserProbeQuery(), context.CancellationToken);
                context.AddExecutionLogEntry("ActedAs", answer);
            }
            catch (Exception ex)
            {
                context.AddExecutionLogEntry("Refused", ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
