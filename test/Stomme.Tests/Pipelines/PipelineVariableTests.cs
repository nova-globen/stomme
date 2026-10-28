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
    /// One step's output reaches a later step's input through a pipeline variable (the pipeline editor walk, 2026-10-02).
    /// </summary>
    /// <remarks>
    /// A data step's empty text input takes the previous step's result, so a straight chain needed nothing more; a pipeline
    /// that uses an earlier step's output after another step has run did. The graph now names variables, saves an output to
    /// one and reads one into an input, and the codec writes the engine's own shapes - measured by serialising a flowchart
    /// through the engine's step serializer: the flowchart's <c>variables</c>, an output's <c>memoryReference</c>, an input's
    /// <c>Variable</c> expression carrying the variable. The ENGINE runs what the codec wrote, composed as the host composes it
    /// with the module's steps.
    /// </remarks>
    public sealed class PipelineVariableTests : IAsyncLifetime
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

        private static PipelineGraph Graph() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-records", Name = "records", TypeName = "String" },
                new PipelineGraphVariable { Id = "v-csv", Name = "csv", TypeName = "String" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "first", Type = "Stomme.ParseCsv", Inputs = { ["Text"] = "name,town\nAda,London\n" }, OutputVariables = { ["Result"] = "v-records" } },
                // A second parse moves the run's LAST result on: without the variable, the next step would get Bob.
                new PipelineGraphStep { Id = "second", Type = "Stomme.ParseCsv", Inputs = { ["Text"] = "name,town\nBob,Paris\n" } },
                new PipelineGraphStep { Id = "render", Type = "Stomme.ToCsv", InputVariables = { ["Records"] = "v-records" }, OutputVariables = { ["Result"] = "v-csv" } },
                new PipelineGraphStep { Id = "write", Type = "Stomme.WriteLine", InputVariables = { ["Text"] = "v-csv" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "first", SourcePort = "Done", TargetId = "second" },
                new PipelineGraphConnection { SourceId = "second", SourcePort = "Done", TargetId = "render" },
                new PipelineGraphConnection { SourceId = "render", SourcePort = "Done", TargetId = "write" },
            },
        };

        [Fact]
        public async Task An_earlier_steps_output_reaches_a_later_input_through_a_variable_and_the_engine_runs_it()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var serializer = scope.ServiceProvider.GetRequiredService<IStepSerializer>();

            var body = PipelineGraphCodec.ToBody(Graph(), currentBody: null, registry, identities);
            var result = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(serializer.Deserialize(body));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            var written = _console.ToString();
            var ran = string.Join(" | ", result.Journal.StepExecutionContexts.Select(context => context.Step.Id + ":" + context.Status + ":" + string.Join(",", context.GetOutputs().Select(o => o.Key + "=" + o.Value))));
            Assert.True(written.Contains("Ada,London", StringComparison.Ordinal), "console [" + written + "] ran " + ran + " body " + body);
            Assert.DoesNotContain("Bob", written);

            // It comes back as the graph that made it: the variables, every output saved and every input read.
            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal(new[] { ("v-records", "records", "String"), ("v-csv", "csv", "String") }, back.Variables.Select(v => (v.Id, v.Name, v.TypeName)));
            Assert.Equal("v-records", back.Steps[0].OutputVariables["Result"]);
            Assert.Equal("v-records", back.Steps[2].InputVariables["Records"]);
            Assert.Equal("v-csv", back.Steps[2].OutputVariables["Result"]);
            Assert.Equal("v-csv", back.Steps[3].InputVariables["Text"]);
            Assert.DoesNotContain("Text", back.Steps[3].Inputs.Keys);
        }

        // The editor's starting value (BL-95, 2026-10-03): a variable no step has saved to holds what the author gave it, and
        // the engine hands that to an input that reads it - driven in a browser the same night ("hello from the start" in the
        // run log). A value that does not fit the variable's kind is refused at the save.
        [Fact]
        public async Task A_variables_starting_value_reaches_an_input_that_reads_it_before_any_step_saves_to_it()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();
            var serializer = scope.ServiceProvider.GetRequiredService<IStepSerializer>();
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-greeting", Name = "greeting", TypeName = "String", Value = "hello from the start" } },
                Steps = { new PipelineGraphStep { Id = "write", Type = "Stomme.WriteLine", InputVariables = { ["Text"] = "v-greeting" } } },
            };

            var body = PipelineGraphCodec.ToBody(graph, currentBody: null, registry, identities);
            var result = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(serializer.Deserialize(body));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains("hello from the start", _console.ToString());
            Assert.Equal("hello from the start", PipelineGraphCodec.ToGraph(body).Variables.Single().Value);

            graph.Variables[0].TypeName = "Int32";
            Assert.Contains("not a whole number", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(graph, null, registry, identities)).Message);
        }

        [Fact]
        public void An_input_back_to_a_typed_value_drops_the_variable_and_an_undeclared_or_doubled_variable_is_refused()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityGenerator>();

            var body = PipelineGraphCodec.ToBody(Graph(), null, registry, identities);
            var back = PipelineGraphCodec.ToGraph(body);
            back.Steps[3].InputVariables.Clear();
            back.Steps[3].Inputs["Text"] = "a value again";
            var text = JsonNode.Parse(PipelineGraphCodec.ToBody(back, body, registry, identities))!["steps"]![3]!["text"]!;
            Assert.Equal(("Literal", "a value again"), (text["expression"]!["type"]!.GetValue<string>(), text["expression"]!["value"]!.GetValue<string>()));

            var undeclared = Graph();
            undeclared.Steps[3].InputVariables["Text"] = "v-nowhere";
            Assert.Contains("does not declare", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(undeclared, null, registry, identities)).Message);

            var doubled = Graph();
            doubled.Variables.Add(new PipelineGraphVariable { Id = "v-again", Name = "RECORDS" });
            Assert.Contains("declared twice", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(doubled, null, registry, identities)).Message);

            var odd = Graph();
            odd.Variables[1].TypeName = "Dictionary";
            Assert.Contains("not 'Dictionary'", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(odd, null, registry, identities)).Message);
        }
    }
}
