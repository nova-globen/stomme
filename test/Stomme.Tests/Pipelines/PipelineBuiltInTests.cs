#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;
using RunPipelineResult = Stomme.Engine.Pipelines.Models.RunPipelineResult;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - the built-in values every expression and condition can read beside the pipeline's variables (n8n's $now,
    /// $today, $execution and $workflow): <c>{{ $now }}</c>, <c>{{ $today }}</c>, <c>{{ $run.id }}</c>,
    /// <c>{{ $pipeline.name }}</c>. ONE list (<see cref="PipelineBuiltIns"/>), filled by the engine from the run and its clock
    /// when a step runs, and by the editor for its previews; the problems panel knows them, so naming one is no "unknown
    /// variable"; a name after a <c>$</c> that is not one still is.
    /// </summary>
    public sealed class PipelineBuiltInTests : IAsyncLifetime
    {
        private static readonly DateTimeOffset Moment = new(2026, 10, 6, 9, 30, 15, TimeSpan.FromHours(2));

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
                module.Configure<Stomme.Engine.KeyValues.Features.KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<Stomme.Modules.Pipelines.Application.PipelineSiteVariablesCache>();
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddExpressionDescriptorProvider<ConditionExpressionDescriptorProvider>();
            services.AddSingleton<ISystemClock>(new FixedClock(Moment));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class FixedClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow => now.ToUniversalTime();
        }

        private static (bool Found, object? Value) NoVariable(string name) => (false, null);

        private static readonly PipelineRunFacts Facts = new(Moment, "run-7", "order-42", Moment.AddMinutes(-5), "def-1", "Nightly orders", 3);

        [Fact]
        public void The_built_ins_read_the_moment_the_run_and_the_pipeline_and_leave_other_names_to_the_variables()
        {
            var lookup = PipelineBuiltIns.With(name => name == "who" ? (true, "Ada") : (false, null), Facts);
            Assert.Equal("2026-10-06T07:30:15Z", PipelineTemplate.Render("{{ $now }}", lookup));
            Assert.Equal("2026-10-06", PipelineTemplate.Render("{{ $today }}", lookup));
            Assert.Equal("run-7 / order-42 / 2026-10-06T07:25:15Z", PipelineTemplate.Render("{{ $run.id }} / {{ $run.correlationId }} / {{ $run.startedAt }}", lookup));
            Assert.Equal("Nightly orders v3 (def-1) for Ada", PipelineTemplate.Render("{{ $pipeline.name }} v{{ $pipeline.version }} ({{ $pipeline.id }}) for {{ who }}", lookup));
            Assert.Equal(3L, PipelineTemplate.Render("{{ $pipeline.version }}", lookup)); // one placeholder is the value itself

            var error = Assert.Throws<PipelineTemplateException>(() => PipelineTemplate.Render("{{ $tomorrow }}", lookup));
            Assert.Contains("'$tomorrow'", error.Message);

            // A condition compares $today as the date it is.
            var after = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ $today }}", Operator = "greater", Right = "2026-09-30" } } };
            Assert.True(PipelineConditions.Evaluate(after, lookup));

            // What the editor suggests after "{{ $r": the run's fields.
            var suggested = PipelineTemplate.Suggest("{{ $r", PipelineBuiltIns.Values(Facts), max: 24);
            Assert.Contains("{{ $run.id }}", suggested);
            Assert.Contains("{{ $run.correlationId }}", suggested);
        }

        // BL-229 - n8n's workflow timezone: $now with the zone's offset, $today the zone's date - a day later in Stockholm at
        // 23:30 UTC - and $run.startedAt there too; a condition still compares them as moments. Empty, "UTC" and a zone this
        // machine does not know read UTC, the last one refused where a pipeline is saved.
        [Fact]
        public void The_built_ins_read_time_in_the_pipelines_zone()
        {
            var late = new DateTimeOffset(2026, 10, 6, 23, 30, 0, TimeSpan.Zero);
            var lookup = PipelineBuiltIns.With(NoVariable, Facts with { Now = late, StartedAt = late.AddMinutes(-5), TimeZone = "Europe/Stockholm" });
            Assert.Equal("2026-10-07T01:30:00+02:00", PipelineTemplate.Render("{{ $now }}", lookup));
            Assert.Equal("2026-10-07", PipelineTemplate.Render("{{ $today }}", lookup));
            Assert.Equal("2026-10-07T01:25:00+02:00", PipelineTemplate.Render("{{ $run.startedAt }}", lookup));
            var after = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ $now }}", Operator = "greater", Right = "2026-10-06T23:00:00Z" } } };
            Assert.True(PipelineConditions.Evaluate(after, lookup)); // 01:30+02:00 is 23:30Z

            Assert.Equal("2026-10-06T23:30:00Z", PipelineTemplate.Render("{{ $now }}", PipelineBuiltIns.With(NoVariable, Facts with { Now = late, TimeZone = " utc " })));
            Assert.Null(PipelineTimeZone.Normalize("Etc/UTC"));
            Assert.Equal("Asia/Tehran", PipelineTimeZone.Normalize(" Asia/Tehran "));
            Assert.Same(TimeZoneInfo.Utc, PipelineTimeZone.Find("Mars/Olympus_Mons"));
            Assert.Equal("'Mars/Olympus_Mons' is not a time zone this server knows - use an IANA name such as Europe/Stockholm, or leave it empty for UTC.", PipelineTimeZone.Problem("Mars/Olympus_Mons"));
            Assert.Null(PipelineTimeZone.Problem(""));
            Assert.Null(PipelineTimeZone.Problem("Europe/Stockholm"));
        }

        // Through the engine: the run reads $today and $now in the zone of the version it runs; a zone the server does not know
        // is refused when the graph is saved; the zone is a field of its own and travels in a file.
        [Fact]
        public async Task A_run_reads_time_in_its_pipelines_zone_and_an_unknown_zone_is_refused()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "zoned {{ $today }} at {{ $now }}" } } },
                TimeZone = "America/New_York",
            };
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            Assert.Equal("America/New_York", PipelineGraphCodec.ToGraph(body).TimeZone);
            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains("zoned 2026-10-06 at 2026-10-06T03:30:15-04:00", _console.ToString()); // 07:30:15Z in New York (EDT)

            graph.TimeZone = "Mars/Olympus_Mons";
            var refused = Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>()));
            Assert.StartsWith("'Mars/Olympus_Mons' is not a time zone this server knows", refused.Message);

            graph.TimeZone = "Asia/Tehran";
            var fields = PipelineGraphFields.ToFields(graph);
            Assert.Equal("Asia/Tehran", fields["pipeline/time-zone"]);
            Assert.Equal("Asia/Tehran", PipelineGraphFields.ToGraph(fields, graph).TimeZone);
            Assert.Equal("the time zone the pipeline reads time in", PipelineGraphFields.Describe("pipeline/time-zone", graph));
            Assert.Equal("Asia/Tehran", PipelineExchange.Read(PipelineExchange.Write("Zoned", null, graph)).Graph!.TimeZone);
        }

        // BL-229 - n8n's $vars: the site's variables, read by every expression; a name is a letter or '_' then letters, digits or '_'.
        [Fact]
        public void The_sites_variables_are_read_as_vars_and_only_names_that_can_be_read_are_kept()
        {
            var lookup = PipelineBuiltIns.With(NoVariable, Facts with { SiteVariables = new Dictionary<string, string> { ["apiBase"] = "https://api.example.com", ["team"] = "ops" } });
            Assert.Equal("https://api.example.com/orders for OPS", PipelineTemplate.Render("{{ $vars.apiBase }}/orders for {{ $vars.team | upper }}", lookup));
            Assert.Contains("{{ $vars.apiBase }}", PipelineTemplate.Suggest("{{ $v", PipelineBuiltIns.Values(Facts with { SiteVariables = new Dictionary<string, string> { ["apiBase"] = "x" } }), max: 24));
            Assert.True(PipelineBuiltIns.IsBuiltIn("$vars"));

            Assert.True(PipelineSiteVariables.Check(new Dictionary<string, string> { ["apiBase"] = "x", ["_team2"] = "y" }, out var error));
            Assert.Null(error);
            Assert.False(PipelineSiteVariables.Check(new Dictionary<string, string> { ["api base"] = "x" }, out error));
            Assert.Equal("'api base' cannot be a variable's name: a letter or '_', then letters, digits or '_', at most 50.", error);
            Assert.False(PipelineSiteVariables.Check(new Dictionary<string, string> { ["2fa"] = "x" }, out _));
            Assert.False(PipelineSiteVariables.Check(new Dictionary<string, string> { ["long"] = new string('x', PipelineSiteVariables.MaxValueLength + 1) }, out error));
            Assert.Equal("'long' is over 1000 characters.", error);
        }

        [Fact]
        public async Task A_step_reads_the_sites_variables_from_the_store()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var keyValues = services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>();
            var tenant = services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>().TenantId;
            await Stomme.Modules.Pipelines.Application.PipelineSiteVariableStore.SaveAsync(keyValues, tenant,
                new Dictionary<string, string> { ["apiBase"] = "https://api.example.com", ["team"] = "ops" }, default);
            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "{{ $vars.apiBase }}/orders for {{ $vars.team | upper }}" } } },
            };
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains("https://api.example.com/orders for OPS", _console.ToString());
            await Assert.ThrowsAsync<ArgumentException>(() => Stomme.Modules.Pipelines.Application.PipelineSiteVariableStore.SaveAsync(keyValues, tenant,
                new Dictionary<string, string> { ["not a name"] = "x" }, default));
        }

        [Fact]
        public async Task A_step_reads_the_built_ins_from_its_run_and_the_engines_clock()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "run {{ $run.id }} on {{ $today }} at {{ $now }}" } } },
            };
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            RunPipelineResult result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains($"run {result.PipelineState.Id} on 2026-10-06 at 2026-10-06T07:30:15Z", _console.ToString());
        }

        // BL-229 - n8n's "When executed by another workflow": what the run was started with is $input - a value that is JSON read
        // by its path - and the engine's own __ keys are not part of it.
        [Fact]
        public async Task A_step_reads_what_its_run_was_started_with_as_input()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var graph = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "order {{ $input.order.id }} for {{ $input.order.customer }}, {{ $input.note }}" } } },
            };
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var input = new Dictionary<string, object> { ["order"] = "{\"id\": 42, \"customer\": \"Ada\"}", ["note"] = "rush", ["__EvaluationId"] = "internal" };
            var result = await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body),
                new Stomme.Engine.Pipelines.Options.RunPipelineOptions { Input = input });

            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Contains("order 42 for Ada, rush", _console.ToString());
            Assert.Equal(new[] { "note", "order" }, PipelineBuiltIns.InputOf(input.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value))).Keys.OrderBy(key => key));
            var catalog = new List<StepCatalogEntry> { new() { Type = "Stomme.WriteLine", DisplayName = "Write Line", Kind = "Action", Inputs = { new() { Name = "Text", DisplayName = "Text", TypeName = "String" } } } };
            Assert.DoesNotContain(PipelineGraphProblems.Find(graph, catalog), problem => problem.Code == PipelineGraphProblems.UnknownVariable);
        }

        [Fact]
        public async Task Filter_records_compares_a_records_field_with_a_built_in()
        {
            using var scope = _services.CreateScope();
            var condition = PipelineConditions.Write(new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ due }}", Operator = "less", Right = "{{ $today }}" } } });
            var filter = new FilterRecords { Records = new("[{\"id\":\"1\",\"due\":\"2026-10-01\"},{\"id\":\"2\",\"due\":\"2026-10-09\"}]"), Condition = new(condition) };
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(filter);

            Assert.Equal(PipelineSubStatus.Finished, run.PipelineState.SubStatus);
            var kept = (string)run.Journal.StepExecutionContexts.Single(c => c.Step is FilterRecords).GetOutputs()["Result"];
            Assert.Equal("[{\"id\":\"1\",\"due\":\"2026-10-01\"}]", kept);
        }

        [Fact]
        public void The_problems_panel_knows_the_built_ins_and_still_names_an_unknown_one()
        {
            var catalog = new List<StepCatalogEntry>
            {
                new() { Type = "Stomme.WriteLine", DisplayName = "Write Line", Kind = "Action", Inputs = { new() { Name = "Text", DisplayName = "Text", TypeName = "String" } } },
                new() { Type = "Stomme.If", DisplayName = "If", Kind = "Action", Inputs = { new() { Name = "Condition", DisplayName = "Condition", TypeName = "Boolean" } } },
            };
            var say = new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "{{ $pipeline.name }} at {{ $now }}" } };
            var test = new PipelineGraphStep
            {
                Id = "test", Type = "Stomme.If",
                InputConditions = { ["Condition"] = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ $today }}", Operator = "equals", Right = "{{ $run.startedAt }}" } } } },
            };
            var graph = new PipelineGraph { Steps = { say, test }, Connections = { new PipelineGraphConnection { SourceId = "say", TargetId = "test" } } };
            Assert.DoesNotContain(PipelineGraphProblems.Find(graph, catalog), problem => problem.Code == PipelineGraphProblems.UnknownVariable);

            say.InputExpressions["Text"] = "{{ $yesterday }}";
            var unknown = Assert.Single(PipelineGraphProblems.Find(graph, catalog), problem => problem.Code == PipelineGraphProblems.UnknownVariable);
            Assert.Contains("'$yesterday'", unknown.Message);

            // A filter that is not one (PipelineTemplateFilters) is an error before publish, in an expression and in a condition.
            say.InputExpressions["Text"] = "{{ $now | shout }}";
            test.InputConditions["Condition"].Rules[0].Right = "{{ $run.startedAt | date: \"yyyy-MM-dd\" | yell }}";
            var filters = PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.Message.Contains("is not a filter")).ToList();
            Assert.Equal(new[] { ("say", PipelineGraphProblems.BadValue), ("test", PipelineGraphProblems.BadCondition) }, filters.Select(problem => (problem.StepId, problem.Code)));
            Assert.Contains("'shout' is not a filter", filters[0].Message);
        }
    }
}
