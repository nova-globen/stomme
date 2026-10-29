#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Wait for a call (n8n's Wait node resumed "on webhook call"): Call address gives the run an address once and keeps
    /// its token on the run; Wait for a call parks the run on a bookmark keyed by the token's hash (never the token); a call to
    /// the address resumes it down Called with the body as the result - once; a call that comes before the run reaches its wait
    /// is queued by the engine and delivered when it does; a wait with a time out goes down Timed out when nothing came; a
    /// token that cannot be one is refused before anything is looked up.
    /// </summary>
    public sealed class WaitForCallTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<DueWaitsTask>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IStimulusDispatcher, InlineStimulusDispatcher>(); // the host's dispatcher works in the background
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

        /// <summary>The stimulus sent at once, in the request's scope - what the background dispatcher does a moment later.</summary>
        private sealed class InlineStimulusDispatcher(IStimulusSender sender) : IStimulusDispatcher
        {
            public async Task<DispatchStimulusResponse> SendAsync(DispatchStimulusRequest request, CancellationToken cancellationToken = default)
            {
                await sender.SendAsync(request.StepTypeName!, request.Stimulus!, request.Metadata, cancellationToken);
                return DispatchStimulusResponse.Empty;
            }
        }

        // A webhook starts it; Call address; optionally a Wait first; Wait for a call; each outcome writes a line of its own.
        private static async Task<string> PublishAsync(IServiceProvider services, string hook, WaitForCall wait, string? waitFirst = null)
        {
            var trigger = new Webhook { WebhookName = new(hook), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            var address = new CallAddress { Id = "address" };
            wait.Id = "wait-call";
            var called = new WriteLine("called " + hook) { Id = "called" };
            var timedOut = new WriteLine("timed out " + hook) { Id = "timed-out" };
            var flowchart = new Flowchart { Id = "flow", Steps = { trigger, address, wait, called, timedOut } };
            flowchart.Connections.Add(new Connection(new Endpoint(trigger), new Endpoint(address)));
            if (waitFirst is null)
            {
                flowchart.Connections.Add(new Connection(new Endpoint(address), new Endpoint(wait)));
            }
            else
            {
                var pause = new Wait { Id = "pause", For = new(waitFirst) };
                flowchart.Steps.Add(pause);
                flowchart.Connections.Add(new Connection(new Endpoint(address), new Endpoint(pause)));
                flowchart.Connections.Add(new Connection(new Endpoint(pause), new Endpoint(wait)));
            }

            flowchart.Connections.Add(new Connection(new Endpoint(wait, PipelineCalls.Called), new Endpoint(called)));
            flowchart.Connections.Add(new Connection(new Endpoint(wait, PipelineCalls.TimedOut), new Endpoint(timedOut)));
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(flowchart);
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        private static async Task<PipelineInstance> RunOf(IServiceProvider services, string definitionId) =>
            Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));

        // The token the run keeps (its properties, stored with it) - what the call address ends with.
        private static async Task<string> TokenOf(IServiceProvider services, string definitionId)
        {
            var run = await RunOf(services, definitionId);
            var kept = run.PipelineState.Properties[CallAddress.TokenProperty];
            return kept is System.Text.Json.JsonElement element ? element.GetString()! : (string)kept;
        }

        private static PipelineCallReceiver Receiver(IServiceProvider services) =>
            new(services.GetRequiredService<IStimulusDispatcher>(), NullLogger<PipelineCallReceiver>.Instance);

        private static async Task<string?> ResultOf(IServiceProvider services, string runId)
        {
            var steps = await new ListRunStepsQueryHandler(services.GetRequiredService<IStepExecutionStore>(), services.GetRequiredService<IStepRegistry>(), new Author())
                .Handle(new ListRunStepsQuery(runId), CancellationToken.None);
            return steps.Single(step => step.StepId == "wait-call").OutputsJson;
        }

        private sealed class Author : Stomme.Security.Abstractions.IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        [Fact]
        public async Task A_call_to_the_runs_address_resumes_it_once_with_the_body_as_the_result()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "pay", new WaitForCall());
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("pay"));

            var run = await RunOf(services, definitionId);
            Assert.Equal(PipelineStatus.Running, run.Status);
            var token = await TokenOf(services, definitionId);
            Assert.True(PipelineCalls.IsToken(token));

            // The bookmark keeps the token's hash - never the token.
            var bookmark = Assert.Single(await services.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter { Name = PipelineCalls.WaitStepType }));
            Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(bookmark.Payload));
            Assert.Contains(PipelineCalls.Hash(token), System.Text.Json.JsonSerializer.Serialize(bookmark.Payload));

            // The address is the step's result, and the run's log never shows it.
            var log = await new GetRunLogQueryHandler(services.GetRequiredService<IPipelineExecutionLogStore>()).Handle(new GetRunLogQuery(run.Id), CancellationToken.None);
            Assert.DoesNotContain(log, line => (line.Message ?? "").Contains(token, StringComparison.Ordinal));

            Assert.True(await Receiver(services).ReceiveAsync(token, "{\"paid\":true}", "application/json", CancellationToken.None));
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("called pay", _console.ToString());
            Assert.Contains("\"paid\": true", await ResultOf(services, run.Id)); // the body, shown as the JSON it is
            log = await new GetRunLogQueryHandler(services.GetRequiredService<IPipelineExecutionLogStore>()).Handle(new GetRunLogQuery(run.Id), CancellationToken.None);
            Assert.Contains(log, line => line.EventName == PipelineCalls.Called && line.Message == "Called with 13 characters of application/json.");

            // Once: a second call finds nothing waiting.
            Assert.True(await Receiver(services).ReceiveAsync(token, "again", null, CancellationToken.None));
            Assert.Single(_console.ToString().Split("called pay").Skip(1));
        }

        [Fact]
        public async Task A_call_that_comes_before_the_run_reaches_its_wait_is_delivered_when_it_does()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "early", new WaitForCall(), waitFirst: "1 minute");
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("early"));
            var token = await TokenOf(services, definitionId);
            Assert.Empty(await services.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter { Name = PipelineCalls.WaitStepType }));

            // Called while the run still waits a minute before its wait for the call: the engine queues the call.
            Assert.True(await Receiver(services).ReceiveAsync(token, "early bird", "text/plain", CancellationToken.None));
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, definitionId)).Status);

            // The minute passes; the run reaches its wait, and the queued call is delivered to it.
            _clock.UtcNow = _clock.UtcNow.AddSeconds(61);
            await services.GetRequiredService<DueWaitsTask>().ExecuteAsync(CancellationToken.None);
            await services.GetRequiredService<IBookmarkQueueProcessor>().ProcessAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("called early", _console.ToString());
        }

        [Fact]
        public async Task A_wait_that_times_out_goes_down_Timed_out_and_a_malformed_token_is_refused()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "late", new WaitForCall { TimeOutAfter = new("10 minutes") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("late"));
            var task = services.GetRequiredService<DueWaitsTask>();

            _clock.UtcNow = _clock.UtcNow.AddMinutes(9);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, definitionId)).Status);

            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains("timed out late", _console.ToString());
            Assert.DoesNotContain("called late", _console.ToString());

            Assert.False(await Receiver(services).ReceiveAsync("not-a-token", "x", null, CancellationToken.None));
            Assert.False(await Receiver(services).ReceiveAsync(new string('a', PipelineCalls.TokenLength - 1), "x", null, CancellationToken.None));
            Assert.False(await Receiver(services).ReceiveAsync(new string('/', PipelineCalls.TokenLength), "x", null, CancellationToken.None));
        }

        [Fact]
        public async Task A_wait_for_a_call_with_no_call_address_fails_saying_so()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var trigger = new Webhook { WebhookName = new("lonely"), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            var wait = new WaitForCall { Id = "wait-call" };
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { trigger, wait }, Connections = { new Connection(new Endpoint(trigger), new Endpoint(wait)) } });
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("lonely"));
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOf(services, definition.DefinitionId)).SubStatus);
        }

        [Fact]
        public void Tokens_are_random_url_safe_and_kept_only_as_their_hash()
        {
            var tokens = Enumerable.Range(0, 50).Select(_ => PipelineCalls.NewToken()).ToList();
            Assert.All(tokens, token => Assert.True(PipelineCalls.IsToken(token), token));
            Assert.Equal(50, tokens.Distinct().Count());
            Assert.Equal(64, PipelineCalls.Hash(tokens[0]).Length);
            Assert.NotEqual(PipelineCalls.Hash(tokens[0]), PipelineCalls.Hash(tokens[1]));
        }
    }
}
