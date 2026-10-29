#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using PipelineDefinitionHandle = Stomme.Engine.Pipelines.Models.PipelineDefinitionHandle;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;
using RunPipelineResult = Stomme.Engine.Pipelines.Models.RunPipelineResult;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Models.Pipelines;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Stores;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Canvas;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// An input's expression runs in the ENGINE: the codec writes the module's <c>Template</c> kind, the engine
    /// renders it from the variables in scope when the step runs (the same renderer the editor previews with), a name no
    /// variable has faults the step saying which, and the graph comes back as it was written. A finished run's variables are
    /// what the editor previews with - an author's only.
    /// </summary>
    public sealed class PipelineExpressionTests : IAsyncLifetime
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
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private const string Template = "{{ greeting }}, {{ people[1].name }} of {{ people[1].town }}";

        private static PipelineGraph Graph(string template) => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-people", Name = "people", TypeName = "String" },
                new PipelineGraphVariable { Id = "v-greeting", Name = "greeting", TypeName = "String", Value = "Hello" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Inputs = { ["Text"] = "name,town\nAda,London\nGrace,Arlington\n" }, OutputVariables = { ["Result"] = "v-people" } },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = template } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "say" } },
        };

        private async Task<(string Body, RunPipelineResult Result)> RunAsync(IServiceProvider services, PipelineGraph graph)
        {
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            return (body, result);
        }

        [Fact]
        public async Task A_template_input_is_filled_from_the_variables_when_the_step_runs_and_comes_back_as_written()
        {
            using var scope = _services.CreateScope();
            var (body, result) = await RunAsync(scope.ServiceProvider, Graph(Template));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains("Hello, Grace of Arlington", _console.ToString());

            var back = PipelineGraphCodec.ToGraph(body);
            Assert.Equal(Template, back.Steps.Single(step => step.Id == "say").InputExpressions["Text"]);
            Assert.Empty(back.Steps.Single(step => step.Id == "say").Inputs);

            // A save that no longer names the expression - the author switched the input back - drops it, as it does a variable.
            var plain = Graph(Template);
            plain.Steps[1].InputExpressions.Clear();
            var rewritten = PipelineGraphCodec.ToBody(plain, body, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), scope.ServiceProvider.GetRequiredService<IIdentityGenerator>());
            Assert.Empty(PipelineGraphCodec.ToGraph(rewritten).Steps.Single(step => step.Id == "say").InputExpressions);
        }

        [Fact]
        public async Task A_name_no_variable_has_faults_the_step_and_says_which()
        {
            using var scope = _services.CreateScope();
            var (_, result) = await RunAsync(scope.ServiceProvider, Graph("Hi {{ nobody }}"));

            Assert.Equal(PipelineSubStatus.Faulted, result.PipelineState.SubStatus);
            Assert.Contains(result.PipelineState.Incidents, incident => incident.Message.Contains("There is no variable named 'nobody'.", StringComparison.Ordinal));
        }

        // The preview's values: what the run's steps saved, as an author is shown them (the run-steps query over the step store,
        // a record's output a string-KIND JsonElement as the store hands it back) - rendered, the line the engine wrote.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_preview_fills_a_template_from_what_the_runs_steps_saved_for_an_author(bool author)
        {
            using var scope = _services.CreateScope();
            var store = new MemoryStepExecutionStore(new MemoryStore<StepExecutionRecord>());
            var records = System.Text.Json.JsonSerializer.Serialize("[{\"name\":\"Ada\",\"town\":\"London\"},{\"name\":\"Grace\",\"town\":\"Arlington\"}]");
            await store.SaveManyAsync(new[]
            {
                new StepExecutionRecord
                {
                    Id = "e-1", PipelineInstanceId = "run-1", StepId = "parse", StepNodeId = "n-parse", StepType = "Stomme.ParseCsv", StepTypeVersion = 1,
                    StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Status = StepStatus.Completed,
                    Outputs = new Dictionary<string, object?> { ["Result"] = System.Text.Json.JsonDocument.Parse(records).RootElement.Clone() },
                },
            });
            var views = await new ListRunStepsQueryHandler(store, scope.ServiceProvider.GetRequiredService<IStepRegistry>(), new Guard(author))
                .Handle(new ListRunStepsQuery("run-1"), CancellationToken.None);

            var graph = Graph(Template);
            var values = RunValues.FromSteps(views, stepId => graph.Steps.FirstOrDefault(step => step.Id == stepId)?.OutputVariables);
            if (!author)
            {
                Assert.Empty(values); // a viewer is shown no outputs, so the preview has no run values
                Assert.Null(Assert.Single(views).ResultCount); // nor how many records they held
                return;
            }

            Assert.Equal(2, Assert.Single(views).ResultCount); // the canvas's "2 records", from the JSON text the step saved

            Assert.Equal(new[] { "v-people" }, values.Keys);
            var rendered = PipelineTemplate.Render(Template, name => name switch
            {
                "people" => (true, values["v-people"]),
                "greeting" => (true, graph.Variables.Single(variable => variable.Name == "greeting").Value),
                _ => (false, null),
            });
            Assert.Equal("Hello, Grace of Arlington", rendered);
        }

        private sealed class Guard(bool author) : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission == Stomme.Pipelines.PipelinesPermissions.Manage ? author : true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }
    }
}
