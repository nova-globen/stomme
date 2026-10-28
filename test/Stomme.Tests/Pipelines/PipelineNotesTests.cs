#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 8 - sticky notes and step comments: kept in the flowchart's and the step's designer metadata, so they
    /// come back as they were written, the engine reads (and runs) the pipeline unchanged around them, two editors merge them
    /// as text, and a comparison names them.
    /// </summary>
    public sealed class PipelineNotesTests : IAsyncLifetime
    {
        private ServiceProvider _engine = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            _engine = services.BuildServiceProvider();
            using var scope = _engine.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _engine.DisposeAsync();

        private static PipelineGraph Graph() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Comment = "Greets whoever reads the log", Inputs = { ["Text"] = "hi" } },
                new PipelineGraphStep { Id = "loop", Type = "Stomme.For", Inputs = { ["End"] = "2" } },
            },
            Notes =
            {
                new PipelineGraphNote { Id = "n1", Text = "Remember: the CSV comes from finance\nevery Monday", Position = new GraphPosition { X = 400, Y = 300 } },
                // A body holding a note and no step is still written, so the note survives.
                new PipelineGraphNote { Id = "n2", Text = "Each pass logs nothing yet", Parent = "loop", ParentPort = "Body", Position = new GraphPosition { X = 30, Y = 40 } },
                // A note in a body of a step the graph does not have goes.
                new PipelineGraphNote { Id = "n3", Text = "orphan", Parent = "gone", ParentPort = "Body" },
            },
        };

        [Fact]
        public async Task Notes_and_comments_come_back_as_written_and_the_engine_runs_the_pipeline_around_them()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphJudge.Body(Graph(), null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());

            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal("Greets whoever reads the log", back.Steps.Single(step => step.Id == "say").Comment);
            Assert.Null(back.Steps.Single(step => step.Id == "loop").Comment);
            Assert.Equal(new[]
            {
                ("n1", "Remember: the CSV comes from finance\nevery Monday", (string?)null, 400d),
                ("n2", "Each pass logs nothing yet", "loop", 30d),
            }, back.Notes.Select(note => (note.Id, note.Text, note.Parent, note.Position.X)).ToArray());

            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
        }

        [Fact]
        public void Two_editors_merge_notes_and_comments_as_text_and_a_comparison_names_them()
        {
            var graph = Graph();
            graph.Notes.RemoveAt(2);
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("Greets whoever reads the log", fields["step/say/comment"]);
            Assert.Equal("1", fields["note/n1"]);
            Assert.Equal("loop/Body", fields["note/n2/parent"]);

            var back = PipelineGraphFields.ToGraph(fields, graph);
            Assert.Equal(PipelineGraphFields.Version(fields), PipelineGraphFields.Version(PipelineGraphFields.ToFields(back)));

            var edited = Graph();
            edited.Notes.RemoveAt(2);
            edited.Notes[0].Text = "Remember: the CSV comes from sales";
            edited.Steps[0].Comment = "Greets";
            var changes = PipelineGraphFields.Changes(graph, edited);
            Assert.All(changes, change => Assert.Equal(Stomme.Collaboration.FieldKind.Text, change.Kind));
            Assert.Equal(new[] { "note/n1/text", "step/say/comment" }, changes.Select(change => change.Path));

            // One editor removed the loop while the other wrote a note in its body: the note would dangle.
            var merged = new Dictionary<string, string?>(fields);
            merged.Remove("step/loop");
            Assert.Contains(PipelineGraphFields.Dangling(merged, fields), conflict => conflict.Path == "note/n2/parent");

            Assert.Contains("Added note 'Remember: the CSV comes from f…'", PipelineGraphDiff.Lines(new PipelineGraph(), graph));
            Assert.Contains("step 'Say', its comment: 'Greets whoever reads the log' → 'Greets'", PipelineGraphDiff.Lines(graph, edited));
        }

        // BL-229 - n8n's note colours: kept in the flowchart's designer metadata (none written for the accent, a name the editor
        // does not offer read as the accent), merged as a field of their own - one editor's colour and another's text both stay -
        // and brought along in a pipeline file, where a colour the editor does not offer is the accent too.
        [Fact]
        public void A_notes_colour_comes_back_as_kept_merges_beside_its_text_and_an_unknown_one_is_the_accent()
        {
            using var scope = _engine.CreateScope();
            var services = scope.ServiceProvider;
            var graph = Graph();
            graph.Notes.RemoveAt(2);
            graph.Notes[0].Color = "Red";
            graph.Notes[1].Color = "purple";
            var body = PipelineGraphJudge.Body(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>(), services.GetRequiredService<IStepSerializer>());
            Assert.Contains("\"color\":\"red\"", body.Replace(" ", string.Empty));
            Assert.DoesNotContain("purple", body);
            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal(new[] { ("n1", (string?)PipelineNoteColors.Red), ("n2", (string?)null) }, back.Notes.Select(note => (note.Id, note.Color)).ToArray());

            // Two editors: one paints the note green, the other rewrites its text - both kept.
            var stored = PipelineGraphFields.ToFields(back);
            var painted = PipelineGraphFields.ToGraph(stored, back);
            painted.Notes[0].Color = PipelineNoteColors.Green;
            var rewritten = PipelineGraphFields.ToGraph(stored, back);
            rewritten.Notes[0].Text = "From sales now";
            var merged = new Dictionary<string, string?>(stored);
            foreach (var change in PipelineGraphFields.Changes(back, painted).Concat(PipelineGraphFields.Changes(back, rewritten)))
            {
                Assert.Equal(Stomme.Collaboration.FieldKind.Text, change.Kind);
                merged[change.Path] = change.ProposedValue;
            }

            var both = PipelineGraphFields.ToGraph(merged, back).Notes[0];
            Assert.Equal((PipelineNoteColors.Green, "From sales now"), (both.Color, both.Text));

            var file = PipelineExchange.Read(PipelineExchange.Write("Notes", null, new PipelineGraph { Notes = { new PipelineGraphNote { Id = "n", Text = "x", Color = "amber" }, new PipelineGraphNote { Id = "m", Text = "y", Color = "neon" } } }));
            Assert.Equal(new[] { (string?)PipelineNoteColors.Amber, null }, file.Graph.Notes.Select(note => note.Color).ToArray());
        }
    }
}
