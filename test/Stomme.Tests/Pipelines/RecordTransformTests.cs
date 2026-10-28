#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - the record transformations n8n users reach for (Filter, Sort, Limit, Remove Duplicates, Summarize, Split Out,
    /// Merge), each run by the engine as the host composes it: the result each gives, the values compared by what they hold
    /// (numbers as numbers, dates as dates, otherwise text), Filter's rules read by the one condition evaluator with the
    /// record's fields, what each refuses saying why, and a chain of them through the records convention.
    /// </summary>
    public sealed class RecordTransformTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        private const string Orders = """
            [
              {"id":"1","customer":"Ada","amount":"120.50","placed":"2026-10-02","status":"paid","tags":["new","vip"]},
              {"id":"2","customer":"byron","amount":"9","placed":"2026-09-30","status":"open","tags":[]},
              {"id":"3","customer":"Ada","amount":"30","placed":"2026-10-05","status":"paid"},
              {"id":"4","customer":"Carl","amount":"100","placed":"2026-10-01","status":"Paid","tags":["vip"]},
              {"id":"5","customer":"Byron","amount":"","placed":"2026-10-03","status":"refunded","note":"late"}
            ]
            """;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private async Task<(RunPipelineResult Run, JsonArray Rows)> ResultOf<T>(T step) where T : IStep
        {
            var run = await RunAsync(step);
            Assert.True(run.PipelineState.SubStatus == PipelineSubStatus.Finished,
                $"{run.PipelineState.SubStatus}: {string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message))}");
            return (run, JsonNode.Parse((string)OutputsOf<T>(run)["Result"])!.AsArray());
        }

        private static List<string> Ids(JsonArray rows) => rows.Select(row => row!["id"]!.GetValue<string>()).ToList();

        private async Task<string> FaultOf(IStep step)
        {
            var run = await RunAsync(step);
            Assert.Equal(PipelineSubStatus.Faulted, run.PipelineState.SubStatus);
            return string.Join(" | ", run.PipelineState.Incidents.Select(i => i.Message));
        }

        // Rules as the editor writes them: {{ field }} on the left, the operator's code, the value on the right.
        private static string Rules(string match, bool ignoreCase, params (string Left, string Operator, string Right)[] rules) =>
            PipelineConditions.Write(new PipelineCondition
            {
                Match = match,
                IgnoreCase = ignoreCase,
                Rules = rules.Select(rule => new PipelineConditionRule { Left = rule.Left, Operator = rule.Operator, Right = rule.Right }).ToList(),
            });

        [Fact]
        public async Task Filter_keeps_the_records_that_meet_the_rules_and_hands_the_rest_on_as_discarded()
        {
            var (run, kept) = await ResultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.All, false, ("{{ status }}", "equals", "paid"), ("{{ amount }}", "at-least", "30"))) });
            Assert.Equal(["1", "3"], Ids(kept)); // 120.50 >= 30 as numbers - as text "120.50" < "30"
            Assert.Equal(3, JsonNode.Parse((string)OutputsOf<FilterRecords>(run)["Discarded"])!.AsArray().Count);
            Assert.Equal(2, (int)OutputsOf<FilterRecords>(run)["KeptCount"]);

            // Any rule, ignoring case: Carl's "Paid" now counts, and the refunded order joins through its note.
            (_, kept) = await ResultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.Any, true, ("{{ status }}", "equals", "paid"), ("{{ note }}", "not-empty", ""))) });
            Assert.Equal(["1", "3", "4", "5"], Ids(kept));

            // Dates compare as dates; a record without the field has it empty (not an error); a list contains an item.
            (_, kept) = await ResultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.All, false, ("{{ placed }}", "greater", "2026-10-01"), ("{{ placed }}", "less", "2026-10-05"))) });
            Assert.Equal(["1", "5"], Ids(kept));
            (_, kept) = await ResultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.All, false, ("{{ note }}", "not-equals", "late"))) });
            Assert.Equal(["1", "2", "3", "4"], Ids(kept));
            (_, kept) = await ResultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.All, false, ("{{ tags }}", "contains", "vip"))) });
            Assert.Equal(["1", "4"], Ids(kept));
            (_, kept) = await ResultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.All, true, ("{{ customer }}", "starts-with", "b"))) });
            Assert.Equal(["2", "5"], Ids(kept));

            // No rule keeps every record; text that is not a condition, and an incomplete one, are refused saying why.
            (_, kept) = await ResultOf(new FilterRecords { Records = new(Orders) });
            Assert.Equal(5, kept.Count);
            Assert.Contains("not one the editor wrote", await FaultOf(new FilterRecords { Records = new(Orders), Condition = new("status = paid") }));
            Assert.Contains("tests nothing", await FaultOf(new FilterRecords { Records = new(Orders), Condition = new(Rules(PipelineConditions.All, false, ("", "equals", "paid"))) }));
        }

        [Fact]
        public void A_record_condition_reads_the_records_field_then_a_variable_and_an_absent_one_as_empty()
        {
            var condition = PipelineConditions.Read(Rules(PipelineConditions.All, false, ("{{ amount }}", "greater", "{{ limit }}"), ("{{ missing }}", "empty", "")));
            (bool, object?) Limit(string name) => name == "limit" ? (true, "50") : (false, null);
            Assert.True(PipelineConditions.HoldsFor(condition, JsonSerializer.SerializeToElement(new { amount = 120 }), Limit));
            Assert.False(PipelineConditions.HoldsFor(condition, JsonSerializer.SerializeToElement(new { amount = 9 }), Limit));

            // The record's field wins over a variable of the same name.
            Assert.False(PipelineConditions.HoldsFor(condition, JsonSerializer.SerializeToElement(new { amount = 120, limit = 500 }), Limit));
            Assert.True(PipelineConditions.TryRead(null, out var none) && none.Rules.Count == 0);
            Assert.False(PipelineConditions.TryRead("[1]", out _));
        }

        [Fact]
        public async Task Sort_orders_a_column_by_what_it_holds_with_the_empty_last_and_ties_kept()
        {
            var (_, rows) = await ResultOf(new SortRecords { Records = new(Orders), SortBy = new("amount desc") });
            Assert.Equal(["1", "4", "3", "2", "5"], Ids(rows)); // as numbers, not text; the empty last even descending

            (_, rows) = await ResultOf(new SortRecords { Records = new(Orders), SortBy = new("customer\nplaced desc"), IgnoreCase = new(true) });
            Assert.Equal(["3", "1", "5", "2", "4"], Ids(rows));

            (_, rows) = await ResultOf(new SortRecords { Records = new(Orders), SortBy = new("status") });
            Assert.Equal(["4", "2", "1", "3", "5"], Ids(rows)); // ordinal: "Paid" before "open"; the two "paid" keep their order

            Assert.Contains("Name a field to sort by", await FaultOf(new SortRecords { Records = new(Orders) }));
        }

        [Fact]
        public async Task Limit_keeps_the_first_or_the_last_so_many()
        {
            var (_, rows) = await ResultOf(new LimitRecords { Records = new(Orders), MaxRecords = new(2) });
            Assert.Equal(["1", "2"], Ids(rows));
            (_, rows) = await ResultOf(new LimitRecords { Records = new(Orders), MaxRecords = new(2), Keep = new(LimitKeep.Last) });
            Assert.Equal(["4", "5"], Ids(rows));
            (_, rows) = await ResultOf(new LimitRecords { Records = new(Orders), MaxRecords = new(50) });
            Assert.Equal(5, rows.Count);
        }

        [Fact]
        public async Task Remove_duplicates_keeps_the_first_of_records_that_agree()
        {
            var (run, rows) = await ResultOf(new RemoveDuplicates { Records = new(Orders), CompareFields = new("customer") });
            Assert.Equal(["1", "2", "4", "5"], Ids(rows));
            Assert.Equal(1, (int)OutputsOf<RemoveDuplicates>(run)["RemovedCount"]);

            (_, rows) = await ResultOf(new RemoveDuplicates { Records = new(Orders), CompareFields = new("customer"), IgnoreCase = new(true) });
            Assert.Equal(["1", "2", "4"], Ids(rows));

            // Every field, whatever their order in the record.
            (_, rows) = await ResultOf(new RemoveDuplicates { Records = new("[{\"id\":\"1\",\"a\":1},{\"a\":1,\"id\":\"1\"},{\"id\":\"1\",\"a\":2}]") });
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        public async Task Summarize_groups_and_aggregates_in_the_order_groups_appear()
        {
            var (run, rows) = await ResultOf(new SummarizeRecords
            {
                Records = new(Orders),
                GroupBy = new("customer"),
                Aggregations = new("count\nsum amount\naverage amount as mean\nmax placed\nconcatenate id\ncount distinct status"),
                Separator = new("+"),
            });
            Assert.Equal(4, (int)OutputsOf<SummarizeRecords>(run)["GroupCount"]);
            var ada = rows[0]!;
            Assert.Equal("Ada", ada["customer"]!.GetValue<string>());
            Assert.Equal(2, ada["count"]!.GetValue<int>());
            Assert.Equal(150.50m, ada["sum_amount"]!.GetValue<decimal>());
            Assert.Equal(75.25m, ada["mean"]!.GetValue<decimal>());
            Assert.Equal("2026-10-05", ada["max_placed"]!.GetValue<string>());
            Assert.Equal("1+3", ada["concatenate_id"]!.GetValue<string>());
            Assert.Equal(1, ada["count_distinct_status"]!.GetValue<int>());

            // Byron's one order has no amount: left out of the sum, so the sum is 0 and the average none.
            var byron = rows.Single(row => row!["customer"]!.GetValue<string>() == "Byron")!;
            Assert.Equal(0m, byron["sum_amount"]!.GetValue<decimal>());
            Assert.Null(byron["mean"]);

            // No group: one summary of every record; min of numbers as numbers.
            (_, rows) = await ResultOf(new SummarizeRecords { Records = new(Orders), Aggregations = new("min amount\ncount amount") });
            Assert.Equal("9", Assert.Single(rows)!["min_amount"]!.GetValue<string>());
            Assert.Equal(4, rows[0]!["count_amount"]!.GetValue<int>());

            Assert.Contains("'Ada' is not a number", await FaultOf(new SummarizeRecords { Records = new(Orders), Aggregations = new("sum customer") }));
            Assert.Contains("is not an aggregation", await FaultOf(new SummarizeRecords { Records = new(Orders), Aggregations = new("median amount") }));
        }

        [Fact]
        public async Task Split_out_gives_a_record_per_element_with_the_other_fields_kept()
        {
            var (_, rows) = await ResultOf(new SplitOut { Records = new(Orders), Field = new("tags") });
            Assert.Equal(["1", "1", "4"], Ids(rows)); // order 2's empty list and orders 3 and 5's absent one give none
            Assert.Equal(["new", "vip", "vip"], rows.Select(row => row!["tags"]!.GetValue<string>()).ToList());
            Assert.Equal("Ada", rows[0]!["customer"]!.GetValue<string>());

            (_, rows) = await ResultOf(new SplitOut { Records = new(Orders), Field = new("tags"), KeepOtherFields = new(false), Destination = new("tag") });
            Assert.Equal("{\"tag\":\"new\"}", rows[0]!.ToJsonString());

            // Elements that are objects give their fields.
            (_, rows) = await ResultOf(new SplitOut { Records = new("[{\"order\":\"7\",\"lines\":[{\"sku\":\"A\",\"qty\":2},{\"sku\":\"B\",\"qty\":1}]}]"), Field = new("lines") });
            Assert.Equal("{\"order\":\"7\",\"sku\":\"A\",\"qty\":2}", rows[0]!.ToJsonString());
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        public async Task Merge_appends_or_joins_on_match_fields_in_every_mode()
        {
            const string customers = """[{"name":"Ada","country":"SE"},{"name":"Carl","country":"NO"},{"name":"Dora","country":"DK"},{"name":"Ada","country":"FI"}]""";
            var (_, rows) = await ResultOf(new MergeRecords { Records = new(Orders), OtherRecords = new(customers) });
            Assert.Equal(9, rows.Count);

            // Ada has two customer rows: each of her orders is combined with both.
            var (run, joined) = await ResultOf(new MergeRecords { Records = new(Orders), OtherRecords = new(customers), Mode = new(RecordMergeMode.KeepMatches), MatchFields = new("customer = name") });
            Assert.Equal(["1", "1", "3", "3", "4"], Ids(joined));
            Assert.Equal("NO", joined[4]!["country"]!.GetValue<string>());
            Assert.Equal(3, (int)OutputsOf<MergeRecords>(run)["MatchedCount"]);

            (_, rows) = await ResultOf(new MergeRecords { Records = new(Orders), OtherRecords = new(customers), Mode = new(RecordMergeMode.KeepAllFirst), MatchFields = new("customer = name") });
            Assert.Equal(["1", "1", "2", "3", "3", "4", "5"], Ids(rows));
            Assert.Null(rows[2]!["country"]);

            (_, rows) = await ResultOf(new MergeRecords { Records = new(Orders), OtherRecords = new(customers), Mode = new(RecordMergeMode.KeepEverything), MatchFields = new("customer = name") });
            Assert.Equal(8, rows.Count);
            Assert.Equal("Dora", rows[7]!["name"]!.GetValue<string>());

            (_, rows) = await ResultOf(new MergeRecords { Records = new(Orders), OtherRecords = new(customers), Mode = new(RecordMergeMode.KeepFirstWithoutMatch), MatchFields = new("customer = name"), IgnoreCase = new(true) });
            Assert.Equal(["2", "5"], Ids(rows)); // byron and Byron match no customer, whatever the case

            // A clash keeps the first record's value.
            (_, rows) = await ResultOf(new MergeRecords { Records = new("[{\"k\":\"1\",\"v\":\"first\"}]"), OtherRecords = new("[{\"k\":\"1\",\"v\":\"other\",\"w\":\"x\"}]"), Mode = new(RecordMergeMode.KeepMatches), MatchFields = new("k") });
            Assert.Equal("{\"k\":\"1\",\"v\":\"first\",\"w\":\"x\"}", rows[0]!.ToJsonString());

            Assert.Contains("name them in Match fields", await FaultOf(new MergeRecords { Records = new(Orders), OtherRecords = new(customers), Mode = new(RecordMergeMode.KeepMatches) }));
        }

        // n8n's Loop Over Items and Aggregate: records in batches of so many, and a field's values gathered into a list per group.
        [Fact]
        public async Task Batches_split_the_records_in_order_and_a_list_gathers_a_fields_values()
        {
            var run = await RunAsync(new BatchRecords { Records = new(Orders), BatchSize = new(2) });
            var batches = JsonNode.Parse((string)OutputsOf<BatchRecords>(run)["Result"])!.AsArray();
            Assert.Equal(new[] { 2, 2, 1 }, batches.Select(batch => batch!.AsArray().Count));
            Assert.Equal("5", batches[2]![0]!["id"]!.GetValue<string>());
            Assert.Equal(3, (int)OutputsOf<BatchRecords>(run)["BatchCount"]);
            Assert.Contains("Batch size: at least 1", await FaultOf(new BatchRecords { Records = new(Orders), BatchSize = new(0) }));

            var (_, rows) = await ResultOf(new SummarizeRecords { Records = new(Orders), GroupBy = new("customer"), Aggregations = new("list id\nlist distinct status as statuses") });
            var ada = rows[0]!;
            Assert.Equal("[\"1\",\"3\"]", ada["list_id"]!.ToJsonString());
            Assert.Equal("[\"paid\"]", ada["statuses"]!.ToJsonString());
        }

        // n8n's Compare Datasets: matched on a key (named differently on each side here), sorted four ways, with what differs.
        [Fact]
        public async Task Compare_sorts_two_sets_into_only_first_only_other_same_and_different_with_what_differs()
        {
            const string yesterday = """[{"sku":"A","price":"10","stock":5},{"sku":"B","price":"20","stock":1},{"sku":"C","price":"30"},{"sku":"","price":"0"}]""";
            const string today = """[{"code":"A","price":10,"stock":5},{"code":"B","price":"25","stock":1},{"code":"D","price":"40"}]""";
            var run = await RunAsync(new CompareRecords { Records = new(yesterday), OtherRecords = new(today), MatchFields = new("sku = code") });
            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var outputs = OutputsOf<CompareRecords>(run);
            JsonArray Of(string name) => JsonNode.Parse((string)outputs[name])!.AsArray();

            Assert.Equal("{\"same\":1,\"different\":1,\"onlyInFirst\":2,\"onlyInOther\":1}", (string)outputs["Result"]);
            Assert.Equal("A", Of("Same").Single()!["sku"]!.GetValue<string>()); // "10" and 10 agree as text
            Assert.Equal(new[] { "C", "" }, Of("OnlyInFirst").Select(record => record!["sku"]!.GetValue<string>())); // an empty key matches nothing
            Assert.Equal("D", Of("OnlyInOther").Single()!["code"]!.GetValue<string>());
            var changed = Of("Different").Single()!;
            Assert.Equal("B", changed["key"]!.GetValue<string>());
            var field = changed["fields"]!.AsArray().Single()!;
            Assert.Equal(("price", "20", "25"), (field["field"]!.GetValue<string>(), field["first"]!.GetValue<string>(), field["other"]!.GetValue<string>()));

            // Only the fields named are compared; a field one side lacks differs from one that holds something.
            run = await RunAsync(new CompareRecords { Records = new(yesterday), OtherRecords = new(today), MatchFields = new("sku = code"), CompareFields = new("stock") });
            Assert.Equal("{\"same\":2,\"different\":0,\"onlyInFirst\":2,\"onlyInOther\":1}", (string)OutputsOf<CompareRecords>(run)["Result"]);

            Assert.Contains("Name the key in Match fields", await FaultOf(new CompareRecords { Records = new(yesterday), OtherRecords = new(today), MatchFields = new(" ") }));
        }

        [Fact]
        public async Task A_chain_filters_sorts_limits_and_summarizes_through_the_records_convention()
        {
            var filter = new FilterRecords { Id = "filter", Records = new(Orders), Condition = new(Rules(PipelineConditions.All, false, ("{{ status }}", "not-equals", "refunded"))) };
            var sort = new SortRecords { Id = "sort", SortBy = new("amount desc") };
            var limit = new LimitRecords { Id = "limit", MaxRecords = new(3) };
            var summarize = new SummarizeRecords { Id = "summarize", Aggregations = new("count\nsum amount as total") };
            var flowchart = new Flowchart { Steps = { filter, sort, limit, summarize } };
            flowchart.Connections.Add(new Connection(new Endpoint(filter, DataStep.DoneOutcome), new Endpoint(sort)));
            flowchart.Connections.Add(new Connection(new Endpoint(sort, DataStep.DoneOutcome), new Endpoint(limit)));
            flowchart.Connections.Add(new Connection(new Endpoint(limit, DataStep.DoneOutcome), new Endpoint(summarize)));
            var run = await RunAsync(flowchart);
            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var total = JsonNode.Parse((string)OutputsOf<SummarizeRecords>(run)["Result"])!.AsArray().Single()!;
            Assert.Equal(3, total["count"]!.GetValue<int>());
            Assert.Equal(250.50m, total["total"]!.GetValue<decimal>()); // 120.50 + 100 + 30: byron's 9 cut by the limit
        }
    }
}
