#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's "When executed by another workflow": When called by another pipeline holds what its caller sent to the fields
    /// it expects - each value as its field holds it, the engine's own keys left out - fails the run when a value cannot be what
    /// its field holds or a required field was not sent, saying which; with no fields it hands on whatever came. The fields read
    /// as Extract information's, and make the caller's template.
    /// </summary>
    public sealed class WhenCalledTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private const string Fields = "order (number): the order's number\ncustomer: who ordered\nrush (true or false)";

        private async Task<RunPipelineResult> CallAsync(Dictionary<string, object> input, string? fields = Fields, string? required = "order")
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(
                new WhenCalled { Fields = new(fields), RequiredFields = new(required) }, new RunPipelineOptions { Input = input });
        }

        private static string Result(RunPipelineResult run) => (string)run.Journal.StepExecutionContexts.Single(c => c.Step is WhenCalled).GetOutputs()["Result"];

        [Fact]
        public async Task What_the_caller_sent_is_held_to_the_fields_and_refused_saying_why()
        {
            var called = await CallAsync(new() { ["order"] = "42", ["customer"] = "Ada", ["rush"] = "yes", ["extra"] = "kept out", ["__EvaluationId"] = "internal" });
            Assert.Equal(PipelineSubStatus.Finished, called.PipelineState.SubStatus);
            Assert.Equal("""{"order":42,"customer":"Ada","rush":true}""", Result(called));

            var missing = await CallAsync(new() { ["customer"] = "Ada" });
            Assert.Equal((PipelineSubStatus.Faulted, "The caller sent no 'order' - it is required."), (missing.PipelineState.SubStatus, missing.PipelineState.Incidents.Single().Message));
            var wrong = await CallAsync(new() { ["order"] = "lots" });
            Assert.Equal("'lots' is not a number, which 'order' holds.", wrong.PipelineState.Incidents.Single().Message);
            var optional = await CallAsync(new() { ["order"] = 7 }, required: null);
            Assert.Equal("""{"order":7,"customer":null,"rush":null}""", Result(optional));

            // No fields: whatever came, without the engine's keys.
            Assert.Equal("""{"anything":"goes"}""", Result(await CallAsync(new() { ["anything"] = "goes", ["__x"] = "y" }, fields: null, required: null)));
            var badRequired = await CallAsync(new() { ["order"] = "1" }, required: "order, nope");
            Assert.Equal("'nope' is required but is none of the fields.", badRequired.PipelineState.Incidents.Single().Message);
        }

        [Fact]
        public void The_fields_make_the_callers_template_and_its_description()
        {
            var fields = PipelineFields.Parse(Fields, out var error)!;
            Assert.Null(error);
            Assert.Equal("""{"order":0,"customer":"","rush":false}""", PipelineFields.Template(fields));
            Assert.Equal("order (number) - the order's number; customer - who ordered; rush (true or false)", PipelineFields.Describe(fields));
            Assert.Null(PipelineFields.Parse(" \n ", out error));
            Assert.Equal("The step needs at least one field.", error);
        }

        private static PipelineGraph Called(string fields = Fields, string? required = "order, nope", bool disabled = false) => new()
        {
            Steps = { new PipelineGraphStep { Id = "in", Type = PipelineCallContracts.WhenCalledType, Disabled = disabled, Inputs = { ["Fields"] = fields, ["RequiredFields"] = required ?? "" } } },
        };

        private static PipelineGraphStep Call(string? input) => new()
        {
            Id = "call", Type = "Stomme.ExecutePipeline", Inputs = input is null ? new() { ["PipelineDefinitionId"] = "child" } : new() { ["PipelineDefinitionId"] = "child", ["Input"] = input },
        };

        [Fact]
        public void A_call_is_held_to_the_called_pipelines_contract_saying_what_it_would_refuse()
        {
            var contract = PipelineCallContracts.Of(Called())!;
            Assert.Equal((3, "order"), (contract.Fields.Count, string.Join(",", contract.Required))); // 'nope' is no field
            Assert.Null(PipelineCallContracts.Of(Called(disabled: true)));
            Assert.Null(PipelineCallContracts.Of(new PipelineGraph()));

            Assert.Empty(PipelineCallContracts.Check(Call("""{"order": 42, "rush": "yes"}"""), contract));
            Assert.Equal(new[] { "Input sends no 'order', which the called pipeline requires." }, PipelineCallContracts.Check(Call(null), contract));
            Assert.Equal(new[] { "Input is not a JSON object - the called pipeline reads its fields from one." }, PipelineCallContracts.Check(Call("[1, 2]"), contract));
            Assert.Equal(new[] { "Input sends no 'order', which the called pipeline requires.", "'maybe' is not true or false, which 'rush' holds - the called pipeline would refuse it." },
                PipelineCallContracts.Check(Call("""{"Rush": "maybe"}"""), contract));
            var fromTheRun = Call(null);
            fromTheRun.InputExpressions["Input"] = "{{ record }}";
            Assert.Empty(PipelineCallContracts.Check(fromTheRun, contract));

            // In the problems, a warning on the call's Input; a call to a pipeline whose contract is not known is not judged.
            var graph = new PipelineGraph { Steps = { Call("{}") } };
            var problem = Assert.Single(PipelineGraphProblems.Find(graph, Array.Empty<StepCatalogEntry>(), new Dictionary<string, PipelineCallContract> { ["child"] = contract }), found => found.Code == PipelineGraphProblems.BadCall);
            Assert.Equal(("call", "Input", PipelineGraphProblems.Warning), (problem.StepId, problem.Input, problem.Severity));
            Assert.DoesNotContain(PipelineGraphProblems.Find(graph, Array.Empty<StepCatalogEntry>(), new Dictionary<string, PipelineCallContract>()), found => found.Code == PipelineGraphProblems.BadCall);
        }

        [Fact]
        public async Task The_pipelines_that_call_one_are_listed_by_name_and_step()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<Stomme.Engine.Pipelines.Management.IPipelineDefinitionPublisher>();
            async Task<string> SaveAsync(string name, PipelineGraph graph)
            {
                var body = Stomme.Modules.Pipelines.Application.PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<Stomme.Engine.Pipelines.IStepRegistry>(), services.GetRequiredService<Stomme.Engine.Pipelines.IIdentityGenerator>());
                var definition = await publisher.NewAsync(services.GetRequiredService<Stomme.Engine.Pipelines.IStepSerializer>().Deserialize(body));
                definition.Name = name;
                await publisher.SaveDraftAsync(definition);
                return definition.DefinitionId;
            }

            var child = await SaveAsync("Child", Called());
            PipelineGraphStep Calling(string id, string type) => new() { Id = id, Name = "Run it", Type = type, Inputs = { ["PipelineDefinitionId"] = child } };
            await SaveAsync("Waits for it", new PipelineGraph { Steps = { Calling("a", "Stomme.ExecutePipeline") } });
            await SaveAsync("Sends it off", new PipelineGraph { Steps = { Calling("b", "Stomme.DispatchPipeline") } });
            await SaveAsync("Calls another", new PipelineGraph { Steps = { new PipelineGraphStep { Id = "c", Type = "Stomme.ExecutePipeline", Inputs = { ["PipelineDefinitionId"] = "someone-else" } } } });

            var callers = await new Stomme.Modules.Pipelines.Application.ListCallersQueryHandler(services.GetRequiredService<Stomme.Engine.Pipelines.Management.IPipelineDefinitionStore>())
                .Handle(new Stomme.Modules.Pipelines.Application.ListCallersQuery(child), default);
            Assert.Equal(new[] { "Sends it off/b/False", "Waits for it/a/True" }, callers.Select(caller => $"{caller.Name}/{caller.StepId}/{caller.Waits}"));
        }
    }
}
