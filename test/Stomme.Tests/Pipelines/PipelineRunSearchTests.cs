#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.State;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's execution search: the runs list finds a run by its id, or the runs of a correlation id, through the engine's
    /// own search filter (held to the memory store on EF and RavenDB by the engine-store conformance suite); a search past its
    /// length is refused rather than cut.
    /// </summary>
    public sealed class PipelineRunSearchTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private static PipelineInstance Run(string id, string correlationId, int minute) => new()
        {
            Id = id,
            DefinitionId = "def-orders",
            DefinitionVersionId = "def-orders-1",
            Version = 1,
            Status = PipelineStatus.Finished,
            SubStatus = PipelineSubStatus.Finished,
            Name = "Orders",
            CorrelationId = correlationId,
            CreatedAt = new DateTimeOffset(2026, 10, 6, 7, minute, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 10, 6, 7, minute, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 10, 6, 7, minute, 1, TimeSpan.Zero),
            PipelineState = new PipelineState { Id = id, DefinitionId = "def-orders" },
        };

        [Fact]
        public async Task A_run_is_found_by_its_id_or_by_its_correlation_id()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            await instances.AddAsync(Run("run-aaa", "order-7", 1));
            await instances.AddAsync(Run("run-bbb", "order-8", 2));
            await instances.AddAsync(Run("run-ccc", "order-7", 3));
            var handler = Handler(services);
            async Task<string[]> Found(string? search) =>
                (await handler.Handle(new ListRunsQuery(Search: search), CancellationToken.None)).Select(run => run.Id).ToArray();

            Assert.Equal(new[] { "run-ccc", "run-aaa" }, await Found("order-7")); // newest first
            Assert.Equal(new[] { "run-bbb" }, await Found("  run-bbb "));
            Assert.Equal(3, (await Found(null)).Length);
            Assert.Empty(await Found("order-9"));
            await Assert.ThrowsAsync<ArgumentException>(() => Found(new string('x', ListRunsQueryHandler.MaxSearchLength + 1)));
        }

        private static ListRunsQueryHandler Handler(IServiceProvider services) => new(
            services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDefinitionStore>(),
            services.GetRequiredService<IKeyValueStore>(), services.GetRequiredService<ITenantAccessor>());

        // BL-229 - n8n's Execution Data: what a run saved about itself is shown with it and found by the runs search - "key: value"
        // for that key holding that value, or a value alone - beside the id and correlation id the store searches; newest first.
        [Fact]
        public async Task A_run_is_found_by_the_data_it_saved_and_listed_with_it()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            var tenant = services.GetRequiredService<ITenantAccessor>().TenantId;
            await instances.AddAsync(Run("run-aaa", "c-1", 1));
            await instances.AddAsync(Run("run-bbb", "c-2", 2));
            await instances.AddAsync(Run("run-42", "c-3", 3));
            await PipelineRunDataStore.MergeAsync(keyValues, tenant, "run-aaa", new Dictionary<string, string> { ["order"] = "42", ["customer"] = "Ada" }, CancellationToken.None);
            await PipelineRunDataStore.MergeAsync(keyValues, tenant, "run-bbb", new Dictionary<string, string> { ["order"] = "43" }, CancellationToken.None);
            await PipelineRunDataStore.MergeAsync(keyValues, tenant, "run-bbb", new Dictionary<string, string> { ["customer"] = "ada" }, CancellationToken.None);
            var handler = Handler(services);
            async Task<string[]> Found(string? search) =>
                (await handler.Handle(new ListRunsQuery(Search: search), CancellationToken.None)).Select(run => run.Id).ToArray();

            Assert.Equal(new[] { "run-aaa" }, await Found("order: 42"));
            Assert.Equal(new[] { "run-aaa" }, await Found("ORDER=42"));
            Assert.Equal(new[] { "run-bbb", "run-aaa" }, await Found("ADA"));
            Assert.Equal(new[] { "run-42", "run-aaa" }, await Found("42")); // its id beside its data, newest first
            Assert.Empty(await Found("customer: Bo"));
            var listed = await handler.Handle(new ListRunsQuery(), CancellationToken.None);
            Assert.Equal(new Dictionary<string, string> { ["order"] = "43", ["customer"] = "ada" }, listed.Single(run => run.Id == "run-bbb").Data);
            Assert.Empty(listed.Single(run => run.Id == "run-42").Data);

            // A run deleted takes its data with it.
            var deleted = new DeleteRunCommandHandler(services.GetRequiredService<IPipelineInstanceManager>(), keyValues, services.GetRequiredService<ITenantAccessor>());
            Assert.True(await deleted.Handle(new DeleteRunCommand("run-aaa"), CancellationToken.None));
            Assert.Empty(await PipelineRunDataStore.ReadAsync(keyValues, tenant, "run-aaa", CancellationToken.None));
            Assert.Empty(await Found("order: 42"));
        }

        // The "Save run data" step: 'key: value' lines (or a JSON object) kept on the run, merged with what it saved before; what is
        // not pairs, or too long, is refused with the reason; the previous result passes through.
        [Fact]
        public async Task Save_run_data_keeps_pairs_on_the_run_and_refuses_what_is_not()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            var run = await services.GetRequiredService<IPipelineRunner>().RunAsync(new Sequence
            {
                Steps =
                {
                    new SaveRunData { Data = new("order: 42\ncustomer = Ada Lovelace") },
                    new SaveRunData { Data = new("{\"customer\": \"Grace\", \"total\": 12.5}") },
                },
            });
            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            Assert.Equal(new Dictionary<string, string> { ["order"] = "42", ["customer"] = "Grace", ["total"] = "12.5" },
                await PipelineRunDataStore.ReadAsync(keyValues, services.GetRequiredService<ITenantAccessor>().TenantId, run.PipelineState.Id, CancellationToken.None));

            var bad = await services.GetRequiredService<IPipelineRunner>().RunAsync(new SaveRunData { Data = new("just words"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("RUN_DATA_INVALID: 'just words' is not 'key: value'.", bad.Journal.StepExecutionContexts.Single(c => c.Step is SaveRunData).GetOutputs()["Error"]);
            var many = string.Join("\n", Enumerable.Range(1, PipelineRunData.MaxKeys + 1).Select(i => $"k{i}: v"));
            var tooMany = await services.GetRequiredService<IPipelineRunner>().RunAsync(new SaveRunData { Data = new(many), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("RUN_DATA_INVALID: A run keeps at most 20 pieces of data.", tooMany.Journal.StepExecutionContexts.Single(c => c.Step is SaveRunData).GetOutputs()["Error"]);
        }
    }
}
