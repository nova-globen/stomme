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
    /// BL-229 increment 6 - test ONE step: it runs on its own in the engine (with what its bodies hold, cut from the steps around
    /// it), with the variables holding the values given, and comes back as a run's steps are shown - data for an author only.
    /// Nothing is stored.
    /// </summary>
    public sealed class PipelineStepTestTests : IAsyncLifetime
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
                Stomme.Modules.Pipelines.Reliability.StepReliability.UseStepReliability(module); // a switched-off step is skipped, as in the host
            });
            StepTestCommitGate.Wrap(services); // as the host composes it
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

        private sealed class Guard(bool author) : IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) =>
                Task.FromResult(permission == PipelinesPermissions.Manage ? author : true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private Task<StepTestResult> TestAsync(IServiceProvider services, PipelineGraph graph, string stepId, Dictionary<string, string>? values = null, bool author = true, bool upToHere = false, bool whole = false) =>
            new TestStepCommandHandler(
                    services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IRegistriesPopulator>(), services.GetRequiredService<IIdentityGenerator>(),
                    services.GetRequiredService<IStepSerializer>(), services.GetRequiredService<IPipelineRunner>(), services.GetRequiredService<IStepExecutionMapper>(), new Guard(author))
                .Handle(new TestStepCommand(graph, stepId, values, upToHere, whole), CancellationToken.None);

        // A chain whose middle step is tested: parse -> say -> log, a variable the parse saves to and the say reads, and a loop.
        private static PipelineGraph Graph() => new()
        {
            Variables =
            {
                new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "nobody yet" },
                new PipelineGraphVariable { Id = "v-n", Name = "n", TypeName = "Int32", Value = "1" },
            },
            Steps =
            {
                new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Inputs = { ["Text"] = "name\nAda\n" } },
                new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "Hello {{ who }}" } },
                new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Inputs = { ["Message"] = "logged by the test" } },
                new PipelineGraphStep { Id = "loop", Type = "Stomme.For", Inputs = { ["Start"] = "1", ["End"] = "3" } },
                new PipelineGraphStep { Id = "inside", Type = "Stomme.WriteLine", Parent = "loop", ParentPort = "Body", Inputs = { ["Text"] = "pass" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "say" },
                new PipelineGraphConnection { SourceId = "say", TargetId = "log" },
            },
        };

        [Fact]
        public async Task A_step_runs_alone_with_the_values_given_and_shows_what_it_received()
        {
            using var scope = _services.CreateScope();
            var result = await TestAsync(scope.ServiceProvider, Graph(), "say", new Dictionary<string, string> { ["who"] = "Ada" });

            Assert.Equal("Finished", result.Status);
            var said = Assert.Single(result.Steps);
            Assert.Equal(("say", "Completed"), (said.StepId, said.Status));
            Assert.Contains("Hello Ada", said.InputsJson);
            Assert.Contains("Hello Ada", _console.ToString());
            // Only the step tested ran - not the parse before it, not the log after it.
            Assert.DoesNotContain("logged by the test", string.Join("|", result.Log));

            // A variable not given keeps its starting value.
            var plain = await TestAsync(scope.ServiceProvider, Graph(), "say");
            Assert.Contains("Hello nobody yet", Assert.Single(plain.Steps).InputsJson);
        }

        [Fact]
        public async Task A_viewer_sees_how_it_ended_but_no_data()
        {
            using var scope = _services.CreateScope();
            var result = await TestAsync(scope.ServiceProvider, Graph(), "say", new Dictionary<string, string> { ["who"] = "Ada" }, author: false);

            Assert.Equal("Finished", result.Status);
            var said = Assert.Single(result.Steps);
            Assert.Null(said.InputsJson);
            Assert.Null(said.OutputsJson);
        }

        [Fact]
        public async Task A_loop_runs_its_body_and_a_step_inside_a_body_runs_alone()
        {
            using var scope = _services.CreateScope();
            var loop = await TestAsync(scope.ServiceProvider, Graph(), "loop");
            Assert.Equal("Finished", loop.Status);
            Assert.Equal(3, loop.Steps.Count(step => step.StepId == "inside"));

            var inside = await TestAsync(scope.ServiceProvider, Graph(), "inside");
            Assert.Equal("Finished", inside.Status);
            Assert.Equal("inside", Assert.Single(inside.Steps).StepId);
        }

        [Fact]
        public async Task The_log_comes_back_a_fault_says_why_and_what_cannot_run_is_refused()
        {
            using var scope = _services.CreateScope();
            var logged = await TestAsync(scope.ServiceProvider, Graph(), "log");
            Assert.Contains(logged.Log, line => line.Contains("logged by the test", StringComparison.Ordinal));

            var broken = Graph();
            broken.Steps.Single(step => step.Id == "say").InputExpressions["Text"] = "Hello {{ nobody }}";
            var faulted = await TestAsync(scope.ServiceProvider, broken, "say");
            Assert.Equal("Faulted", faulted.Status);
            Assert.Contains("There is no variable named 'nobody'.", faulted.Error);

            Assert.Equal("Refused", (await TestAsync(scope.ServiceProvider, Graph(), "no-such-step")).Status);
            var refused = await TestAsync(scope.ServiceProvider, Graph(), "say", new Dictionary<string, string> { ["n"] = "many" });
            Assert.Equal("Refused", refused.Status);
            Assert.Contains("not a whole number", refused.Error);
        }

        // BL-229 (n8n tests a step with what leads to it): up to here runs every step that leads to the one tested, in its own
        // flowchart, then it - not the steps after it, not the ones beside; a step before it switched off stays off; one before
        // it failing stops the test before it; a step with nothing before it runs alone, as Test step does.
        [Fact]
        public async Task Up_to_here_runs_what_leads_to_the_step_first_and_nothing_else()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;

            var said = await TestAsync(services, Graph(), "say", new Dictionary<string, string> { ["who"] = "Ada" }, upToHere: true);
            Assert.Equal("Finished", said.Status);
            Assert.Equal(new[] { "parse", "say" }, said.Steps.Select(step => step.StepId));
            Assert.DoesNotContain("logged by the test", string.Join("|", said.Log)); // the step after it did not run
            Assert.DoesNotContain(said.Steps, step => step.StepId is "loop" or "inside"); // nor the ones beside it

            var logged = await TestAsync(services, Graph(), "log", upToHere: true);
            Assert.Equal(new[] { "parse", "say", "log" }, logged.Steps.Select(step => step.StepId));

            // A step before it switched off is skipped, as a run would skip it; the step tested runs whatever its switch says.
            var off = Graph();
            off.Steps.Single(step => step.Id == "parse").Disabled = true;
            off.Steps.Single(step => step.Id == "say").Disabled = true;
            var skipped = await TestAsync(services, off, "say", upToHere: true);
            Assert.Contains(skipped.Log, line => line.Contains("switched off", StringComparison.Ordinal));
            Assert.Contains("Hello nobody yet", skipped.Steps.Single(step => step.StepId == "say").InputsJson);

            // One before it failing stops the test there.
            var broken = Graph();
            broken.Steps.Single(step => step.Id == "say").InputExpressions["Text"] = "Hello {{ nobody }}";
            var stopped = await TestAsync(services, broken, "log", upToHere: true);
            Assert.Equal("Faulted", stopped.Status);
            Assert.DoesNotContain(stopped.Steps, step => step.StepId == "log");

            // Nothing before it: the step alone, as Test step runs it; a step in a body is tested in its body's flowchart.
            Assert.Equal(new[] { "parse" }, (await TestAsync(services, Graph(), "parse", upToHere: true)).Steps.Select(step => step.StepId));
            Assert.Equal(new[] { "inside" }, (await TestAsync(services, Graph(), "inside", upToHere: true)).Steps.Select(step => step.StepId));
        }

        // BL-229 (n8n's "Test workflow"): the whole pipeline as it stands goes as a run would - its chain in order, with the
        // values given, the log written - a switched-off step skipped as a run skips it, and no step at all refused.
        [Fact]
        public async Task The_whole_pipeline_runs_as_a_run_would_with_the_values_given()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;

            var whole = await TestAsync(services, Graph(), "", new Dictionary<string, string> { ["who"] = "Ada" }, whole: true);
            Assert.Equal("Finished", whole.Status);
            Assert.Equal(new[] { "parse", "say", "log" }, whole.Steps.Select(step => step.StepId).Where(id => id is "parse" or "say" or "log"));
            Assert.Contains("Hello Ada", whole.Steps.Single(step => step.StepId == "say").InputsJson);
            Assert.Contains(whole.Log, line => line.Contains("logged by the test", StringComparison.Ordinal));

            var off = Graph();
            off.Steps.Single(step => step.Id == "say").Disabled = true;
            var skipped = await TestAsync(services, off, "", whole: true);
            Assert.Contains(skipped.Log, line => line.Contains("switched off", StringComparison.Ordinal));
            Assert.Contains(skipped.Log, line => line.Contains("logged by the test", StringComparison.Ordinal)); // and on past it

            var nothing = await TestAsync(services, new PipelineGraph(), "", whole: true);
            Assert.Equal(("Refused", "The pipeline has no steps to test."), (nothing.Status, nothing.Error));
        }

        // Nothing a step test runs is stored: no run in the site's list (measured on a scratch host before the gate - every test
        // left a run of definition "1" in All runs), no log record - while a run of the engine's own still commits.
        [Fact]
        public async Task A_step_test_stores_no_run_and_no_log_while_a_real_run_still_does()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            await TestAsync(services, Graph(), "say", new Dictionary<string, string> { ["who"] = "Ada" });
            await TestAsync(services, Graph(), "loop");
            await TestAsync(services, Graph(), "", whole: true); // the whole pipeline as it stands, too
            var broken = Graph();
            broken.Steps.Single(step => step.Id == "say").InputExpressions["Text"] = "Hello {{ nobody }}";
            await TestAsync(services, broken, "say");

            var instances = services.GetRequiredService<Stomme.Engine.Pipelines.Management.IPipelineInstanceStore>();
            Assert.Equal(0, await instances.CountAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineInstanceFilter()));
            Assert.Null(await services.GetRequiredService<Stomme.Engine.Pipelines.Runtime.IPipelineExecutionLogStore>()
                .FindAsync(new Stomme.Engine.Pipelines.Runtime.Filters.PipelineExecutionLogRecordFilter()));

            await services.GetRequiredService<IPipelineRunner>().RunAsync(new Stomme.Engine.Pipelines.Steps.WriteLine("a run of the engine's own"));
            Assert.Equal(1, await instances.CountAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineInstanceFilter()));
        }
    }
}
