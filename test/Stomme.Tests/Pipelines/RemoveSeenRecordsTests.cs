#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Remove Duplicates "remove items processed in previous executions": Remove records seen before, run by the
    /// engine as the host composes it, run after run of one pipeline - what an earlier run had is removed, twins in one run too;
    /// by one field, only what is higher than before; each step's own history or the pipeline's shared one; the oldest
    /// forgotten past the history's size; Forget starting afresh; a step test reading and remembering nothing; what is kept
    /// is hashes, never the values; and what it refuses, saying why.
    /// </summary>
    public sealed class RemoveSeenRecordsTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>)); // the key-value store's memory
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        // One run of a pipeline "orders" whose one step is the step given - a fresh pipeline object each time, as each run has.
        private async Task<RunPipelineResult> RunAsync(RemoveSeenRecords step, string pipeline = "orders", bool test = false)
        {
            using var scope = _services.CreateScope();
            var options = new RunPipelineOptions();
            if (test)
            {
                options.Properties = new Dictionary<string, object>
                {
                    [StepTestCommitGate.PropertyKey] = true,
                    [Stomme.Modules.Pipelines.Reliability.PipelineCallerPolicy.TestedPipelineKey] = pipeline,
                };
            }

            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(
                new Pipeline { Identity = new PipelineIdentity(pipeline, 1, pipeline + "-v1"), Root = step }, options);
        }

        private static RemoveSeenRecords Step(string records, string id = "seen", string? fields = "id", SeenKeep keep = SeenKeep.NotSeenBefore,
            SeenScope scope = SeenScope.ThisStep, int history = RemoveSeenRecords.DefaultHistory, bool forget = false) => new()
        {
            Id = id,
            Records = new(records),
            CompareFields = new(fields),
            Keep = new(keep),
            Scope = new(scope),
            HistorySize = new(history),
            Forget = new(forget),
        };

        private static (string Kept, int Removed) Result(RunPipelineResult run)
        {
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished,
                $"{run.PipelineState.SubStatus}: {string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message))}");
            var outputs = run.Journal.StepExecutionContexts.Single(c => c.Step is RemoveSeenRecords).GetOutputs();
            var kept = JsonNode.Parse((string)outputs["Result"])!.AsArray().Select(row => row!["id"]!.ToString());
            return (string.Join(",", kept), (int)outputs["RemovedCount"]); // the ids kept, in order
        }

        private static string Ids(params string[] ids) => "[" + string.Join(",", ids.Select(id => "{\"id\":\"" + id + "\",\"name\":\"Customer " + id + "\"}")) + "]";

        [Fact]
        public async Task What_an_earlier_run_had_is_removed_and_twins_in_one_run_too_and_only_hashes_are_kept()
        {
            Assert.Equal(("1,2,3", 0), Result(await RunAsync(Step(Ids("1", "2", "3")))));
            Assert.Equal(("4", 3), Result(await RunAsync(Step(Ids("2", "4", "3", "4")))));
            Assert.Equal(("", 2), Result(await RunAsync(Step(Ids("1", "4")))));

            // Another pipeline has its own history.
            Assert.Equal(("1", 0), Result(await RunAsync(Step(Ids("1")), pipeline: "invoices")));

            // What is kept is hashes, never the values compared: not the id, not the record.
            Result(await RunAsync(Step(Ids("zebra-42"))));
            using var scope = _services.CreateScope();
            var kept = (await scope.ServiceProvider.GetRequiredService<IKeyValueStore>().FindAsync(new KeyValueFilter { Key = RemoveSeenRecords.Key("", "orders", "seen") }, CancellationToken.None))!;
            Assert.DoesNotContain("zebra", kept.SerializedValue);
            Assert.DoesNotContain("Customer", kept.SerializedValue);
            Assert.All(JsonNode.Parse(kept.SerializedValue)!["Seen"]!.AsArray(), hash => Assert.Matches("^[0-9a-f]{32}$", hash!.GetValue<string>()));
            Assert.Equal(5, JsonNode.Parse(kept.SerializedValue)!["Seen"]!.AsArray().Count);
        }

        [Fact]
        public async Task Each_step_has_its_own_history_unless_it_shares_the_pipelines()
        {
            Result(await RunAsync(Step(Ids("1"), id: "a")));
            Assert.Equal(("1", 0), Result(await RunAsync(Step(Ids("1"), id: "b")))); // b's own: 1 is new to it
            Result(await RunAsync(Step(Ids("2"), id: "c", scope: SeenScope.ThePipeline)));
            Assert.Equal(("", 1), Result(await RunAsync(Step(Ids("2"), id: "d", scope: SeenScope.ThePipeline)))); // shared
        }

        [Fact]
        public async Task Past_the_history_size_the_oldest_is_forgotten_and_forget_starts_afresh()
        {
            Result(await RunAsync(Step(Ids("1", "2", "3"), history: 2)));
            Assert.Equal(("1", 1), Result(await RunAsync(Step(Ids("1", "3"), history: 2)))); // 1 was forgotten, 3 kept
            Assert.Equal(("1,3", 0), Result(await RunAsync(Step(Ids("1", "3"), forget: true))));
            Assert.Equal(("", 2), Result(await RunAsync(Step(Ids("1", "3"))))); // and remembered again
        }

        [Fact]
        public async Task By_one_field_only_what_is_higher_than_before_is_kept_numbers_or_dates()
        {
            Assert.Equal(("5,3", 0), Result(await RunAsync(Step(Ids("5", "3"), keep: SeenKeep.HigherThanBefore))));
            Assert.Equal(("6,10", 2), Result(await RunAsync(Step(Ids("4", "6", "5", "10"), keep: SeenKeep.HigherThanBefore))));
            Assert.Equal(("", 1), Result(await RunAsync(Step(Ids("9"), keep: SeenKeep.HigherThanBefore))));

            string Dated(params string[] days) => "[" + string.Join(",", days.Select(day => "{\"id\":\"" + day + "\",\"at\":\"2026-10-" + day + "T08:00:00Z\"}")) + "]";
            Result(await RunAsync(Step(Dated("10"), id: "dates", fields: "at", keep: SeenKeep.HigherThanBefore)));
            Assert.Equal(("11", 2), Result(await RunAsync(Step(Dated("09", "10", "11"), id: "dates", fields: "at", keep: SeenKeep.HigherThanBefore))));
        }

        [Fact]
        public async Task A_step_test_reads_what_the_pipeline_has_seen_and_remembers_nothing()
        {
            Result(await RunAsync(Step(Ids("1"))));
            Assert.Equal(("2", 1), Result(await RunAsync(Step(Ids("1", "2")), test: true)));
            Assert.Equal(("2", 1), Result(await RunAsync(Step(Ids("1", "2"))))); // the test remembered nothing
        }

        [Fact]
        public async Task It_refuses_saying_why_more_than_one_field_to_compare_higher_or_a_value_that_is_neither_number_nor_date()
        {
            static string Refusal(RunPipelineResult run) => run.PipelineState.Incidents.Single().Message;

            Assert.Contains("compares one field", Refusal(await RunAsync(Step(Ids("1"), fields: "id, name", keep: SeenKeep.HigherThanBefore))));
            Assert.Contains("compares one field", Refusal(await RunAsync(Step(Ids("1"), fields: null, keep: SeenKeep.HigherThanBefore))));
            Assert.Contains("is neither", Refusal(await RunAsync(Step(Ids("A-1"), id: "text", keep: SeenKeep.HigherThanBefore))));
            Assert.Contains("no value", Refusal(await RunAsync(Step("[{\"id\":\"\"}]", id: "empty", keep: SeenKeep.HigherThanBefore))));
            Assert.Contains("not JSON", Refusal(await RunAsync(Step("not records", id: "bad"))));
        }
    }
}
