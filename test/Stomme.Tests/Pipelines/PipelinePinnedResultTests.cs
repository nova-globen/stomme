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
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Pipelines;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's pinned data: the result an author pins for a step is kept with the pipeline, and a TEST hands it on in place
    /// of running the step - an API not called again, a model not asked again - its outputs and the variables they save to set
    /// as the step's own would be, and the run going on through its first outcome. The step tested itself runs (that is how its
    /// result is had again), and a published run never reads a pin. It can be real data, so an author alone is given it and a
    /// file never carries it.
    /// </summary>
    public sealed class PipelinePinnedResultTests : IAsyncLifetime
    {
        private const string Pinned = "[{\"name\":\"Ada\"},{\"name\":\"Grace\"}]";
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
                Stomme.Modules.Pipelines.Reliability.StepReliability.UseStepReliability(module); // as the host composes it
            });
            StepTestCommitGate.Wrap(services);
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

        private sealed class Guard : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private Task<StepTestResult> TestAsync(IServiceProvider services, PipelineGraph graph, string stepId, bool upToHere = false, bool whole = false) =>
            new TestStepCommandHandler(
                    services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(), services.GetRequiredService<IIdentityGenerator>(),
                    services.GetRequiredService<IStepSerializer>(), services.GetRequiredService<IPipelineRunner>(), services.GetRequiredService<IStepExecutionMapper>(), new Guard())
                .Handle(new TestStepCommand(graph, stepId, null, upToHere, whole), CancellationToken.None);

        // An HTTP request the egress refuses (it fails the run when it really runs), its result saved to a variable the next step
        // reads - and pinned.
        private static PipelineGraph Graph(string? pinned = Pinned) => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-people", Name = "people", TypeName = "Object" } },
            Steps =
            {
                new PipelineGraphStep { Id = "fetch", Type = "Stomme.SendHttpRequest", Inputs = { ["Url"] = "http://127.0.0.1:1/people" }, OutputVariables = { ["Result"] = "v-people" }, PinnedResult = pinned },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "Second: {{ people[1].name }}" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "fetch", SourcePort = "Done", TargetId = "say" } },
        };

        [Fact]
        public async Task A_test_hands_the_pinned_result_on_in_place_of_running_the_step()
        {
            using var scope = _services.CreateScope();
            var whole = await TestAsync(scope.ServiceProvider, Graph(), "say", whole: true);

            Assert.Equal("Finished", whole.Status); // the request the egress refuses was never sent
            Assert.Contains("Second: Grace", _console.ToString());
            Assert.Contains(whole.Log, line => line.Contains("pinned result is handed on", StringComparison.Ordinal));
            Assert.Equal("Completed", whole.Steps.Single(step => step.StepId == "fetch").Status);

            // Testing the step after it, up to here, hands the pin on too.
            var upTo = await TestAsync(scope.ServiceProvider, Graph(), "say", upToHere: true);
            Assert.Equal("Finished", upTo.Status);
        }

        // FOUND driving it: the pin reached the variable the step saves to, but its record showed no Result and a next step reading
        // "the previous step's result" read nothing - the engine keys a step's outputs by the name they are set under.
        [Fact]
        public async Task The_pinned_result_is_the_steps_recorded_result_and_the_next_steps_previous_result()
        {
            using var scope = _services.CreateScope();
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-last", Name = "last", TypeName = "Object" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "fetch", Type = "Stomme.SendHttpRequest", Inputs = { ["Url"] = "http://127.0.0.1:1/people" }, PinnedResult = Pinned },
                    new PipelineGraphStep { Id = "limit", Type = "Stomme.LimitRecords", Inputs = { ["MaxRecords"] = "1", ["Keep"] = "Last" }, OutputVariables = { ["Result"] = "v-last" } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "Last: {{ last[0].name }}" } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "fetch", SourcePort = "Done", TargetId = "limit" },
                    new PipelineGraphConnection { SourceId = "limit", SourcePort = "Done", TargetId = "say" },
                },
            };

            var whole = await TestAsync(scope.ServiceProvider, graph, "say", whole: true);
            Assert.Equal("Finished", whole.Status);
            Assert.Contains("Last: Grace", _console.ToString()); // Limit records read the pin as the previous step's result
            Assert.Contains("Grace", whole.Steps.Single(step => step.StepId == "fetch").OutputsJson); // and its record shows it
        }

        [Fact]
        public async Task The_step_tested_itself_runs_and_so_does_one_with_nothing_pinned()
        {
            using var scope = _services.CreateScope();
            var itself = await TestAsync(scope.ServiceProvider, Graph(), "fetch");
            Assert.Equal("Faulted", itself.Status);
            Assert.Contains("'127.0.0.1' is not permitted", itself.Error); // it really ran: the egress refused it

            var unpinned = await TestAsync(scope.ServiceProvider, Graph(pinned: null), "say", whole: true);
            Assert.Equal("Faulted", unpinned.Status);
        }

        [Fact]
        public async Task A_run_that_is_no_test_runs_the_step_even_marked_pinned()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var registry = services.GetRequiredService<IStepRegistry>();
            var serializer = services.GetRequiredService<IStepSerializer>();
            var body = PipelineGraphJudge.Body(Graph(), null, registry, services.GetRequiredService<IIdentityGenerator>(), serializer);
            Assert.DoesNotContain(Stomme.Modules.Pipelines.Reliability.StepReliability.PinnedKey, body); // the codec never puts a pin on a step

            var marked = TestStepCommandHandler.MarkPinned(body, new Dictionary<string, string> { ["fetch"] = Pinned });
            Assert.Contains(Stomme.Modules.Pipelines.Reliability.StepReliability.PinnedKey, marked);
            var run = await services.GetRequiredService<IPipelineRunner>().RunAsync(serializer.Deserialize(marked));
            Assert.Equal(Stomme.Engine.Pipelines.PipelineSubStatus.Faulted, run.PipelineState.SubStatus); // no test: the step ran
        }

        [Fact]
        public void A_pin_is_kept_with_the_pipeline_for_an_author_and_never_leaves_in_a_file()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(Graph(), null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            Assert.Equal(Pinned, PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "fetch").PinnedResult);

            // Anyone but an author: neither in the body nor in the graph.
            Assert.Null(PipelineGraphCodec.ToGraph(PipelineGraphCodec.WithoutTestValues(body)).Steps.Single(step => step.Id == "fetch").PinnedResult);
            Assert.Null(PipelineGraphCodec.WithoutTestValues(Graph())!.Steps.Single(step => step.Id == "fetch").PinnedResult);

            // Two editors: a field of its own, merged as text.
            var fields = PipelineGraphFields.ToFields(Graph());
            Assert.Equal(Pinned, fields["step/fetch/pinned"]);
            Assert.Equal(Pinned, PipelineGraphFields.ToGraph(fields).Steps.Single(step => step.Id == "fetch").PinnedResult);

            // A file: written without it, and read without it whatever wrote it.
            var graph = Graph();
            var file = PipelineExchange.Write("People", null, graph);
            Assert.DoesNotContain("Grace", file);
            Assert.Equal(Pinned, graph.Steps[0].PinnedResult); // the pipeline's own step untouched
            var planted = file.Replace("\"id\": \"fetch\",", "\"id\": \"fetch\", \"pinnedResult\": \"[1]\",", StringComparison.Ordinal);
            Assert.Contains("pinnedResult", planted);
            Assert.Null(PipelineExchange.Read(planted).Graph!.Steps.Single(step => step.Id == "fetch").PinnedResult);
        }
    }
}
