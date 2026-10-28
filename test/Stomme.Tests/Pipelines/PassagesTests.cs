#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - "answer from your documents", the retrieval half: Store passages splits a text into passages - paragraphs whole
    /// where they fit, a long one cut between sentences, each passage after the first carrying the end of the one before - and
    /// keeps them in a data table, made when missing, a source stored again replacing its passages; Find passages ranks them for
    /// a question by BM25, best first, leaves out those sharing no word with it, and numbers them as one context for Ask AI.
    /// </summary>
    public sealed class PassagesTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
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

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private const string Handbook =
            "Returns are accepted within 30 days of delivery. The item must be unused and in its original box, with the receipt or the order number on the slip.\n\n" +
            "Shipping inside Sweden takes two working days with the standard service. Abroad it takes five to seven working days, depending on the carrier.\n\n" +
            "Invoices are sent by email when the order ships. Pay within 14 days of the invoice date; a late payment costs a reminder fee of 60 kronor.";

        [Fact]
        public void A_text_is_split_by_paragraphs_with_overlap_and_a_long_one_between_sentences()
        {
            var passages = Passages.Split(Handbook, 200, 30);
            Assert.Equal(3, passages.Count);
            Assert.StartsWith("Returns are accepted", passages[0]);
            Assert.EndsWith("depending on the carrier.", passages[1]);
            var carried = passages[1].Split("\n\n")[0]; // the end of the one before, from a word's start
            Assert.True(carried.Length is > 0 and <= 30, carried);
            Assert.EndsWith(carried, passages[0]);
            Assert.All(passages, passage => Assert.True(passage.Length <= 200 + 30, passage));

            var long_ = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"Sentence number {i} says something."));
            var cut = Passages.Split(long_, 200, 0);
            Assert.True(cut.Count > 3);
            Assert.All(cut, passage => Assert.True(passage.Length <= 200 && passage.EndsWith('.'), passage));
            Assert.Equal(long_, string.Join(" ", cut));
            Assert.Empty(Passages.Split("  \n\n ", 800, 100));
        }

        [Fact]
        public void Passages_are_ranked_by_the_words_they_share_with_the_question()
        {
            var passages = Passages.Split(Handbook, 200, 0);
            var ranked = Passages.Rank("How long does shipping abroad take?", passages, 4);
            Assert.Equal(1, ranked[0].Index);
            Assert.DoesNotContain(ranked, found => found.Index == 0 && found.Score > ranked[0].Score);
            Assert.Empty(Passages.Rank("xylophone quartz", passages, 4));
            Assert.Equal(new[] { "när", "kommer", "fakturan", "2026" }, Passages.Words("När kommer fakturan? (2026)"));
        }

        [Fact]
        public async Task Passages_are_stored_replaced_and_found_for_a_question()
        {
            var stored = await RunAsync(new StorePassages { Text = new(Handbook), Source = new("handbook.pdf"), Length = new(200), Overlap = new(0) });
            Assert.Equal(PipelineSubStatus.Finished, stored.PipelineState.SubStatus);
            Assert.Equal(3, OutputsOf<StorePassages>(stored)["Count"]);
            await RunAsync(new StorePassages { Text = new("Gift cards never expire."), Source = new("gift-cards.txt") });

            // The handbook stored again replaces its passages.
            await RunAsync(new StorePassages { Text = new(Handbook), Source = new("handbook.pdf"), Length = new(200), Overlap = new(0) });
            using (var scope = _services.CreateScope())
            {
                var keyValues = scope.ServiceProvider.GetRequiredService<IKeyValueStore>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantAccessor>().TenantId;
                var table = (await PipelineSiteTableStore.FindAsync(keyValues, tenant, "Passages", default))!;
                Assert.Equal(new[] { "source", "passage", "text" }, table.Columns.Select(column => column.Name));
                Assert.Equal(4, (await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenant, table.Id, default)).Count);
            }

            var found = await RunAsync(new FindPassages { Question = new("When are invoices sent and how long to pay?"), Top = new(2) });
            Assert.Equal(PipelineSubStatus.Finished, found.PipelineState.SubStatus);
            var records = JsonNode.Parse((string)OutputsOf<FindPassages>(found)["Result"])!.AsArray();
            Assert.Equal(("handbook.pdf", 3), (records[0]!["source"]!.GetValue<string>(), records[0]!["passage"]!.GetValue<int>()));
            Assert.StartsWith("[1] (handbook.pdf) Invoices are sent by email", (string)OutputsOf<FindPassages>(found)["Context"]);

            var giftOnly = await RunAsync(new FindPassages { Question = new("Do gift cards expire?"), Source = new("gift-cards.txt") });
            Assert.Equal(1, OutputsOf<FindPassages>(giftOnly)["Count"]);
            var none = await RunAsync(new FindPassages { Question = new("xylophone"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal((0, ""), ((int)OutputsOf<FindPassages>(none)["Count"], (string)OutputsOf<FindPassages>(none)["Context"]));
            var noTable = await RunAsync(new FindPassages { Table = new("Nowhere"), Question = new("x"), OnFailure = new(FailurePolicy.Continue) });
            Assert.Equal("PASSAGES_TABLE: The site has no data table 'Nowhere' - Store passages makes it.", OutputsOf<FindPassages>(noTable)["Error"]);
        }
    }
}
