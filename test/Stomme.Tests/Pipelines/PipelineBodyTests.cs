#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
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
    /// BL-95 (2026-10-05): a step's BODY - a loop's Body, an If's Then and Else - authored in the editor's flat graph and run by
    /// the engine.
    /// </summary>
    /// <remarks>
    /// A graph stays one list: a step names the step it sits in (<c>Parent</c>) and which body (<c>ParentPort</c>). The codec
    /// writes each body as a flowchart in the holder's port - the engine's own shape, measured through its step serializer -
    /// and reads it back flat. What the codec refuses, the save refuses: a body the holder does not have, a connection
    /// crossing into another body, a step inside itself.
    /// </remarks>
    public sealed class PipelineBodyTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private async Task<(string Body, string Written, PipelineStatus Status)> RunAsync(PipelineGraph graph)
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var serializer = scope.ServiceProvider.GetRequiredService<IStepSerializer>();
            var body = PipelineGraphCodec.ToBody(graph, currentBody: null, registry, identities);
            var result = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(serializer.Deserialize(body));
            return (body, _console.ToString(), result.PipelineState.Status);
        }

        private string ToBody(PipelineGraph graph, string? current = null)
        {
            using var scope = _services.CreateScope();
            return PipelineGraphCodec.ToBody(graph, current, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), scope.ServiceProvider.GetRequiredService<IIdentityGenerator>());
        }

        [Fact]
        public async Task A_loops_body_runs_once_per_value_and_reads_the_current_value_through_a_variable()
        {
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-n", Name = "n", TypeName = "Object" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "loop", Type = "Stomme.For", Inputs = { ["Start"] = "1", ["End"] = "3", ["Step"] = "1", ["OuterBoundInclusive"] = "true" }, OutputVariables = { ["CurrentValue"] = "v-n" } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Parent = "loop", ParentPort = "Body", InputVariables = { ["Text"] = "v-n" } },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "after the loop" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "loop", TargetId = "after" } },
            };

            var (body, written, status) = await RunAsync(graph);

            Assert.Equal(PipelineStatus.Finished, status);
            var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(new[] { "1", "2", "3", "after the loop" }.SequenceEqual(lines), "written [" + written + "] body " + body);

            // The engine's shape: the body is a flowchart in the loop's "body" property, holding the body's step.
            var loop = JsonNode.Parse(body)!["steps"]!.AsArray().Single(step => step!["id"]!.GetValue<string>() == "loop")!;
            Assert.Equal(PipelineGraphCodec.FlowchartType, loop["body"]!["type"]!.GetValue<string>());
            Assert.Equal("say", loop["body"]!["steps"]![0]!["id"]!.GetValue<string>());

            // It comes back as the flat graph that made it.
            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal(new[] { ("loop", (string?)null, (string?)null), ("say", "loop", "Body"), ("after", null, null) }, back.Steps.Select(step => (step.Id, step.Parent, step.ParentPort)));
            Assert.Equal("v-n", back.Steps.Single(step => step.Id == "say").InputVariables["Text"]);
        }

        [Fact]
        public async Task An_If_runs_the_branch_its_condition_names_and_a_branch_is_a_flowchart_of_its_own()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "check", Type = "Stomme.If", Inputs = { ["Condition"] = "true" } },
                    new PipelineGraphStep { Id = "yes-1", Type = "Stomme.WriteLine", Parent = "check", ParentPort = "Then", Inputs = { ["Text"] = "then, first" } },
                    new PipelineGraphStep { Id = "yes-2", Type = "Stomme.WriteLine", Parent = "check", ParentPort = "Then", Inputs = { ["Text"] = "then, second" } },
                    new PipelineGraphStep { Id = "no", Type = "Stomme.WriteLine", Parent = "check", ParentPort = "Else", Inputs = { ["Text"] = "else" } },
                },
                // A connection inside the Then branch: the branch runs its two steps in order.
                Connections = { new PipelineGraphConnection { SourceId = "yes-1", TargetId = "yes-2" } },
            };

            var (body, written, status) = await RunAsync(graph);

            Assert.Equal(PipelineStatus.Finished, status);
            Assert.Equal(new[] { "then, first", "then, second" }, written.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var check = JsonNode.Parse(body)!["steps"]![0]!;
            Assert.Single(check["then"]!["connections"]!.AsArray());
            Assert.Empty(JsonNode.Parse(body)!["connections"]!.AsArray());
            Assert.Equal("else", check["else"]!["steps"]![0]!["text"]!["expression"]!["value"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_ForEach_walks_the_rows_an_earlier_step_produced()
        {
            var graph = new PipelineGraph
            {
                Variables =
                {
                    new PipelineGraphVariable { Id = "v-rows", Name = "rows", TypeName = "Object" },
                    new PipelineGraphVariable { Id = "v-row", Name = "row", TypeName = "Object" },
                },
                Steps =
                {
                    new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Inputs = { ["Text"] = "name\nAda\nGrace\n" }, OutputVariables = { ["Result"] = "v-rows" } },
                    new PipelineGraphStep { Id = "each", Type = "Stomme.ForEach", InputVariables = { ["Items"] = "v-rows" }, OutputVariables = { ["CurrentValue"] = "v-row" } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Parent = "each", ParentPort = "Body", InputVariables = { ["Text"] = "v-row" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "each" } },
            };

            var (body, written, status) = await RunAsync(graph);

            Assert.True(status == PipelineStatus.Finished, $"{status}; written [{written}] body {body}");
            var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(lines.Length == 2 && lines[0].Contains("Ada", StringComparison.Ordinal) && lines[1].Contains("Grace", StringComparison.Ordinal), "written [" + written + "] body " + body);
        }

        [Fact]
        public void The_save_refuses_a_body_the_holder_lacks_a_connection_across_bodies_and_a_step_inside_itself()
        {
            PipelineGraph Holding(params PipelineGraphStep[] inner) => new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "loop", Type = "Stomme.While", Inputs = { ["Condition"] = "false" } } },
            }.With(inner);

            var noSuchBody = Assert.Throws<ArgumentException>(() => ToBody(Holding(new PipelineGraphStep { Id = "a", Type = "Stomme.WriteLine", Parent = "loop", ParentPort = "Then" })));
            Assert.Contains("does not hold steps in", noSuchBody.Message);

            var notAHolder = Assert.Throws<ArgumentException>(() => ToBody(Holding(
                new PipelineGraphStep { Id = "a", Type = "Stomme.WriteLine" },
                new PipelineGraphStep { Id = "b", Type = "Stomme.WriteLine", Parent = "a", ParentPort = "Body" })));
            Assert.Contains("does not hold steps in", notAHolder.Message);

            var missingHolder = Assert.Throws<ArgumentException>(() => ToBody(Holding(new PipelineGraphStep { Id = "a", Type = "Stomme.WriteLine", Parent = "gone", ParentPort = "Body" })));
            Assert.Contains("not in the graph", missingHolder.Message);

            var across = Holding(new PipelineGraphStep { Id = "inside", Type = "Stomme.WriteLine", Parent = "loop", ParentPort = "Body" }, new PipelineGraphStep { Id = "outside", Type = "Stomme.WriteLine" });
            across.Connections.Add(new PipelineGraphConnection { SourceId = "inside", TargetId = "outside" });
            Assert.Contains("crosses into another body", Assert.Throws<ArgumentException>(() => ToBody(across)).Message);

            var loopInLoop = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "outer", Type = "Stomme.While", Parent = "inner", ParentPort = "Body" },
                    new PipelineGraphStep { Id = "inner", Type = "Stomme.While", Parent = "outer", ParentPort = "Body" },
                },
            };
            Assert.Contains("sits inside itself", Assert.Throws<ArgumentException>(() => ToBody(loopInLoop)).Message);
        }

        [Fact]
        public void A_save_keeps_a_bodys_flowchart_id_and_an_untouched_step_inside_it_keeps_what_the_editor_does_not_author()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "loop", Type = "Stomme.While", Inputs = { ["Condition"] = "false" } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Parent = "loop", ParentPort = "Body", Inputs = { ["Text"] = "hi" } },
                },
            };
            var first = JsonNode.Parse(ToBody(graph))!;
            var bodyId = first["steps"]![0]!["body"]!["id"]!.GetValue<string>();

            // An expression kind the editor does not author, inside the body - as code or another tool could leave it.
            first["steps"]![0]!["body"]!["steps"]![0]!["text"] = JsonNode.Parse("""{"typeName":"String","expression":{"type":"JavaScript","value":"'from a script'"}}""");
            var edited = first.ToJsonString();
            var reread = PipelineGraphCodec.ToGraph(edited);
            Assert.DoesNotContain("Text", reread.Steps.Single(step => step.Id == "say").Inputs.Keys);

            var second = JsonNode.Parse(ToBody(reread, edited))!;
            Assert.Equal(bodyId, second["steps"]![0]!["body"]!["id"]!.GetValue<string>());
            Assert.Equal("JavaScript", second["steps"]![0]!["body"]!["steps"]![0]!["text"]!["expression"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public void A_trigger_inside_a_body_never_starts_the_pipeline()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "loop", Type = "Stomme.While", Inputs = { ["Condition"] = "false" } },
                    new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Parent = "loop", ParentPort = "Body", Inputs = { ["WebhookName"] = "inner-hook" } },
                },
            };

            var hook = JsonNode.Parse(ToBody(graph))!["steps"]![0]!["body"]!["steps"]![0]!;
            Assert.Null(hook["customProperties"]!["canStartPipeline"]);
        }
    }

    internal static class PipelineGraphTestExtensions
    {
        public static PipelineGraph With(this PipelineGraph graph, params PipelineGraphStep[] steps)
        {
            graph.Steps.AddRange(steps);
            return graph;
        }
    }
}
