#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95 increment (c), the graph seam: the editor's graph becomes the engine's own Flowchart document and back, and
    /// the ENGINE is the judge - the document it gets is deserialised by its step serializer into a flowchart with the
    /// steps, the typed literal inputs and the connections the graph named, and a run of it executes them. Held to the
    /// engine composed as the host composes it, with no store behind it.
    /// </summary>
    public sealed class PipelineGraphCodecTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var console = _console;
            services.AddEngine(module => module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console)));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private static PipelineGraph Graph() => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "write", Type = "Stomme.WriteLine", Name = "Say it", Position = new GraphPosition { X = 40, Y = 60 }, Inputs = { ["Text"] = "hello from the graph" } },
                new PipelineGraphStep { Id = "decide", Type = "Stomme.FlowDecision", Inputs = { ["Condition"] = "true" } },
                new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Name = "After the decision", Position = new GraphPosition { X = 300, Y = 60 }, Inputs = { ["Text"] = "took the true port" } },
                new PipelineGraphStep { Id = "never", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "took the false port" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "write", TargetId = "decide" },
                new PipelineGraphConnection { SourceId = "decide", SourcePort = "True", TargetId = "after" },
                new PipelineGraphConnection { SourceId = "decide", SourcePort = "False", TargetId = "never" },
            },
        };

        [Fact]
        public async Task The_graph_becomes_a_flowchart_the_engine_reads_and_runs_and_comes_back_as_the_same_graph()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var serializer = scope.ServiceProvider.GetRequiredService<IStepSerializer>();

            var body = PipelineGraphCodec.ToBody(Graph(), currentBody: null, registry, identities);

            // The engine reads it as a flowchart with the steps and connections named.
            var flowchart = Assert.IsType<Flowchart>(serializer.Deserialize(body));
            Assert.Equal(new[] { "write", "decide", "after", "never" }, flowchart.Steps.Select(s => s.Id));
            Assert.Equal("Say it", Assert.IsType<WriteLine>(flowchart.Steps.Single(s => s.Id == "write")).Name);
            Assert.Equal(3, flowchart.Connections.Count);
            Assert.Contains(flowchart.Connections, c => c.Source.Step.Id == "decide" && c.Source.Port == "True" && c.Target.Step.Id == "after");
            Assert.Contains(flowchart.Connections, c => c.Source.Step.Id == "write" && c.Source.Port is null && c.Target.Step.Id == "decide");

            // It runs, and ran what the graph said: the first line, then the decision's True branch and not its False one.
            var result = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(flowchart);
            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            var written = _console.ToString();
            Assert.Contains("hello from the graph", written);
            Assert.Contains("took the true port", written);
            Assert.DoesNotContain("took the false port", written);

            // And it comes back as the graph that made it: ids, types, names, positions, literal inputs, connections.
            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal(Graph().Steps.Select(s => (s.Id, s.Type, s.Name)), back.Steps.Select(s => (s.Id, s.Type, s.Name)));
            Assert.Equal((40d, 60d), (back.Steps[0].Position.X, back.Steps[0].Position.Y));
            Assert.Equal("hello from the graph", back.Steps[0].Inputs["Text"]);
            Assert.Equal("true", back.Steps[1].Inputs["Condition"]);
            Assert.Equal(Graph().Connections.Select(c => (c.SourceId, c.SourcePort, c.TargetId)), back.Connections.Select(c => (c.SourceId, c.SourcePort, c.TargetId)));

            // A second save over the first keeps the flowchart's id and rebuilds the rest.
            var again = PipelineGraphCodec.ToBody(back, body, registry, identities);
            Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(body)!["id"]!.ToString(), System.Text.Json.Nodes.JsonNode.Parse(again)!["id"]!.ToString());
        }

        // BL-229: how many runs may go at once is kept in the pipeline's designer metadata (the engine reads its options, which
        // the save sets); a value the editor does not offer is not kept.
        [Fact]
        public void How_many_runs_go_at_once_is_kept_with_the_graph()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var graph = Graph();
            graph.RunsAtOnce = PipelineRunsAtOnce.OnePerCorrelation;

            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);
            Assert.Equal("one-per-correlation", System.Text.Json.Nodes.JsonNode.Parse(body)!["metadata"]!["designer"]!["runsAtOnce"]!.GetValue<string>());
            Assert.Equal(PipelineRunsAtOnce.OnePerCorrelation, PipelineCodecRoundTrip(body).RunsAtOnce);

            graph.RunsAtOnce = "sometimes";
            Assert.Null(PipelineCodecRoundTrip(PipelineGraphCodec.ToBody(graph, body, registry, identities)).RunsAtOnce);
            graph.RunsAtOnce = null;
            Assert.DoesNotContain("runsAtOnce", PipelineGraphCodec.ToBody(graph, body, registry, identities));

            static PipelineGraph PipelineCodecRoundTrip(string text) => PipelineGraphCodec.ToGraph(text);
        }

        // BL-229: n8n's "save successful executions" - not keeping the runs that finish well is kept in the designer metadata,
        // where the task that deletes them reads it from the published body; kept (the default) writes nothing.
        // BL-229, n8n's "save execution progress": kept with the graph, absent while off.
        [Fact]
        public void Saving_progress_is_kept_with_the_graph()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var graph = Graph();
            graph.SaveProgress = true;

            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);
            Assert.True(System.Text.Json.Nodes.JsonNode.Parse(body)!["metadata"]!["designer"]!["saveProgress"]!.GetValue<bool>());
            Assert.True(PipelineGraphCodec.ToGraph(body).SaveProgress);

            graph.SaveProgress = null;
            var off = PipelineGraphCodec.ToBody(graph, body, registry, identities);
            Assert.DoesNotContain("saveProgress", off);
            Assert.Null(PipelineGraphCodec.ToGraph(off).SaveProgress);
        }

        [Fact]
        public void Not_keeping_the_runs_that_finish_well_is_kept_with_the_graph()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var graph = Graph();
            graph.KeepSuccessfulRuns = false;

            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);
            Assert.False(System.Text.Json.Nodes.JsonNode.Parse(body)!["metadata"]!["designer"]!["keepSuccessfulRuns"]!.GetValue<bool>());
            Assert.False(PipelineGraphCodec.ToGraph(body).KeepSuccessfulRuns);
            Assert.False(PipelineGraphCodec.KeepsSuccessfulRuns(body));

            graph.KeepSuccessfulRuns = null;
            var kept = PipelineGraphCodec.ToBody(graph, body, registry, identities);
            Assert.DoesNotContain("keepSuccessfulRuns", kept);
            Assert.Null(PipelineGraphCodec.ToGraph(kept).KeepSuccessfulRuns);
            Assert.Null(PipelineGraphCodec.KeepsSuccessfulRuns(kept));
            Assert.Null(PipelineGraphCodec.KeepsSuccessfulRuns("not json"));
        }

        // BL-229: what the runs keep of their steps' data is kept in the designer metadata, where the task that empties the step
        // records reads it from the published body; always (the default) writes nothing.
        [Fact]
        public void What_the_runs_keep_of_their_steps_data_is_kept_with_the_graph()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var graph = Graph();
            graph.KeepStepData = PipelineStepDataKeeping.WhenFailed;

            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);
            Assert.Equal("failed", System.Text.Json.Nodes.JsonNode.Parse(body)!["metadata"]!["designer"]!["keepStepData"]!.GetValue<string>());
            Assert.Equal("failed", PipelineGraphCodec.ToGraph(body).KeepStepData);
            Assert.Equal("failed", PipelineGraphCodec.KeepsStepData(body));

            graph.KeepStepData = null;
            var always = PipelineGraphCodec.ToBody(graph, body, registry, identities);
            Assert.DoesNotContain("keepStepData", always);
            Assert.Null(PipelineGraphCodec.ToGraph(always).KeepStepData);
            Assert.Null(PipelineGraphCodec.KeepsStepData(always));
            Assert.Null(PipelineGraphCodec.KeepsStepData("not json"));
        }

        // BL-229: n8n's time saved per run - kept in the designer metadata, where Insights reads it from the published body; not
        // said, nothing is written.
        [Fact]
        public void The_minutes_a_run_saves_are_kept_with_the_graph()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var graph = Graph();
            graph.MinutesSavedPerRun = 15;

            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);
            Assert.Equal(15, System.Text.Json.Nodes.JsonNode.Parse(body)!["metadata"]!["designer"]!["minutesSaved"]!.GetValue<int>());
            Assert.Equal(15, PipelineGraphCodec.ToGraph(body).MinutesSavedPerRun);
            Assert.Equal(15, PipelineGraphCodec.MinutesSaved(body));

            graph.MinutesSavedPerRun = null;
            var unsaid = PipelineGraphCodec.ToBody(graph, body, registry, identities);
            Assert.DoesNotContain("minutesSaved", unsaid);
            Assert.Null(PipelineGraphCodec.ToGraph(unsaid).MinutesSavedPerRun);
            Assert.Null(PipelineGraphCodec.MinutesSaved(unsaid));
            Assert.Null(PipelineGraphCodec.MinutesSaved("not json"));
        }

        [Fact]
        public void Pinned_test_values_are_kept_in_the_designer_metadata_and_a_viewers_body_has_none()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var graph = Graph();
            graph.Variables.Add(new PipelineGraphVariable { Id = "who", Name = "who", TypeName = "String", Value = "world", TestValue = "{\"name\":\"Ada\"}" });
            graph.Variables.Add(new PipelineGraphVariable { Id = "count", Name = "count", TypeName = "Int32" });

            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);

            // Kept on the pipeline's own flowchart, beside the notes - designer metadata a run never reads; the starting value stays.
            Assert.Equal("{\"name\":\"Ada\"}", System.Text.Json.Nodes.JsonNode.Parse(body)!["metadata"]!["designer"]!["testValues"]!["who"]!.GetValue<string>());
            var flowchart = Assert.IsType<Flowchart>(scope.ServiceProvider.GetRequiredService<IStepSerializer>().Deserialize(body));
            Assert.Equal("world", flowchart.Variables.Single(variable => variable.Id == "who").Value?.ToString());
            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal(new string?[] { "{\"name\":\"Ada\"}", null }, back.Variables.Select(variable => variable.TestValue));
            Assert.Equal("world", back.Variables[0].Value);

            // Anyone but an author is given the body and the graph without them, the rest unchanged.
            var stripped = PipelineGraphCodec.WithoutTestValues(body)!;
            Assert.DoesNotContain("Ada", stripped);
            Assert.All(PipelineGraphCodec.ToGraph(stripped).Variables, variable => Assert.Null(variable.TestValue));
            Assert.Equal(back.Steps.Select(step => step.Id), PipelineGraphCodec.ToGraph(stripped).Steps.Select(step => step.Id));
            Assert.All(PipelineGraphCodec.WithoutTestValues(PipelineGraphCodec.ToGraph(body))!.Variables, variable => Assert.Null(variable.TestValue));
            Assert.Null(PipelineGraphCodec.WithoutTestValues((string?)null));
            Assert.Equal("not json \"testValues\"", PipelineGraphCodec.WithoutTestValues("not json \"testValues\""));

            // Forgotten on the canvas, a save over the body drops them.
            back.Variables[0].TestValue = null;
            Assert.DoesNotContain("testValues", PipelineGraphCodec.ToBody(back, body, registry, identities));
        }

        [Fact]
        public void Inputs_are_typed_by_the_catalog_and_a_value_the_type_cannot_hold_is_refused()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();

            var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "for", Type = "Stomme.For", Inputs = { ["Start"] = "1", ["End"] = "3", ["OuterBoundInclusive"] = "true" } } } };
            var body = PipelineGraphCodec.ToBody(graph, null, registry, identities);
            var step = System.Text.Json.Nodes.JsonNode.Parse(body)!["steps"]![0]!;
            Assert.Equal(1, step["start"]!["expression"]!["value"]!.GetValue<int>());
            Assert.Equal("Int32", step["start"]!["typeName"]!.GetValue<string>());
            Assert.True(step["outerBoundInclusive"]!["expression"]!["value"]!.GetValue<bool>());

            var bad = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "for", Type = "Stomme.For", Inputs = { ["Start"] = "one" } } } };
            Assert.Contains("Int32", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(bad, null, registry, identities)).Message);
        }

        [Fact]
        public void The_codec_refuses_what_the_engine_would_not_place_and_reads_a_non_flowchart_as_empty()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();

            Assert.Contains("not a step type", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Stomme.NoSuchStep" } } }, null, registry, identities)).Message);
            Assert.Contains("used twice", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Stomme.WriteLine" }, new PipelineGraphStep { Id = "x", Type = "Stomme.WriteLine" } } }, null, registry, identities)).Message);
            Assert.Contains("not in the graph", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Stomme.WriteLine" } }, Connections = { new PipelineGraphConnection { SourceId = "x", TargetId = "y" } } }, null, registry, identities)).Message);

            // A step without an id gets one; a new pipeline's body (an empty sequence) reads as an empty graph.
            var body = PipelineGraphCodec.ToBody(new PipelineGraph { Steps = { new PipelineGraphStep { Type = "Stomme.WriteLine" } } }, null, registry, identities);
            Assert.False(string.IsNullOrEmpty(PipelineGraphCodec.ToGraph(body).Steps.Single().Id));
            var sequence = scope.ServiceProvider.GetRequiredService<IStepSerializer>().Serialize(new Sequence());
            Assert.Empty(PipelineGraphCodec.ToGraph(sequence).Steps);
            Assert.Empty(PipelineGraphCodec.ToGraph(null).Steps);
            Assert.Empty(PipelineGraphCodec.ToGraph("not json").Steps);
        }
    }
}
