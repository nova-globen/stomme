#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Features;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The "Pipeline failed" trigger (n8n's error workflow) through the engine as the host composes it: a published
    /// pipeline starting with it is indexed under the pipeline it watches, or every pipeline; a FAILED run of a stored pipeline
    /// starts the ones watching it, with the failure as the trigger's result; a run that succeeds, a run of no stored pipeline,
    /// and a failed run of a pipeline that itself starts this way start nothing - so a failure handler that fails cannot loop.
    /// </summary>
    public sealed class PipelineFailureTriggerTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly Collected _collected = new();
        private readonly ConcurrentQueue<string> _handlerErrors = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            // What the failure handler logs as an error - a failure it swallows so the failed run is unaffected - is kept, and asserted empty.
            services.AddLogging(logging => logging.AddProvider(new HandlerErrors(_handlerErrors)));
            services.AddSingleton(_collected);
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(TextWriter.Null));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<Boom>();
                module.AddStep<Collect>();
                // The host dispatches the stimulus to a background worker; here it is sent at once, so a test sees its runs.
                module.Configure<PipelineRuntimeFeature>(runtime => runtime.StimulusDispatcher = provider => new InlineDispatcher(provider.GetRequiredService<IServiceScopeFactory>()));
                // A run's page reads the data the run saved from the key-value store the host always composes.
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddNotificationHandler<StartPipelinesOnFailure, PipelineFinished>();
            services.AddNotificationHandler<KeepRunInput, PipelineStarted>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class HandlerErrors(ConcurrentQueue<string> errors) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => categoryName == typeof(StartPipelinesOnFailure).FullName ? new Logger(errors) : NullLogger.Instance;

            public void Dispose()
            {
            }

            private sealed class Logger(ConcurrentQueue<string> errors) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    if (logLevel >= LogLevel.Error)
                    {
                        errors.Enqueue(formatter(state, exception));
                    }
                }
            }
        }

        private sealed class InlineDispatcher(IServiceScopeFactory scopes) : IStimulusDispatcher
        {
            public async Task<DispatchStimulusResponse> SendAsync(DispatchStimulusRequest request, CancellationToken cancellationToken = default)
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IStimulusSender>().SendAsync(request.StepTypeName!, request.Stimulus!, request.Metadata, cancellationToken);
                return DispatchStimulusResponse.Empty;
            }
        }

        public sealed class Collected
        {
            public ConcurrentQueue<(string Tag, string? Value)> Items { get; } = new();
        }

        [Step("Stomme.Tests", "Tests", "Fails, always.")]
        public sealed class Boom : CodeStep
        {
            protected override void Execute(StepExecutionContext context) => throw new InvalidOperationException("the report could not be sent");
        }

        /// <summary>Keeps the last result under its tag.</summary>
        [Step("Stomme.Tests", "Tests", "Collects the last result.")]
        public sealed class Collect : CodeStep
        {
            public string? Tag { get; set; }

            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<Collected>().Items.Enqueue((Tag ?? "", context.GetLastResult()?.ToString()));
        }

        private static Flowchart Chain(IStep trigger, params IStep[] next)
        {
            trigger.Id = "trigger";
            trigger.SetCanStartPipeline(true);
            var chart = new Flowchart { Steps = { trigger } };
            var previous = trigger;
            for (var i = 0; i < next.Length; i++)
            {
                next[i].Id = "step-" + i;
                next[i].Name = "Step " + i;
                chart.Steps.Add(next[i]);
                chart.Connections.Add(new Connection(new Endpoint(previous), new Endpoint(next[i])));
                previous = next[i];
            }

            return chart;
        }

        private static async Task<string> PublishAsync(IServiceProvider services, string name, IStep root)
        {
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(root);
            definition.Name = name;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private static async Task<long> RunsAsync(IServiceProvider services, string definitionId) =>
            await services.GetRequiredService<IPipelineInstanceStore>().CountAsync(new PipelineInstanceFilter { DefinitionId = definitionId });

        private string[] Tags() => _collected.Items.Select(item => item.Tag).OrderBy(tag => tag, StringComparer.Ordinal).ToArray();

        [Fact]
        public async Task A_failed_run_starts_the_pipelines_watching_it_with_what_failed()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var report = await PublishAsync(services, "Nightly report", Chain(new Webhook { WebhookName = new("report") }, new Boom()));
            var other = await PublishAsync(services, "Something else", Chain(new Webhook { WebhookName = new("other") }, new WriteLine("fine")));
            var alert = await PublishAsync(services, "Alert on the report", Chain(new PipelineFailed { Pipeline = new(report) }, new Collect { Tag = "watching the report" }));
            await PublishAsync(services, "Alert on anything", Chain(new PipelineFailed(), new Collect { Tag = "watching every pipeline" }));
            await PublishAsync(services, "Alert on the other", Chain(new PipelineFailed { Pipeline = new(other) }, new Collect { Tag = "watching the other" }));

            // Indexed under what each watches: the report's id, every pipeline, the other's id.
            var watched = (await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = PipelineFailure.StepType }))
                .Select(trigger => Assert.IsType<PipelineFailedStimulus>(trigger.Payload).Pipeline).OrderBy(id => id, StringComparer.Ordinal);
            Assert.Equal(new[] { report, other, PipelineFailure.AnyPipeline }.OrderBy(id => id, StringComparer.Ordinal), watched);

            // The other succeeds: nothing starts.
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("other"));
            Assert.Empty(_collected.Items);

            // The report fails: the two watching it start, the one watching the other does not.
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("report"));
            Assert.Equal(new[] { "watching every pipeline", "watching the report" }, Tags());

            var failed = Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = report }));
            Assert.Equal(PipelineSubStatus.Faulted, failed.SubStatus);
            using var failure = JsonDocument.Parse(_collected.Items.First().Value!);
            var root = failure.RootElement;
            Assert.Equal("Nightly report", root.GetProperty("pipeline").GetString());
            Assert.Equal(report, root.GetProperty("pipelineId").GetString());
            Assert.Equal(failed.Id, root.GetProperty("run").GetString());
            Assert.Equal("Step 0", root.GetProperty("step").GetString());
            Assert.Equal("step-0", root.GetProperty("stepId").GetString());
            Assert.Equal("Stomme.Tests.Boom", root.GetProperty("stepType").GetString());
            Assert.Contains("the report could not be sent", root.GetProperty("error").GetString());
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("failedAt").GetString(), out _));
            Assert.Empty(_handlerErrors);

            // The handler's run, as its page reads it: whose run it is and the run whose failure started it - for anyone who may
            // view runs; what it was started with - the failure, data - for an author only.
            var started = Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = alert }));
            GetRunQueryHandler Reader(bool author) => new(services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDefinitionStore>(), new Guard(author),
                services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>());
            var viewed = await Reader(author: false).Handle(new GetRunQuery(started.Id), CancellationToken.None);
            Assert.Equal(("Alert on the report", failed.Id, "Nightly report"), (viewed!.PipelineName, viewed.FailedRunId, viewed.FailedPipelineName));
            Assert.Null(viewed.InputJson);
            // The failure is JSON text in the input; it is shown as the JSON it is, not as an escaped string.
            var input = (await Reader(author: true).Handle(new GetRunQuery(started.Id), CancellationToken.None))!.InputJson;
            Assert.Contains("\"pipeline\": \"Nightly report\"", input);
            Assert.Contains("the report could not be sent", input);
            Assert.DoesNotContain("\\\"", input);
            Assert.Null((await Reader(author: true).Handle(new GetRunQuery(failed.Id), CancellationToken.None))!.FailedRunId);
        }

        private sealed class Guard(bool author) : Stomme.Security.Abstractions.IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission == Stomme.Pipelines.PipelinesPermissions.Manage ? author : true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        [Fact]
        public async Task A_failure_handler_that_fails_starts_nothing_and_a_run_of_no_stored_pipeline_starts_nothing()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var report = await PublishAsync(services, "Nightly report", Chain(new Webhook { WebhookName = new("report") }, new Boom()));
            var failing = await PublishAsync(services, "A handler that fails", Chain(new PipelineFailed(), new Collect { Tag = "failing handler" }, new Boom()));
            await PublishAsync(services, "Alert on anything", Chain(new PipelineFailed(), new Collect { Tag = "watching every pipeline" }));

            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("report"));

            // Each handler ran ONCE, for the report: the failing handler's own failure started nobody, itself included.
            Assert.Equal(new[] { "failing handler", "watching every pipeline" }, Tags());
            Assert.Equal(1, await RunsAsync(services, failing));
            Assert.Equal(PipelineSubStatus.Faulted, Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = failing })).SubStatus);

            // A run of no stored pipeline - what a step test is - fails without starting anyone.
            var adHoc = await services.GetRequiredService<IPipelineRunner>().RunAsync(new Flowchart { Steps = { new Boom { Id = "alone" } } });
            Assert.Equal(PipelineSubStatus.Faulted, adHoc.PipelineState.SubStatus);
            Assert.Equal(2, _collected.Items.Count);
            Assert.Equal(1, await RunsAsync(services, report));
            Assert.Empty(_handlerErrors);
        }

        [Fact]
        public void The_failure_is_one_shape_for_a_run_and_for_the_editors_sample()
        {
            var at = new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.FromHours(2));
            using var written = JsonDocument.Parse(PipelineFailure.Write("def-1", null, "run-1", "send", null, "Stomme.SendHttpRequest", "503", at));
            Assert.Equal("def-1", written.RootElement.GetProperty("pipeline").GetString()); // no name: the id
            Assert.Equal("send", written.RootElement.GetProperty("step").GetString()); // no step name: its id
            Assert.Equal("2026-10-06T05:00:00.0000000Z", written.RootElement.GetProperty("failedAt").GetString());

            using var sample = JsonDocument.Parse(PipelineFailure.Sample("Nightly report", at));
            Assert.Equal(
                written.RootElement.EnumerateObject().Select(property => property.Name),
                sample.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("Nightly report", sample.RootElement.GetProperty("pipeline").GetString());

            Assert.Equal(PipelineFailure.AnyPipeline, PipelineFailure.Watched("  "));
            Assert.Equal("def-1", PipelineFailure.Watched(" def-1 "));
        }
    }
}
