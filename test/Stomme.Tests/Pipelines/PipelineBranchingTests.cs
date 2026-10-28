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
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 increment 10 - branching an author builds without code, run by the ENGINE: a Decision, an If and a While test a
    /// condition of rules (the module's <c>Condition</c> kind); a Switch (flow) goes out through the case that matches, or Default;
    /// a Fork (flow) goes out through every branch it names. Each comes back from the engine's document as written.
    /// </summary>
    public sealed class PipelineBranchingTests : IAsyncLifetime
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

        private async Task<(string Body, string[] Written, PipelineStatus Status)> RunAsync(PipelineGraph graph)
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            var serializer = scope.ServiceProvider.GetRequiredService<IStepSerializer>();
            var body = PipelineGraphJudge.Body(graph, null, registry, scope.ServiceProvider.GetRequiredService<IIdentityGenerator>(), serializer);
            _console.GetStringBuilder().Clear();
            var result = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(serializer.Deserialize(body));
            return (body, _console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), result.PipelineState.Status);
        }

        private static PipelineCondition Rule(string left, string op, string right = "") =>
            new() { Rules = { new PipelineConditionRule { Left = left, Operator = op, Right = right } } };

        private static PipelineGraphStep Say(string id, string text, string? parent = null, string? port = null) =>
            new() { Id = id, Type = "Stomme.WriteLine", Inputs = { ["Text"] = text }, Parent = parent, ParentPort = port };

        [Theory]
        [InlineData("250", "big")]
        [InlineData("90", "small")]
        public async Task A_decision_tests_its_rules_against_the_variables(string total, string expected)
        {
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-total", Name = "total", TypeName = "String", Value = total } },
                Steps =
                {
                    new PipelineGraphStep { Id = "check", Type = "Stomme.FlowDecision", InputConditions = { ["Condition"] = Rule("{{ total }}", "greater", "100") } },
                    Say("big", "big"),
                    Say("small", "small"),
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "check", SourcePort = "True", TargetId = "big" },
                    new PipelineGraphConnection { SourceId = "check", SourcePort = "False", TargetId = "small" },
                },
            };

            var (body, written, status) = await RunAsync(graph);

            Assert.Equal(PipelineStatus.Finished, status);
            Assert.Equal(new[] { expected }, written);
            var back = PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "check");
            Assert.Equal(PipelineConditions.Write(graph.Steps[0].InputConditions["Condition"]), PipelineConditions.Write(back.InputConditions["Condition"]));
            Assert.Empty(back.Inputs);
        }

        [Fact]
        public async Task An_if_runs_the_branch_its_condition_names()
        {
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-status", Name = "status", TypeName = "String", Value = "shipped" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "if", Type = "Stomme.If", InputConditions = { ["Condition"] = new PipelineCondition { IgnoreCase = true, Rules = { new PipelineConditionRule { Left = "{{ status }}", Operator = "equals", Right = "SHIPPED" } } } } },
                    Say("yes", "then", "if", "Then"),
                    Say("no", "else", "if", "Else"),
                },
            };

            var (_, written, status) = await RunAsync(graph);

            Assert.Equal(PipelineStatus.Finished, status);
            Assert.Equal(new[] { "then" }, written);
        }

        [Theory]
        [InlineData("gold", "first", "gold")]
        [InlineData("silver", "first", "silver")]
        [InlineData("tin", "first", "other")]
        public async Task A_switch_goes_out_through_the_case_that_matches_or_default(string tier, string mode, string expected)
        {
            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-tier", Name = "tier", TypeName = "String", Value = tier } },
                Steps =
                {
                    new PipelineGraphStep
                    {
                        Id = "switch", Type = "Stomme.FlowSwitch", Inputs = { ["Mode"] = mode == "first" ? "MatchFirst" : "MatchAny" },
                        Cases =
                        {
                            new PipelineGraphCase { Label = "Gold", Condition = Rule("{{ tier }}", "equals", "gold") },
                            new PipelineGraphCase { Label = "Silver", Condition = Rule("{{ tier }}", "equals", "silver") },
                        },
                    },
                    Say("gold", "gold"),
                    Say("silver", "silver"),
                    Say("other", "other"),
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "switch", SourcePort = "Gold", TargetId = "gold" },
                    new PipelineGraphConnection { SourceId = "switch", SourcePort = "Silver", TargetId = "silver" },
                    new PipelineGraphConnection { SourceId = "switch", SourcePort = "Default", TargetId = "other" },
                },
            };

            var (body, written, status) = await RunAsync(graph);

            Assert.True(status == PipelineStatus.Finished, $"{status}: {body}");
            Assert.Equal(new[] { expected }, written);
            var back = PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "switch");
            Assert.Equal(new[] { "Gold", "Silver" }, back.Cases.Select(@case => @case.Label));
            Assert.Equal("{{ tier }} is equal to silver", PipelineConditions.Describe(back.Cases[1].Condition));
        }

        [Fact]
        public async Task A_fork_goes_out_through_every_branch_it_names()
        {
            var graph = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "fork", Type = "Stomme.FlowFork", Inputs = { ["Branches"] = "[\"Mail\",\"Archive\"]" } },
                    Say("mail", "mailed"),
                    Say("archive", "archived"),
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "fork", SourcePort = "Mail", TargetId = "mail" },
                    new PipelineGraphConnection { SourceId = "fork", SourcePort = "Archive", TargetId = "archive" },
                },
            };

            var (body, written, status) = await RunAsync(graph);

            Assert.True(status == PipelineStatus.Finished, $"{status}: {body}");
            Assert.Equal(new[] { "archived", "mailed" }, written.OrderBy(line => line, StringComparer.Ordinal));
            Assert.Equal("[\"Mail\",\"Archive\"]", PipelineGraphCodec.ToGraph(body).Steps.Single(step => step.Id == "fork").Inputs["Branches"]);
        }

        [Fact]
        public void The_catalog_says_which_steps_name_their_own_outcomes_and_the_problems_check_them()
        {
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());
            Assert.Equal(StepCatalogEntry.CaseOutcomes, catalog.Single(entry => entry.Type == "Stomme.FlowSwitch").OutcomesFrom);
            Assert.Equal(StepCatalogEntry.BranchesOutcomes, catalog.Single(entry => entry.Type == "Stomme.FlowFork").OutcomesFrom);
            // BL-229: Classify text's categories are its ports through the Fork's own input, so the editor draws one per category.
            Assert.Equal(StepCatalogEntry.BranchesOutcomes, catalog.Single(entry => entry.Type == "Stomme.ClassifyText").OutcomesFrom);
            Assert.Equal(new[] { "Stomme.ClassifyText", "Stomme.FlowFork", "Stomme.FlowSwitch" }, catalog.Where(entry => entry.OutcomesFrom is not null).Select(entry => entry.Type).OrderBy(type => type, StringComparer.Ordinal));

            var graph = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-tier", Name = "tier", TypeName = "String", Value = "gold" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "empty", Type = "Stomme.FlowDecision", Name = "Empty", InputConditions = { ["Condition"] = new PipelineCondition() } },
                    new PipelineGraphStep { Id = "typo", Type = "Stomme.FlowDecision", Name = "Typo", InputConditions = { ["Condition"] = Rule("{{ teir }}", "empty") } },
                    new PipelineGraphStep
                    {
                        Id = "switch", Type = "Stomme.FlowSwitch", Name = "Tiers",
                        Cases = { new PipelineGraphCase { Label = "Gold", Condition = Rule("{{ tier }}", "equals", "gold") }, new PipelineGraphCase { Label = "gold", Condition = Rule("{{ tier }}", "equals", "x") }, new PipelineGraphCase { Label = "" , Condition = new PipelineCondition { Rules = { new PipelineConditionRule() } } } },
                    },
                    new PipelineGraphStep { Id = "none", Type = "Stomme.FlowSwitch", Name = "No cases" },
                    new PipelineGraphStep { Id = "fork", Type = "Stomme.FlowFork", Name = "Fork", Inputs = { ["Branches"] = "[\"Mail\",\"mail\"]" } },
                },
            };

            var problems = PipelineGraphProblems.Find(graph, catalog).Where(problem => problem.Code != PipelineGraphProblems.NeverRuns).Select(problem => (problem.Step, problem.Severity, problem.Message)).ToList();

            Assert.Contains(("Empty", PipelineGraphProblems.Error, "Condition: the condition needs at least one rule."), problems);
            Assert.Contains(("Typo", PipelineGraphProblems.Error, "Condition reads {{ teir }}, but no variable is named 'teir'."), problems);
            Assert.Contains(("Tiers", PipelineGraphProblems.Error, "The case 'gold' is there twice."), problems);
            Assert.Contains(("Tiers", PipelineGraphProblems.Error, "Case 3 needs a label: it is the outcome its connection leaves by."), problems);
            Assert.Contains(("Tiers", PipelineGraphProblems.Error, "Case 3: the condition has a rule that tests nothing - give it a value, e.g. {{ status }}."), problems);
            Assert.Contains(("No cases", PipelineGraphProblems.Warning, "It has no case, so it always goes out through Default."), problems);
            Assert.Contains(("Fork", PipelineGraphProblems.Error, "Branches names the branch 'Mail' twice."), problems);
            Assert.Equal(7, problems.Count);
        }

        [Fact]
        public void The_steps_that_name_a_connection_ask_the_editor_for_a_connection_picker()
        {
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());

            Assert.Equal(new[] { "Stomme.EmailReceived", "Stomme.ExtractFromConnection", "Stomme.SendChatMessage", "Stomme.SendEmail", "Stomme.SendThroughConnection", "Stomme.TelegramMessageReceived" },
                catalog.Where(entry => entry.Inputs.Any(input => input.UIHint == PipelineInputHints.ConnectionPicker)).Select(entry => entry.Type).OrderBy(type => type, StringComparer.Ordinal));
            Assert.All(catalog.Where(entry => entry.Inputs.Any(input => input.UIHint == PipelineInputHints.ConnectionPicker)),
                entry => Assert.Equal("Connection", entry.Inputs.Single(input => input.UIHint == PipelineInputHints.ConnectionPicker).Name));
        }

        [Fact]
        public void Two_editors_merge_a_condition_and_the_cases_as_settings_and_a_comparison_says_them_in_words()
        {
            var before = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "check", Type = "Stomme.FlowDecision", InputConditions = { ["Condition"] = Rule("{{ total }}", "greater", "100") } },
                    new PipelineGraphStep { Id = "switch", Type = "Stomme.FlowSwitch", Cases = { new PipelineGraphCase { Label = "Gold", Condition = Rule("{{ tier }}", "equals", "gold") } } },
                },
            };
            var after = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "check", Type = "Stomme.FlowDecision", InputConditions = { ["Condition"] = Rule("{{ total }}", "greater", "300") } },
                    new PipelineGraphStep { Id = "switch", Type = "Stomme.FlowSwitch", Cases = { new PipelineGraphCase { Label = "Platinum", Condition = Rule("{{ tier }}", "equals", "platinum") } } },
                },
            };

            var changes = PipelineGraphFields.Changes(before, after);
            Assert.Equal(new[] { "step/check/input-condition/Condition", "step/switch/cases" }, changes.Select(change => change.Path));
            Assert.All(changes, change => Assert.Equal(Stomme.Collaboration.FieldKind.Structured, change.Kind));

            var back = PipelineGraphFields.ToGraph(PipelineGraphFields.ToFields(after));
            Assert.Equal("{{ total }} is greater than 300", PipelineConditions.Describe(back.Steps[0].InputConditions["Condition"]));
            Assert.Equal("Platinum", back.Steps[1].Cases.Single().Label);

            Assert.Equal(new[]
            {
                "step check, the condition of input 'Condition': '{{ total }} is greater than 100' → '{{ total }} is greater than 300'",
                "step switch, its cases: 'Gold: {{ tier }} is equal to gold' → 'Platinum: {{ tier }} is equal to platinum'",
            }, PipelineGraphDiff.Lines(before, after));
        }

        [Fact]
        public void A_switch_case_without_a_label_or_named_twice_is_refused()
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IStepRegistry>();
            PipelineGraph Switch(params string[] labels) => new()
            {
                Steps = { new PipelineGraphStep { Id = "s", Type = "Stomme.FlowSwitch", Cases = labels.Select(label => new PipelineGraphCase { Label = label, Condition = Rule("1", "equals", "1") }).ToList() } },
            };

            Assert.Contains("needs a label", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(Switch("A", " "), null, registry, scope.ServiceProvider.GetRequiredService<IIdentityGenerator>())).Message);
            Assert.Contains("twice", Assert.Throws<ArgumentException>(() => PipelineGraphCodec.ToBody(Switch("A", "a"), null, registry, scope.ServiceProvider.GetRequiredService<IIdentityGenerator>())).Message);
        }
    }
}
