#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Reliability;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A step's own reliability, run by the ENGINE through the module's step middleware: a failed step is
    /// tried again as often as it says, and - when it says so - the pipeline goes on past it when it still fails, the failure
    /// in the run's log; without either, a failure faults the run as before. Kept on the step through the codec.
    /// </summary>
    public sealed class PipelineReliabilityTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly Attempts _attempts = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddSingleton(_attempts);
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<Flaky>();
                module.UseStepReliability();
            });
            services.AddExpressionDescriptorProvider<Stomme.Modules.Pipelines.Expressions.TemplateExpressionDescriptorProvider>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        public sealed class Attempts
        {
            public int FailFirst { get; set; }

            public int Tried { get; set; }
        }

        /// <summary>Fails its first <see cref="Attempts.FailFirst"/> tries.</summary>
        [Step("Stomme.Tests", "Tests", "Fails its first tries.")]
        public sealed class Flaky : CodeStep
        {
            protected override void Execute(StepExecutionContext context)
            {
                var attempts = context.GetRequiredService<Attempts>();
                attempts.Tried++;
                if (attempts.Tried <= attempts.FailFirst)
                {
                    throw new InvalidOperationException("flaky try " + attempts.Tried);
                }
            }
        }

        private static PipelineGraph Graph(int retries, bool continueOnError) => new()
        {
            Steps =
            {
                new PipelineGraphStep { Id = "flaky", Type = "Stomme.Tests.Flaky", Retries = retries, RetryDelaySeconds = 0, ContinueOnError = continueOnError },
                new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "after the flaky step" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "flaky", TargetId = "after" } },
        };

        private async Task<(Stomme.Engine.Pipelines.Models.RunPipelineResult Result, string Body)> RunAsync(PipelineGraph graph, int failFirst)
        {
            _attempts.FailFirst = failFirst;
            _attempts.Tried = 0;
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            return (result, body);
        }

        private static string[] Logged(Stomme.Engine.Pipelines.Models.RunPipelineResult result) =>
            result.Journal.PipelineExecutionLogEntries.Select(entry => entry.EventName + ": " + entry.Message).ToArray();

        [Fact]
        public async Task A_failed_step_is_tried_again_as_often_as_it_says_and_the_run_goes_on()
        {
            var (result, body) = await RunAsync(Graph(retries: 2, continueOnError: false), failFirst: 2);

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.NotEqual(PipelineSubStatus.Faulted, result.PipelineState.SubStatus);
            Assert.Equal(3, _attempts.Tried);
            Assert.Contains("after the flaky step", _console.ToString());
            Assert.Equal(2, Logged(result).Count(line => line.StartsWith("Retrying: Try ", StringComparison.Ordinal)));

            // The settings come back from the stored body as they were written.
            var back = PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "flaky");
            Assert.Equal((2, 0, false), (back.Retries, back.RetryDelaySeconds, back.ContinueOnError));
        }

        // A step switched off (n8n's deactivate) is skipped - it does nothing, the run's log says so - and the run goes on through
        // its first outcome: a Decision's True. A trigger switched off starts nothing. It comes back from the body as written.
        [Fact]
        public async Task A_step_switched_off_is_skipped_and_the_run_goes_on_through_its_first_outcome()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "flaky", Type = "Stomme.Tests.Flaky", Disabled = true },
                    new PipelineGraphStep { Id = "check", Type = "Stomme.FlowDecision", Inputs = { ["Condition"] = "false" }, Disabled = true },
                    new PipelineGraphStep { Id = "yes", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "through True" } },
                    new PipelineGraphStep { Id = "no", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "through False" } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "flaky", TargetId = "check" },
                    new PipelineGraphConnection { SourceId = "check", SourcePort = "True", TargetId = "yes" },
                    new PipelineGraphConnection { SourceId = "check", SourcePort = "False", TargetId = "no" },
                },
            };

            var (result, body) = await RunAsync(graph, failFirst: 5);

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.NotEqual(PipelineSubStatus.Faulted, result.PipelineState.SubStatus);
            Assert.Equal(0, _attempts.Tried); // the flaky step never ran
            Assert.Contains("through True", _console.ToString()); // the Decision, off, went its first way though false
            Assert.DoesNotContain("through False", _console.ToString());
            Assert.Equal(2, Logged(result).Count(line => line.StartsWith("Skipped: ", StringComparison.Ordinal)));
            Assert.Equal(new[] { "flaky", "check" }, PipelineGraphCodec.ToGraph(body).Steps.Where(step => step.Disabled).Select(step => step.Id));

            // A trigger switched off is not a start: the engine is not told to index it.
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var schedule = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "every", Type = "Stomme.Schedule", Inputs = { ["Cron"] = "0 * * * *" }, Disabled = true } } };
            var stored = PipelineGraphCodec.ToBody(schedule, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            Assert.DoesNotContain("canStartPipeline", stored);
            schedule.Steps[0].Disabled = false;
            Assert.Contains("canStartPipeline", PipelineGraphCodec.ToBody(schedule, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>()));
        }

        [Fact]
        public async Task Out_of_tries_a_step_faults_the_run_unless_it_says_to_go_on()
        {
            var (faulted, _) = await RunAsync(Graph(retries: 1, continueOnError: false), failFirst: 5);
            Assert.Equal(PipelineSubStatus.Faulted, faulted.PipelineState.SubStatus);
            Assert.Equal(2, _attempts.Tried);
            Assert.DoesNotContain("after the flaky step", _console.ToString());

            var (wentOn, _) = await RunAsync(Graph(retries: 1, continueOnError: true), failFirst: 5);
            Assert.Equal(PipelineStatus.Finished, wentOn.PipelineState.Status);
            Assert.NotEqual(PipelineSubStatus.Faulted, wentOn.PipelineState.SubStatus);
            Assert.Contains("after the flaky step", _console.ToString());
            Assert.Contains(Logged(wentOn), line => line.StartsWith("ContinuedAfterFailure: Failed: flaky try 2", StringComparison.Ordinal));

            // Without settings, a failure faults the run as it always has.
            var (plain, _) = await RunAsync(Graph(retries: 0, continueOnError: false), failFirst: 1);
            Assert.Equal(PipelineSubStatus.Faulted, plain.PipelineState.SubStatus);
            Assert.Equal(1, _attempts.Tried);
        }

        // n8n's "continue (using error output)": a step that still fails goes out through its Failed port - what is connected there
        // runs, what follows its Done does not - with its error saved to the variable it names, read by an expression. A step that
        // succeeds goes its usual way. The settings come back from the body; Failed path wins over going on.
        private static PipelineGraph FailedPathGraph() => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-failure", Name = "failure", TypeName = "Object" } },
            Steps =
            {
                new PipelineGraphStep { Id = "flaky", Type = "Stomme.Tests.Flaky", Name = "Fetch", Retries = 1, FailedPath = true, ContinueOnError = true, FailureVariable = "v-failure" },
                new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "after the flaky step" } },
                new PipelineGraphStep { Id = "handle", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "handled: {{ failure.error }} in {{ failure.step }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "flaky", TargetId = "after" },
                new PipelineGraphConnection { SourceId = "flaky", SourcePort = PipelineOutcomes.Failed, TargetId = "handle" },
            },
        };

        [Fact]
        public async Task A_step_that_still_fails_takes_its_Failed_path_with_its_error_saved()
        {
            var (failed, body) = await RunAsync(FailedPathGraph(), failFirst: 5);

            Assert.Equal(PipelineStatus.Finished, failed.PipelineState.Status);
            Assert.NotEqual(PipelineSubStatus.Faulted, failed.PipelineState.SubStatus);
            Assert.Equal(2, _attempts.Tried); // tried again first, as it says
            Assert.Contains("handled: flaky try 2 in Fetch", _console.ToString());
            Assert.DoesNotContain("after the flaky step", _console.ToString());
            Assert.Contains(Logged(failed), line => line.StartsWith("TookFailedPath: Failed: flaky try 2", StringComparison.Ordinal) && line.EndsWith("Its error is saved to 'failure'.", StringComparison.Ordinal));

            // Back from the stored body: the Failed path and the variable, and not "go on" - the Failed path wins.
            var back = PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "flaky");
            Assert.Equal((true, "v-failure", false), (back.FailedPath, back.FailureVariable, back.ContinueOnError));

            // Succeeding, it goes its usual way.
            var (succeeded, _) = await RunAsync(FailedPathGraph(), failFirst: 0);
            Assert.Equal(PipelineStatus.Finished, succeeded.PipelineState.Status);
            Assert.Contains("after the flaky step", _console.ToString());
            Assert.Equal(1, _console.ToString().Split("handled:").Length - 1); // the handler ran once, for the failure only
        }

        // The step's record says it failed though it completed, how, and why - what the canvas and the step test show; a step with
        // no settings has none of it.
        [Fact]
        public void A_handled_failure_is_read_back_from_the_steps_record()
        {
            Assert.Equal((PipelineStepRunView.TookFailedPath, "boom"), StepReliability.HandledFailure(new System.Collections.Generic.Dictionary<string, object>
            {
                [StepReliability.HandledFailureKey] = System.Text.Json.JsonSerializer.SerializeToElement(PipelineStepRunView.TookFailedPath),
                [StepReliability.FailureMessageKey] = "boom",
            }));
            Assert.Equal(((string?)null, (string?)null), StepReliability.HandledFailure(new System.Collections.Generic.Dictionary<string, object>()));
            Assert.Equal(((string?)null, (string?)null), StepReliability.HandledFailure(null));
            Assert.Equal((false, (string?)null), StepReliability.OnFailure(null));
        }

        [Fact]
        public void The_settings_are_bounded_wherever_they_come_from()
        {
            Assert.Equal((5, 60, true), StepReliability.Of(new System.Collections.Generic.Dictionary<string, object>
            {
                [StepReliability.RetriesKey] = 99,
                [StepReliability.RetryDelayKey] = "3600",
                [StepReliability.ContinueOnErrorKey] = true,
            }));
            Assert.Equal((0, 0, false), StepReliability.Of(null));

            // For two editors: one field each, structured.
            var graph = Graph(retries: 3, continueOnError: true);
            graph.Steps[0].RetryDelaySeconds = 5;
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("3/5", fields["step/flaky/retry"]);
            Assert.Equal("1", fields["step/flaky/continue-on-error"]);
            var back = PipelineGraphFields.ToGraph(fields, graph).Steps[0];
            Assert.Equal((3, 5, true), (back.Retries, back.RetryDelaySeconds, back.ContinueOnError));

            // The Failed path and the variable its error is saved to: one field each, structured.
            var failing = FailedPathGraph();
            var failingFields = PipelineGraphFields.ToFields(failing);
            Assert.Equal("1", failingFields["step/flaky/failed-path"]);
            Assert.Equal("v-failure", failingFields["step/flaky/failure-variable"]);
            Assert.Equal((true, "v-failure"), (PipelineGraphFields.ToGraph(failingFields, failing).Steps[0].FailedPath, PipelineGraphFields.ToGraph(failingFields, failing).Steps[0].FailureVariable));
            Assert.Equal("step 'Fetch', whether a failure takes its Failed path", PipelineGraphFields.Describe("step/flaky/failed-path", failing));
        }
    }
}
