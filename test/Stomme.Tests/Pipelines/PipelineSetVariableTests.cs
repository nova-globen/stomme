#nullable enable
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - the palette's Set Variable, saved and run as the editor writes it. Its Variable is a property holding the variable
    /// itself, not an input holding an expression: written as an input, the engine read a variable of its own making and every run
    /// failed "Variable '...' not found" (measured through the engine, before the fix). Saved, run, read back and checked here as
    /// the editor and the save do it.
    /// </summary>
    public sealed class PipelineSetVariableTests : IAsyncLifetime
    {
        private const string SetVariableType = "Stomme.SetVariable";
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
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddExpressionDescriptorProvider<ConditionExpressionDescriptorProvider>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        // x has no starting value: only the Set Variable step gives it one, from who.
        private static PipelineGraph SetThenSay(string? sets = "v-x")
        {
            var set = new PipelineGraphStep { Id = "set", Type = SetVariableType, Name = "Shout the name", InputExpressions = { ["Value"] = "{{ who | upper }}" } };
            if (sets is not null)
            {
                set.InputVariables["Variable"] = sets;
            }

            return new PipelineGraph
            {
                Variables = { new() { Id = "v-x", Name = "x", TypeName = "String" }, new() { Id = "v-who", Name = "who", TypeName = "String", Value = "ada" } },
                Steps = { set, new() { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "x is {{ x }}" } } },
                Connections = { new() { SourceId = "set", TargetId = "say" } },
            };
        }

        private static string Save(IServiceScope scope, PipelineGraph graph) =>
            PipelineGraphJudge.Body(graph, null, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), scope.ServiceProvider.GetRequiredService<IIdentityGenerator>(), scope.ServiceProvider.GetRequiredService<IStepSerializer>());

        private static async Task<RunPipelineResult> Run(IServiceScope scope, string body) =>
            await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(scope.ServiceProvider.GetRequiredService<IStepSerializer>().Deserialize(body));

        // The body is the pipeline's flowchart: its steps at the top.
        private static JsonObject StepOf(JsonNode body, string id) =>
            body["steps"]!.AsArray().OfType<JsonObject>().Single(step => step["id"]?.GetValue<string>() == id);

        private static JsonObject StepOf(string body, string id) => StepOf(JsonNode.Parse(body)!, id);

        [Fact]
        public async Task A_set_variable_step_sets_its_variable_and_a_later_step_reads_it()
        {
            using var scope = _services.CreateScope();

            var result = await Run(scope, Save(scope, SetThenSay()));

            Assert.Empty(result.PipelineState.Incidents);
            Assert.Equal((PipelineStatus.Finished, PipelineSubStatus.Finished), (result.PipelineState.Status, result.PipelineState.SubStatus));
            Assert.Equal("x is ADA", _console.ToString().Trim());
        }

        // Saved as the variable itself, and read back as the variable the step sets - so the editor shows it chosen and a second
        // save writes it again the same way.
        [Fact]
        public void The_variable_it_sets_is_saved_as_the_variable_itself_and_read_back_as_the_steps()
        {
            using var scope = _services.CreateScope();

            var body = Save(scope, SetThenSay());
            var saved = StepOf(body, "set")["variable"] as JsonObject;
            var back = PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "set");

            Assert.NotNull(saved);
            Assert.Equal(("v-x", "x"), (saved!["id"]?.GetValue<string>(), saved["name"]?.GetValue<string>()));
            Assert.Null(saved["expression"]);
            Assert.Equal("v-x", back.InputVariables["Variable"]);
            Assert.False(back.Inputs.ContainsKey("Variable"));
            Assert.Equal(new[] { "Value" }, back.InputExpressions.Keys);
            Assert.Equal("v-x", StepOf(Save(scope, PipelineGraphCodec.ToGraph(body)), "set")["variable"]?["id"]?.GetValue<string>());
        }

        // A pipeline saved before the fix held the variable as an input - read back as the variable it names, so opening it and
        // saving it again is the repair, and that run works.
        [Fact]
        public async Task One_saved_before_the_fix_is_read_back_as_its_variable_and_runs_once_saved_again()
        {
            using var scope = _services.CreateScope();
            var document = JsonNode.Parse(Save(scope, SetThenSay()))!;
            var set = StepOf(document, "set");
            var variable = set["variable"]!.DeepClone();
            set["variable"] = new JsonObject { ["typeName"] = "Variable", ["expression"] = new JsonObject { ["type"] = "Variable", ["value"] = variable } };
            var before = document.ToJsonString();

            var stale = await Run(scope, before);
            var resaved = Save(scope, PipelineGraphCodec.ToGraph(before));
            var result = await Run(scope, resaved);

            Assert.Contains(stale.PipelineState.Incidents, incident => incident.Message.Contains("not found")); // what every run of one did
            Assert.Equal("v-x", PipelineGraphCodec.ToGraph(before).Steps.Single(step => step.Id == "set").InputVariables["Variable"]);
            Assert.Empty(result.PipelineState.Incidents);
            Assert.Equal("x is ADA", _console.ToString().Trim().Split('\n').Last().Trim());
        }

        // The catalog offers it as a variable to choose, the editor and the save say a step without one is unfinished, and a
        // variable only it sets is no longer "never set".
        [Fact]
        public void Without_its_variable_the_step_is_a_problem_and_writes_none()
        {
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());
            var input = catalog.Single(entry => entry.Type == SetVariableType).Inputs.Single(candidate => candidate.Name == "Variable");

            var unchosen = SetThenSay(sets: null);
            var problems = PipelineGraphProblems.Find(unchosen, catalog);
            var gone = PipelineGraphProblems.Find(SetThenSay(sets: "v-gone"), catalog);

            Assert.Equal(PipelineInputHints.VariablePicker, input.UIHint);
            var missing = Assert.Single(problems, problem => problem.StepId == "set");
            Assert.Equal((PipelineGraphProblems.Error, PipelineGraphProblems.MissingInput, "Variable"), (missing.Severity, missing.Code, missing.Input));
            Assert.Equal("Variable needs a variable: the one this step sets.", missing.Message);
            Assert.Null(StepOf(Save(scope, unchosen), "set")["variable"]);
            Assert.Contains(gone, problem => problem.StepId == "set" && problem.Code == PipelineGraphProblems.MissingVariable && problem.Input == "Variable");
            Assert.Empty(PipelineGraphProblems.Find(SetThenSay(), catalog));
        }
    }
}
