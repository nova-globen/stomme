using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Collaboration;
using Stomme.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.Collaboration.Services;
using Stomme.Modules.Pipelines.Canvas;
using Stomme.Modules.Pipelines.Services;
using Stomme.Pipelines;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EditorPage = Stomme.Modules.Pipelines.Editor;
using IndexPage = Stomme.Modules.Pipelines.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Find and place steps fast: a search over the catalog that ranks names first, arrows and Enter to
    /// add, and a step added while another is selected lands to its right CONNECTED to it, so steps typed one after another
    /// form a chain.
    /// </summary>
    public sealed class PipelineStepPickerTests : BunitContext
    {
        private static readonly List<StepCatalogEntry> Catalog = new()
        {
            new() { Type = "Stomme.ParseCsv", Version = 1, DisplayName = "Parse CSV", Category = "Data", Description = "Read CSV text into records." },
            new() { Type = "Stomme.ToCsv", Version = 1, DisplayName = "Records to CSV", Category = "Data", Description = "Render records as CSV text." },
            new() { Type = "Stomme.Log", Version = 1, DisplayName = "Log", Category = "Control Flow", Description = "Write a line to the run log." },
            new() { Type = "Stomme.Delay", Version = 1, DisplayName = "Delay", Category = "Control Flow", Description = "Wait a while." },
            new()
            {
                Type = "Stomme.FlowDecision", Version = 1, DisplayName = "Decision", Category = "Branching", Description = "Choose a path.", Ports = { "True", "False" },
                Inputs = { new() { Name = "Condition", DisplayName = "Condition", TypeName = "Boolean", UIHint = "singleline" } },
            },
            new() { Type = "Stomme.SendHttpRequest", Version = 1, DisplayName = "HTTP request", Category = "Actions", Description = "Call a URL and keep the answer." },
            // A step that cannot do without one of its inputs.
            new()
            {
                Type = "Stomme.WriteLine", Version = 1, DisplayName = "Write Line", Category = "Console", Description = "Write a line of text.",
                Inputs = { new() { Name = "Text", DisplayName = "Text", TypeName = "String", IsRequired = true } },
            },
            // Two steps that name their own outcomes - a Switch's cases beside Default, a Fork's branches.
            new()
            {
                Type = "Stomme.FlowSwitch", Version = 1, DisplayName = "Switch (flow)", Category = "Flow control", Description = "Go out through the case that matches.",
                Ports = { "Default" }, OutcomesFrom = StepCatalogEntry.CaseOutcomes,
                Inputs =
                {
                    new() { Name = "Cases", DisplayName = "Cases", TypeName = "ICollection`1", UIHint = "flow-switch-editor" },
                    new() { Name = "Mode", DisplayName = "Mode", TypeName = "SwitchMode", Options = new List<string> { "MatchFirst", "MatchAny" } },
                },
            },
            new()
            {
                Type = "Stomme.FlowFork", Version = 1, DisplayName = "Fork (flow)", Category = "Flow control", Description = "Go out through every branch at once.",
                OutcomesFrom = StepCatalogEntry.BranchesOutcomes,
                Inputs = { new() { Name = "Branches", DisplayName = "Branches", TypeName = "ICollection`1", UIHint = "dynamic-outcomes" } },
            },
            // A step that names one of the site's connections.
            new()
            {
                Type = "Stomme.ExtractFromConnection", Version = 1, DisplayName = "Extract from connection", Category = "Data", Description = "Extract from one of the site's connections.",
                Inputs =
                {
                    new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", UIHint = PipelineInputHints.ConnectionPicker },
                    new() { Name = "Operation", DisplayName = "Operation", TypeName = "String", UIHint = PipelineInputHints.ConnectorOperation },
                    new() { Name = "Query", DisplayName = "Query", TypeName = "String", UIHint = PipelineInputHints.SqlQuery },
                },
            },
            // The two triggers with a panel of their own.
            new()
            {
                Type = "Stomme.Webhook", Version = 1, DisplayName = "Webhook", Category = "Triggers", Description = "Start the pipeline when the site's webhook receiver is called with this name.", Kind = "Trigger",
                Inputs = { new() { Name = "WebhookName", DisplayName = "Name", TypeName = "String", IsRequired = true } },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            },
            new()
            {
                Type = "Stomme.Schedule", Version = 1, DisplayName = "Schedule", Category = "Triggers", Description = "Start the pipeline on a cron schedule (UTC).", Kind = "Trigger",
                Inputs = { new() { Name = "Cron", DisplayName = "Cron", TypeName = "String", IsRequired = true }, new() { Name = "TimeZone", DisplayName = "Time zone", TypeName = "String" } },
            },
            // n8n's error workflow - a trigger watching one of the site's other pipelines, or every one.
            new()
            {
                Type = PipelineFailure.StepType, Version = 1, DisplayName = "Pipeline failed", Category = "Triggers", Description = "Start the pipeline when a run of another pipeline on the site fails.", Kind = "Trigger",
                Inputs = { new() { Name = "Pipeline", DisplayName = "Pipeline", TypeName = "String", UIHint = "pipeline-definition-picker" } },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            },
            // n8n's Form trigger - its fields edited as a list, its page's address in its panel.
            new()
            {
                Type = PipelineForms.StepType, Version = 1, DisplayName = "Form submitted", Category = "Triggers", Description = "Start the pipeline when a person submits the form the site shows for it.", Kind = "Trigger",
                Inputs =
                {
                    new() { Name = "FormName", DisplayName = "Name", TypeName = "String", IsRequired = true },
                    new() { Name = "Fields", DisplayName = "Fields", TypeName = "String", IsRequired = true, UIHint = PipelineInputHints.FormFields },
                },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            },
            // n8n's AI Agent - its tools offered from the site's published ones; and the trigger that makes a pipeline one.
            new()
            {
                Type = "Stomme.AiAgent", Version = 1, DisplayName = "AI agent", Category = "AI", Description = "Let the site's language model answer with tools.", Kind = "Action",
                Inputs = { new() { Name = "Tools", DisplayName = "Tools", TypeName = "String", UIHint = PipelineInputHints.AgentTools } },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            },
            new()
            {
                Type = "Stomme.AgentTool", Version = 1, DisplayName = "AI agent tool", Category = "Triggers", Description = "Start the pipeline when an AI agent on the site calls it as a tool.", Kind = "Trigger",
                Inputs = { new() { Name = "ToolName", DisplayName = "Name", TypeName = "String", IsRequired = true } },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            },
            // The step that sets one of the pipeline's variables - the engine's own picker for it.
            new()
            {
                Type = "Stomme.SetVariable", Version = 1, DisplayName = "Set Variable", Category = "Primitives", Description = "Assign a value to a pipeline variable.",
                Inputs =
                {
                    new() { Name = "Variable", DisplayName = "Variable", TypeName = "Variable", UIHint = PipelineInputHints.VariablePicker },
                    new() { Name = "Value", DisplayName = "Value", TypeName = "Object" },
                },
            },
        };

        [Theory]
        [InlineData("csv", new[] { "Stomme.ParseCsv", "Stomme.ToCsv" })]     // a word of the name ("CSV") before the name containing it at the end
        [InlineData("parse", new[] { "Stomme.ParseCsv" })]
        [InlineData("records csv", new[] { "Stomme.ToCsv", "Stomme.ParseCsv" })] // every word, anywhere; the name starting with the query first
        [InlineData("url", new[] { "Stomme.SendHttpRequest" })]                  // the description counts
        [InlineData("branching", new[] { "Stomme.FlowDecision" })]               // and the category
        [InlineData("nothing like this", new string[0])]
        public void The_search_finds_every_word_and_ranks_names_first(string query, string[] expected)
        {
            Assert.Equal(expected, StepSearch.Filter(Catalog, query).Select(entry => entry.Type));
        }

        [Fact]
        public void An_empty_query_is_the_whole_catalog_in_its_order()
        {
            Assert.Equal(Catalog.Select(entry => entry.Type), StepSearch.Filter(Catalog, "  ").Select(entry => entry.Type));
        }

        [Fact]
        public void A_step_after_another_lands_one_column_right_and_below_whatever_is_there()
        {
            Assert.Equal((290d, 40d), StepPlacement.After((30, 40), new[] { (30d, 40d) }));
            // A second step after the same one: the first spot is taken, so one row down.
            Assert.Equal((290d, 180d), StepPlacement.After((30, 40), new[] { (30d, 40d), (290d, 40d) }));
        }

        // How many runs may go at once, chosen with nothing selected - a change like any other: saved with the graph, undone.
        [Fact]
        public void How_many_runs_go_at_once_is_chosen_saved_and_undone_like_any_change()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-runs-at-once")));
            Assert.Equal(PipelineRunsAtOnce.All.Select(choice => choice.Label), cut.FindAll("#pipelines-editor-runs-at-once option").Select(option => option.TextContent.Trim()));
            Assert.Equal("", cut.Find("#pipelines-editor-runs-at-once").GetAttribute("value"));

            cut.Find("#pipelines-editor-runs-at-once").Change(PipelineRunsAtOnce.One);
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-undo").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(PipelineRunsAtOnce.One, service.Saved!.RunsAtOnce);

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#pipelines-editor-runs-at-once").GetAttribute("value")));
            Assert.StartsWith("A start while a run is going - or waiting - is skipped", cut.Find("#pipelines-editor-runs-at-once-help").TextContent.Trim());

            // In turn: what it does is said - a start waits its turn, Queued, rather than being skipped.
            cut.Find("#pipelines-editor-runs-at-once").Change(PipelineRunsAtOnce.OneInTurn);
            cut.WaitForAssertion(() => Assert.StartsWith("A start while a run is going - or waiting - waits its turn", cut.Find("#pipelines-editor-runs-at-once-help").TextContent.Trim()));
        }

        // What the runs keep of their steps' data, chosen beside them: saved with the graph, undone; always is saved as nothing.
        [Fact]
        public void What_the_runs_keep_of_their_steps_data_is_chosen_saved_and_undone_like_any_change()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-keep-step-data")));
            Assert.Equal(PipelineStepDataKeeping.All.Select(choice => choice.Label), cut.FindAll("#pipelines-editor-keep-step-data option").Select(option => option.TextContent.Trim()));
            Assert.Equal(PipelineStepDataKeeping.Always, cut.Find("#pipelines-editor-keep-step-data").GetAttribute("value"));

            cut.Find("#pipelines-editor-keep-step-data").Change(PipelineStepDataKeeping.Never);
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-undo").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(PipelineStepDataKeeping.Never, service.Saved!.KeepStepData);

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(PipelineStepDataKeeping.Always, cut.Find("#pipelines-editor-keep-step-data").GetAttribute("value")));
            cut.Find("#pipelines-editor-keep-step-data").Change(PipelineStepDataKeeping.WhenFailed);
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(PipelineStepDataKeeping.WhenFailed, service.Saved!.KeepStepData));

            // n8n's "save execution progress": off unless ticked, and saved as the pipeline's setting.
            Assert.False(cut.Find("#pipelines-editor-save-progress").HasAttribute("checked"));
            Assert.StartsWith("Ticked, a run is stored after each step it runs", cut.Find("#pipelines-editor-save-progress-help").TextContent.Trim());
            cut.Find("#pipelines-editor-save-progress").Change(true);
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.True(service.Saved!.SaveProgress));
            cut.Find("#pipelines-editor-keep-step-data").Change(PipelineStepDataKeeping.Always);
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Null(service.Saved!.KeepStepData));
            Assert.True(service.Saved!.SaveProgress); // kept through the save that changed something else
            Assert.True(cut.Find("#pipelines-editor-save-progress").HasAttribute("checked"));
        }

        // n8n's zoom to fit on open: a pipeline whose steps do not all show at the view it opens with is fitted once the
        // canvas has its size - zoomed out, every step whole on the canvas; one that shows whole opens as it is.
        [Fact]
        public void A_pipeline_too_wide_for_the_canvas_opens_fitted()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            static PipelineGraphStep Log(string id, double x, double y) => new() { Id = id, Type = "Stomme.LogMessage", Name = id, Position = new GraphPosition { X = x, Y = y }, Inputs = { ["Message"] = id } };

            var wide = new FakePipelinesService { Loaded = new PipelineGraph { Steps = { Log("a", 80, 80), Log("b", 1000, 80), Log("c", 1900, 500) } } };
            Services.AddSingleton<IPipelinesService>(wide);
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            cut.WaitForAssertion(() => Assert.True(Diagram().Zoom < 1, $"zoom {Diagram().Zoom}"));
            var fitted = Diagram();
            Assert.True(Stomme.Modules.Pipelines.Canvas.CanvasFit.Shows(1200, 800, fitted.Zoom, fitted.Pan.X, fitted.Pan.Y,
                fitted.Nodes.Select(node => (node.Position.X, node.Position.Y, node.Size?.Width, node.Size?.Height))));
            Assert.Equal(new[] { 80d, 1000d, 1900d }, fitted.Nodes.Select(node => node.Position.X)); // the view moved, not the steps
            Assert.DoesNotContain("unsaved", cut.Markup, StringComparison.OrdinalIgnoreCase); // a view is no change to save
        }

        [Fact]
        public void A_pipeline_that_shows_whole_opens_at_its_own_size()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            var small = new FakePipelinesService
            {
                Loaded = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "a", Type = "Stomme.LogMessage", Name = "a", Position = new GraphPosition { X = 80, Y = 80 }, Inputs = { ["Message"] = "a" } },
                        new PipelineGraphStep { Id = "b", Type = "Stomme.LogMessage", Name = "b", Position = new GraphPosition { X = 380, Y = 80 }, Inputs = { ["Message"] = "b" } },
                    },
                },
            };
            Services.AddSingleton<IPipelinesService>(small);
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            cut.WaitForAssertion(() => Assert.True(Diagram().Container is { Width: > 0 }));
            Assert.Equal((1d, 0d, 0d), (Diagram().Zoom, Diagram().Pan.X, Diagram().Pan.Y));
        }

        // A pipeline that already saves its progress opens ticked, and a save of another change keeps it.
        [Fact]
        public void A_pipeline_that_saves_its_progress_opens_ticked_and_keeps_it_through_a_save()
        {
            var service = new FakePipelinesService { Loaded = new PipelineGraph { SaveProgress = true } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.True(cut.Find("#pipelines-editor-save-progress").HasAttribute("checked")));
            cut.Find("#pipelines-editor-keep-step-data").Change(PipelineStepDataKeeping.Never);
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal((PipelineStepDataKeeping.Never, (bool?)true), (service.Saved!.KeepStepData, service.Saved.SaveProgress));
        }

        // n8n's time saved per run, typed beside them - saved with the graph, undone; nought or less says nothing.
        [Fact]
        public void The_minutes_a_run_saves_are_typed_saved_and_undone_like_any_change()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-minutes-saved")));
            Assert.Equal("", cut.Find("#pipelines-editor-minutes-saved").GetAttribute("value"));

            cut.Find("#pipelines-editor-minutes-saved").Change("12");
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-undo").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(12, service.Saved!.MinutesSavedPerRun);

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#pipelines-editor-minutes-saved").GetAttribute("value")));
            cut.Find("#pipelines-editor-minutes-saved").Change("0");
            Assert.Equal("", cut.Find("#pipelines-editor-minutes-saved").GetAttribute("value"));
        }

        // n8n's "Timeout Workflow", typed beside them - saved with the graph, undone; nought is no limit, past a day a day.
        [Fact]
        public void A_runs_time_limit_is_typed_saved_and_undone_like_any_change()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-timeout-minutes")));
            Assert.Equal("", cut.Find("#pipelines-editor-timeout-minutes").GetAttribute("value"));

            cut.Find("#pipelines-editor-timeout-minutes").Change("30");
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-undo").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(30, service.Saved!.TimeoutMinutes);

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#pipelines-editor-timeout-minutes").GetAttribute("value")));
            cut.Find("#pipelines-editor-timeout-minutes").Change("0");
            Assert.Equal("", cut.Find("#pipelines-editor-timeout-minutes").GetAttribute("value"));
            cut.Find("#pipelines-editor-timeout-minutes").Change("5000");
            cut.WaitForAssertion(() => Assert.Equal("1440", cut.Find("#pipelines-editor-timeout-minutes").GetAttribute("value")));
        }

        // Beside each variable, where it is used - the steps that set it and those that read it, each a way to the step - and
        // one used by nothing said so.
        [Fact]
        public void Each_variable_says_which_steps_set_and_read_it_each_a_way_to_the_step()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String" }, new PipelineGraphVariable { Id = "v-idle", Name = "idle", TypeName = "String" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Read people", Position = new GraphPosition { X = 30, Y = 40 }, OutputVariables = { ["Result"] = "v-who" } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Greet", Position = new GraphPosition { X = 260, Y = 40 }, InputExpressions = { ["Text"] = "Hello {{ who }}" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "say" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-variable-uses-v-who")));
            string Uses(string id) => string.Join(" ", cut.Find("#pipelines-editor-variable-uses-" + id).TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal("set by Read people · read by Greet", Uses("v-who"));
            Assert.Equal("Not used yet.", Uses("v-idle"));

            cut.Find("#pipelines-editor-variable-uses-v-who").QuerySelectorAll("button").Single(link => link.TextContent.Trim() == "Greet").Click();
            cut.WaitForAssertion(() => Assert.Equal("Greet", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
        }

        private static StepCatalogEntry RunScriptEntry() => new()
        {
            Type = "Stomme.RunScript", Version = 1, DisplayName = "Run script", Category = "Scripting", Description = "Run a JavaScript script.",
            Inputs =
            {
                new() { Name = "Script", DisplayName = "Script", TypeName = "String", UIHint = "code-editor", IsRequired = true },
                new() { Name = "Input", DisplayName = "Input", TypeName = "String", UIHint = "multiline" },
                new() { Name = "Variables", DisplayName = "Variables", TypeName = "String", UIHint = "multiline" },
                new() { Name = "Style", DisplayName = "Written for", TypeName = "ScriptStyle", Options = new List<string> { "JavaScript", "N8nAllItems", "N8nEachItem" } },
            },
            Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
        };

        // n8n's "Ask AI" in its Code node: under a Run script step's script the AI is asked to write it, told what the script
        // can see - the fields of what the step before it last produced, its variables' names - and never a value, which the editor
        // says under the button; the script comes back as ONE change Undo takes back, one that does not parse said so, a refusal said.
        [Fact]
        public void Ask_ai_writes_a_run_scripts_script_told_only_what_it_can_see_as_one_change_undo_takes_back()
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = true, Scripts = true } };
            service.ExtraSteps.Add(new StepCatalogEntry { Type = "Stomme.Fetch", Version = 1, DisplayName = "Fetch", Category = "Data", Description = "Fetch records.", Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } } });
            service.ExtraSteps.Add(RunScriptEntry());
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "fetch", Type = "Stomme.Fetch", Name = "Fetch orders", Position = new GraphPosition { X = 30, Y = 40 }, PinnedResult = """[{"id":7,"total":250,"customer":"Ada"}]""" },
                    new PipelineGraphStep { Id = "script", Type = "Stomme.RunScript", Name = "Big orders", Position = new GraphPosition { X = 260, Y = 40 }, Inputs = { ["Variables"] = "limit = 100\nlabel = {{ who }}" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "fetch", SourcePort = "Done", TargetId = "script" } },
            };
            service.Written = new PipelineScriptView { Script = "var orders = JSON.parse(input);\nJSON.stringify(orders.filter(o => o.total > Number(limit)))" };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            page.QueryString["step"] = "script";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("Big orders", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            // What leaves the page, said before anything is asked: field names and variable names, no value.
            Assert.Equal("Ask AI to write it", cut.Find("label[for=pipelines-editor-script-ai-ask]").TextContent.Trim());
            Assert.Equal("The AI is told the script, the fields of the records it receives (id, total, customer) and its variables' names (limit, label) - never a value.",
                cut.Find("#pipelines-editor-script-ai-told").TextContent.Trim());
            Assert.True(cut.Find("#pipelines-editor-script-ai-write").HasAttribute("disabled")); // nothing asked yet

            cut.Find("#pipelines-editor-script-ai-ask").Input("Keep the orders over the limit");
            cut.Find("#pipelines-editor-script-ai-write").Click();

            cut.WaitForAssertion(() => Assert.Equal(service.Written.Script, cut.Find("#pipelines-editor-input-Script").GetAttribute("value")));
            var asked = Assert.Single(service.ScriptsAsked);
            Assert.Equal(("Keep the orders over the limit", "", "", PipelineScriptInput.Records), (asked.Instruction, asked.Script, asked.Style, asked.Receives));
            Assert.Equal(new[] { "limit", "label" }, asked.Variables);
            Assert.Equal(new[] { ("id", "number"), ("total", "number"), ("customer", "text") }, asked.Fields.Select(field => (field.Name, field.Kind)));
            Assert.DoesNotContain("Ada", System.Text.Json.JsonSerializer.Serialize(asked)); // never a value
            Assert.StartsWith("The AI wrote the script of 'Big orders' - read it before you run it", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.Equal("Ask AI to change it", cut.Find("label[for=pipelines-editor-script-ai-ask]").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-script-ai-problem"));

            // Kept with the step when saved; one change, which Undo takes back.
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(service.Written.Script, service.Saved!.Steps.Single(step => step.Id == "script").Inputs["Script"]);
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#pipelines-editor-input-Script").GetAttribute("value") ?? ""));

            // Changing one it has sends it; one that does not parse comes back saying where; a refusal is said and changes nothing.
            cut.Find("#pipelines-editor-input-Script").Input("input");
            service.Written = new PipelineScriptView { Script = "return input;", Problem = "It does not parse - line 1, column 1: 'return' outside of function" };
            cut.Find("#pipelines-editor-script-ai-ask").Input("Hand it on");
            cut.Find("#pipelines-editor-script-ai-write").Click();
            cut.WaitForAssertion(() => Assert.Equal("It does not parse - line 1, column 1: 'return' outside of function", cut.Find("#pipelines-editor-script-ai-problem").TextContent.Trim()));
            Assert.Equal("input", service.ScriptsAsked[1].Script);
            Assert.Equal("return input;", cut.Find("#pipelines-editor-input-Script").GetAttribute("value"));

            // The AI's script is a change of its own, not joined to what the author typed before it: Undo takes back only the AI's -
            // and the problem with it, which was about the script Undo took back (found driving it).
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("input", cut.Find("#pipelines-editor-input-Script").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#pipelines-editor-script-ai-problem"));

            service.ScriptRefusal = new InvalidOperationException("The assistant is switched off for this site.");
            cut.Find("#pipelines-editor-script-ai-ask").Input("Again");
            cut.Find("#pipelines-editor-script-ai-write").Click();
            cut.WaitForAssertion(() => Assert.Equal("The AI did not write it: The assistant is switched off for this site.", cut.Find("#pipelines-editor-script-ai-error").TextContent.Trim()));
            Assert.Equal("input", cut.Find("#pipelines-editor-input-Script").GetAttribute("value"));
        }

        // n8n's Code node linting: a moment after the author stops typing, each script the pipeline holds - a Run script
        // step's code in the style it is written for, an input given as JavaScript - is checked by the server's parser; one that does
        // not parse is a warning on its input and in the list, gone once the script parses; a text checked once is not asked again.
        [Fact]
        public async Task A_script_that_does_not_parse_is_said_on_its_input_a_moment_after_typing()
        {
            var service = new FakePipelinesService
            {
                Capabilities = new PipelineCapabilitiesView { Scripts = true },
                ScriptProblem = check => check.Script.Contains("return") && check.Style != "N8nAllItems" ? "It does not parse - line 1, column 1: Illegal return statement" : null,
            };
            service.ExtraSteps.Add(RunScriptEntry());
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "script", Type = "Stomme.RunScript", Name = "Totals", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Script"] = "1 + 1" } },
                    new PipelineGraphStep { Id = "n8n", Type = "Stomme.RunScript", Name = "From n8n", Position = new GraphPosition { X = 260, Y = 40 }, Inputs = { ["Script"] = "return items;", ["Style"] = "N8nAllItems" } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 490, Y = 40 }, InputScripts = { ["Text"] = "'n = ' + n" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "script", SourcePort = "Done", TargetId = "n8n" }, new PipelineGraphConnection { SourceId = "n8n", SourcePort = "Done", TargetId = "say" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            page.QueryString["step"] = "script";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            var wait = TimeSpan.FromSeconds(5);

            // Opened: every script asked about once - each in its style, the JavaScript input too - and none is wrong. (Polled: an
            // answer of scripts that all parse draws nothing again, and bUnit's wait checks only on a render.)
            for (var tries = 0; tries < 50 && service.ScriptChecks.Count == 0; tries++)
            {
                await Task.Delay(100);
            }

            Assert.Single(service.ScriptChecks);
            Assert.Equal(new[] { ("1 + 1", ""), ("return items;", "N8nAllItems"), ("'n = ' + n", "") }, service.ScriptChecks[0].Scripts.Select(check => (check.Script, check.Style)));
            // A Run script step's script with its variables' names (none here), so a name it cannot see is said; an input's
            // JavaScript with none asked - it reads every variable in scope.
            Assert.Equal(new[] { "[]", "[]", "null" }, service.ScriptChecks[0].Scripts.Select(check => check.Names is null ? "null" : "[" + string.Join(",", check.Names) + "]"));
            Assert.Empty(cut.FindAll("#pipelines-editor-input-problem-Script"));

            // A script that does not parse: said on its input and in the list, as a warning, once the author pauses. (Typed through
            // the renderer: the check's answer draws again in the background, between a Find and its Input.)
            await cut.InvokeAsync(() => cut.Find("#pipelines-editor-input-Script").Input("var total = 0;\nreturn total;"));
            cut.WaitForAssertion(() => Assert.Equal("It does not parse - line 1, column 1: Illegal return statement. The step fails when it runs.", cut.Find("#pipelines-editor-input-problem-Script").TextContent.Trim()), wait);
            Assert.Contains("is-warning", cut.Find("#pipelines-editor-input-problem-Script").GetAttribute("class"));
            Assert.Equal(new[] { "var total = 0;\nreturn total;" }, service.ScriptChecks[^1].Scripts.Select(check => check.Script)); // only what was not checked yet
            Assert.Contains("It does not parse", cut.Find("#pipelines-editor-problems").TextContent);

            // Put right, it is gone; the text written again brings its known answer back at once - not asked again.
            await cut.InvokeAsync(() => cut.Find("#pipelines-editor-input-Script").Input("var total = 0;\ntotal"));
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-input-problem-Script")), wait);
            var asked = service.ScriptChecks.Count;
            await cut.InvokeAsync(() => cut.Find("#pipelines-editor-input-Script").Input("var total = 0;\nreturn total;"));
            Assert.NotEmpty(cut.FindAll("#pipelines-editor-input-problem-Script"));
            await Task.Delay(800); // past the pause after typing: had it been asked, it would have been by now
            Assert.Equal(asked, service.ScriptChecks.Count);
        }

        // A step's discussion (better than n8n, whose workflows have none): read and added in the step's inspector, its
        // open comments counted on the step, resolved and reopened, what is being said kept per step - and none of it a change
        // to save or to undo.
        [Fact]
        public async Task A_step_is_discussed_in_its_inspector_and_its_open_comments_are_counted_on_it()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Log", Name = "Second", Position = new GraphPosition { X = 300, Y = 40 } },
                },
            };
            var bobs = new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "a", UserName = "bob", Text = "Why\nlog here?", CreatedOnUtc = new DateTime(2026, 10, 8, 9, 30, 0, DateTimeKind.Utc) };
            var onSecond = new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "b", UserName = "bob", Text = "And here?", CreatedOnUtc = new DateTime(2026, 10, 8, 9, 40, 0, DateTimeKind.Utc) };
            service.Comments.Add(bobs);
            service.Comments.Add(onSecond);
            service.Comments.Add(new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "a", UserName = "ada", Mine = true, CanDelete = true, Text = "Settled", Resolved = true, ResolvedBy = "bob", ResolvedOnUtc = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc) });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddLocalization(); // the delete's confirmation

            var page = Page();
            page.QueryString["step"] = "a";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            string Badges() => string.Join("|", cut.FindAll(".db-step-node").Select(node => node.QuerySelector(".db-step-node__discussion")?.TextContent.Trim() ?? "-"));

            // What is open is counted on its step; the step's inspector reads it - the resolved behind a toggle.
            Assert.Equal("1 open comment|1 open comment", Badges());
            Assert.Equal("Discussion - 1 open", cut.Find("#pipelines-editor-discussion h5").TextContent.Trim());
            var open = cut.Find("#pipelines-editor-comment-" + bobs.Id);
            Assert.Equal("bob", open.QuerySelector("strong")!.TextContent);
            Assert.Equal("Why\nlog here?", open.QuerySelector(".db-pipeline-editor__comment-text")!.TextContent);
            Assert.Contains("2026-10-08 09:30 UTC", open.TextContent);
            Assert.Empty(cut.FindAll("#pipelines-editor-comment-" + bobs.Id + " .pipelines-editor-comment-delete")); // not hers to delete
            Assert.Equal("Show the 1 resolved", cut.Find("#pipelines-editor-comments-resolved-toggle").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-comments-resolved"));
            cut.Find("#pipelines-editor-comments-resolved-toggle").Click();
            Assert.Contains("Resolved by bob 2026-10-08 10:00 UTC", cut.Find("#pipelines-editor-comments-resolved").TextContent);

            // Said with Ctrl+Enter: added to the step at once, counted on it - and no change to save.
            Assert.True(cut.Find("#pipelines-editor-discussion-add").HasAttribute("disabled")); // nothing said yet
            cut.Find("#pipelines-editor-discussion-text").Input("  Because the audit wants it.  ");
            cut.Find("#pipelines-editor-discussion-text").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }); // a line, not a send
            Assert.Empty(service.CommentWrites);
            cut.Find("#pipelines-editor-discussion-text").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter", CtrlKey = true });
            cut.WaitForAssertion(() => Assert.Equal("2 open comments|1 open comment", Badges()));
            Assert.Equal(new[] { "add a Because the audit wants it." }, service.CommentWrites);
            Assert.Equal("", cut.Find("#pipelines-editor-discussion-text").GetAttribute("value") ?? "");
            Assert.Equal("Discussion - 2 open", cut.Find("#pipelines-editor-discussion h5").TextContent.Trim());
            Assert.DoesNotContain("unsaved", cut.Find(".db-pipeline-editor__meta").TextContent);

            // Resolved: off the count, among the resolved; reopened: back.
            cut.Find("#pipelines-editor-comment-resolve-" + bobs.Id).Click();
            cut.WaitForAssertion(() => Assert.Equal("1 open comment|1 open comment", Badges()));
            Assert.Contains("Why", cut.Find("#pipelines-editor-comments-resolved").TextContent);
            Assert.Equal("Reopen", cut.Find("#pipelines-editor-comment-resolve-" + bobs.Id).TextContent.Trim());
            cut.Find("#pipelines-editor-comment-resolve-" + bobs.Id).Click();
            cut.WaitForAssertion(() => Assert.Equal("2 open comments|1 open comment", Badges()));
            Assert.Equal(new[] { "resolve " + bobs.Id + " true", "resolve " + bobs.Id + " false" }, service.CommentWrites.Skip(1));

            // What is being said is the step's own: another step's box is its own, and the first is kept to go back to.
            cut.Find("#pipelines-editor-discussion-text").Input("Half a thought");
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            cut.Find("#pipelines-editor-comment-step-" + onSecond.Id).Click(); // from the pipeline's list to the step
            cut.WaitForAssertion(() => Assert.Equal("Second", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.Equal("", cut.Find("#pipelines-editor-discussion-text").GetAttribute("value") ?? "");
            Assert.Equal("Discussion - 1 open", cut.Find("#pipelines-editor-discussion h5").TextContent.Trim());
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            cut.Find("#pipelines-editor-comment-step-" + bobs.Id).Click();
            cut.WaitForAssertion(() => Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.Equal("Half a thought", cut.Find("#pipelines-editor-discussion-text").GetAttribute("value"));

            // None of it was an edit of the pipeline: nothing to undo, nothing unsaved.
            Assert.True(cut.Find("#pipelines-editor-undo").HasAttribute("disabled"));
            Assert.DoesNotContain("unsaved", cut.Find(".db-pipeline-editor__meta").TextContent);

            // A refusal is said under the step's discussion, and nothing changes - what was being said kept.
            service.CommentRefusal = new InvalidOperationException("Say something in 1 to 2000 characters.");
            cut.Find("#pipelines-editor-discussion-add").Click();
            cut.WaitForAssertion(() => Assert.Equal("It was not added: Say something in 1 to 2000 characters.", cut.Find("#pipelines-editor-discussion-error").TextContent.Trim()));
            Assert.Equal("Half a thought", cut.Find("#pipelines-editor-discussion-text").GetAttribute("value"));
            Assert.Equal(4, service.Comments.Count);
        }

        // The discussion follows others - read again on the editor's clock, so a comment someone else adds shows without a
        // reload - and a step holding a body counts the open comments on the steps inside it, apart from its own.
        [Fact]
        public async Task The_discussion_follows_others_and_a_loop_counts_what_is_open_inside_it()
        {
            var clock = new DiscussionClock();
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "loop", Type = "Stomme.ForEach", Name = "Each order", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "inner", Type = "Stomme.Log", Name = "Note the order", Parent = "loop", ParentPort = "Body", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.Log", Name = "Done", Position = new GraphPosition { X = 260, Y = 40 } },
                },
            };
            service.Comments.Add(new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "inner", UserName = "bob", Text = "Per order?", CreatedOnUtc = new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc) });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.Add(editor => editor.Clock, clock).AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            string Badges(string title) => string.Join("|", cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == title)
                .QuerySelectorAll(".db-step-node__discussion").Select(badge => badge.TextContent.Trim()));

            // The loop holds the step commented on: it says so, apart from its own (none).
            Assert.Equal("1 open inside", Badges("Each order"));
            Assert.Equal("", Badges("Done"));

            // Someone else comments on the loop and resolves the inner one: shown at the next read, without a reload.
            service.Comments.Add(new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "loop", UserName = "carl", Text = "Batch these?", CreatedOnUtc = new DateTime(2026, 10, 9, 9, 5, 0, DateTimeKind.Utc) });
            service.Comments[0].Resolved = true;
            var poll = Assert.Single(clock.Timers, timer => timer.DueTime == EditorPage.DiscussionPollInterval);
            await cut.InvokeAsync(poll.Fire);
            cut.WaitForAssertion(() => Assert.Equal("1 open comment", Badges("Each order")));
            Assert.True(poll.Armed); // and read again after the next half minute
            Assert.Contains("Batch these?", cut.Find("#pipelines-editor-discussion").TextContent);
        }

        // @mentions: an @ and the start of a name offers the people who can see the pipeline (never oneself); one chosen
        // is written in for the name begun; an add says whom it told and which names told nobody.
        [Fact]
        public void An_at_offers_the_people_who_can_see_the_pipeline_and_an_add_says_who_was_told()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            service.People.Add(new PipelineMentionView { UserName = "user7", DisplayName = "Me Myself" });
            service.People.Add(new PipelineMentionView { UserName = "bob", DisplayName = "Bob Builder" });
            service.People.Add(new PipelineMentionView { UserName = "carl", DisplayName = "Carl Barks" });
            service.People.Add(new PipelineMentionView { UserName = "dora", DisplayName = "Dora" });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddLocalization(); // an added comment's delete confirmation

            var page = Page();
            page.QueryString["step"] = "a";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            string Offered() => string.Join("|", cut.FindAll("#pipelines-editor-mentions button").Select(button => button.TextContent.Trim()));

            // No @ being typed: nothing offered. "@b": by the start of a user name, or anywhere in a display name - never oneself.
            cut.Find("#pipelines-editor-discussion-text").Input("Can someone look");
            Assert.Empty(cut.FindAll("#pipelines-editor-mentions"));
            cut.Find("#pipelines-editor-discussion-text").Input("Can @b");
            Assert.Equal("Bob Builder @bob|Carl Barks @carl", Offered());
            cut.Find("#pipelines-editor-discussion-text").Input("Can @");
            Assert.Equal("Bob Builder @bob|Carl Barks @carl|Dora @dora", Offered());

            // Chosen: the name begun is replaced, a space follows, and the list is gone.
            cut.Find("#pipelines-editor-discussion-text").Input("Can @bu");
            Assert.Equal("Bob Builder @bob", Offered());
            cut.Find("#pipelines-editor-mentions button").Click();
            Assert.Equal("Can @bob ", cut.Find("#pipelines-editor-discussion-text").GetAttribute("value"));
            Assert.Empty(cut.FindAll("#pipelines-editor-mentions"));

            // Added: who was told, and a name that told nobody.
            cut.Find("#pipelines-editor-discussion-text").Input("Can @bob and @zed look?");
            cut.Find("#pipelines-editor-discussion-add").Click();
            cut.WaitForAssertion(() => Assert.Equal("Told Bob Builder. @zed told nobody - no one here by that name can see pipelines.",
                cut.Find("#pipelines-editor-discussion-told").TextContent.Trim()));
        }

        // With nothing selected, the pipeline's whole discussion - each comment a way to its step, one on a step since
        // removed said so and still resolved or deleted there - and anyone who can see the pipeline takes part, an author or not.
        [Fact]
        public void The_pipelines_discussion_is_listed_with_its_steps_and_a_reader_takes_part_too()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            var onFirst = new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "a", UserName = "bob", Text = "Fine by me", CreatedOnUtc = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc) };
            var onGone = new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "removed", UserName = "user7", Mine = true, CanDelete = true, Text = "This step is odd", CreatedOnUtc = new DateTime(2026, 10, 8, 9, 5, 0, DateTimeKind.Utc) };
            service.Comments.Add(onFirst);
            service.Comments.Add(onGone);
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddLocalization(); // the delete's confirmation

            // A reader: the pipeline's View key, not its Manage key.
            var page = Page();
            page.RenderMode = RenderModes.Interactive; // as the admin page renders - the delete's dialog asks in place
            page.User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Registered + ";" };
            var module = Module();
            Assert.True(PermissionKeys.TrySplit(PipelinesPermissions.View, out var resource, out var operation));
            module.PermissionList = module.PermissionList
                .Concat(new[] { new Permission(1, resource, PermissionKeys.KeyEntityId, operation, RoleNames.Registered, null, true) })
                .ToList();
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(module));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-comment-" + onGone.Id)));
            Assert.Empty(cut.FindAll("#pipelines-editor-save")); // a reader: nothing of the pipeline to change

            Assert.Equal("on First", cut.Find("#pipelines-editor-comment-step-" + onFirst.Id).TextContent.Trim());
            Assert.Equal("on a step since removed", cut.Find("#pipelines-editor-comment-gone-" + onGone.Id).TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-comment-step-" + onGone.Id));

            // Deleting asks first, in the house's dialog; Cancel keeps it.
            cut.Find("#pipelines-editor-comment-" + onGone.Id + " .pipelines-editor-comment-delete").Click();
            Assert.Equal("Delete your comment? It cannot be undone.", cut.Find("#pipelines-editor-comment-" + onGone.Id + " .db-dialog-body").TextContent.Trim());
            cut.Find("#pipelines-editor-comment-" + onGone.Id + " .pipelines-editor-comment-delete-cancel").Click();
            Assert.Empty(service.CommentWrites);
            cut.Find("#pipelines-editor-comment-" + onGone.Id + " .pipelines-editor-comment-delete").Click();
            cut.Find("#pipelines-editor-comment-" + onGone.Id + " .pipelines-editor-comment-delete-confirm").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-comment-" + onGone.Id)));
            Assert.Equal(new[] { "delete " + onGone.Id }, service.CommentWrites);

            // The reader resolves; to the step, and says something there.
            cut.Find("#pipelines-editor-comment-resolve-" + onFirst.Id).Click();
            cut.WaitForAssertion(() => Assert.Equal("Nothing open.", cut.Find("#pipelines-editor-comments-none-open").TextContent.Trim()));
            cut.Find("#pipelines-editor-comments-resolved-toggle").Click();
            cut.Find("#pipelines-editor-comment-step-" + onFirst.Id).Click();
            cut.WaitForAssertion(() => Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.True(cut.Find("#pipelines-editor-step-name").HasAttribute("disabled"));
            Assert.False(cut.Find("#pipelines-editor-discussion-text").HasAttribute("disabled"));
            cut.Find("#pipelines-editor-discussion-text").Input("Agreed");
            cut.Find("#pipelines-editor-discussion-add").Click();
            cut.WaitForAssertion(() => Assert.Equal("1 open comment", cut.Find(".db-step-node__discussion").TextContent.Trim()));
            Assert.Equal(new[] { "delete " + onGone.Id, "resolve " + onFirst.Id + " true", "add a Agreed" }, service.CommentWrites);

        }

        // n8n's "Save changes before leaving?": with nothing to save, leaving goes; with changes not saved it is held and
        // asked about - Stay keeps the page, Save and leave saves first (a refused save keeps the question and says why, nothing
        // lost), Leave without saving goes and saves nothing.
        [Fact]
        public async Task Leaving_with_changes_not_saved_is_asked_about_first()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

            var page = Page();
            page.QueryString["step"] = "a";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            // Nothing to save: leaving goes, asked about by nobody.
            await cut.InvokeAsync(() => navigation.NavigateTo("/elsewhere"));
            Assert.EndsWith("/elsewhere", navigation.Uri);
            Assert.Empty(cut.FindAll("#pipelines-editor-leaving"));

            // A change not saved: leaving is held, and asked about.
            cut.Find("#pipelines-editor-step-name").Input("Greet");
            await cut.InvokeAsync(() => navigation.NavigateTo("/away"));
            Assert.EndsWith("/elsewhere", navigation.Uri);
            Assert.Equal("Save changes before leaving?", cut.Find("#pipelines-editor-leaving-title").TextContent.Trim());
            Assert.Equal("'Picker' has changes that are not saved; leaving without saving loses them.", cut.Find("#pipelines-editor-leaving-text").TextContent.Trim());

            // While asked, the canvas's keys wait (Delete deletes nothing), and Escape is Stay - as the editor's script sends them.
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("delete"));
            Assert.Single(cut.FindAll(".db-step-node"));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            Assert.Empty(cut.FindAll("#pipelines-editor-leaving"));

            // Stay: the page as it was, the change kept.
            await cut.InvokeAsync(() => navigation.NavigateTo("/away"));
            cut.Find("#pipelines-editor-leave-stay").Click();
            Assert.Empty(cut.FindAll("#pipelines-editor-leaving"));
            Assert.EndsWith("/elsewhere", navigation.Uri);
            Assert.Equal("Greet", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));

            // Save and leave, refused: still here, asked still, and why said - nothing lost.
            await cut.InvokeAsync(() => navigation.NavigateTo("/away"));
            service.SaveRefusal = new InvalidOperationException("A schedule's cron is not one.");
            cut.Find("#pipelines-editor-leave-save").Click();
            cut.WaitForAssertion(() => Assert.Equal("It was not saved - The pipeline was refused: A schedule's cron is not one.", cut.Find("#pipelines-editor-leaving-error").TextContent.Trim()));
            Assert.EndsWith("/elsewhere", navigation.Uri);

            // Save and leave: saved, then gone.
            service.SaveRefusal = null;
            cut.Find("#pipelines-editor-leave-save").Click();
            cut.WaitForAssertion(() => Assert.EndsWith("/away", navigation.Uri));
            Assert.Equal("Greet", service.Saved!.Steps.Single().Name);
        }

        // Leave without saving goes, and saves nothing; and the browser is asked to confirm a reload or a closed tab only while there
        // is something to lose.
        [Fact]
        public async Task Leaving_without_saving_goes_and_saves_nothing()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

            var page = Page();
            page.QueryString["step"] = "a";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.False(cut.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>().Instance.ConfirmExternalNavigation);

            cut.Find("#pipelines-editor-step-name").Input("Greet");
            Assert.True(cut.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>().Instance.ConfirmExternalNavigation);
            await cut.InvokeAsync(() => navigation.NavigateTo("/away"));
            cut.Find("#pipelines-editor-leave-discard").Click();

            Assert.EndsWith("/away", navigation.Uri);
            Assert.Null(service.Saved);
        }

        // n8n's completion in its Code node: a script box says what its script can see - its own names for the style it is
        // written for, the members of those it knows, its variables' names, the fields of what it receives - and a SQL box says none.
        [Fact]
        public void A_script_box_says_what_its_script_can_see_for_completion()
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Scripts = true } };
            service.ExtraSteps.Add(new StepCatalogEntry { Type = "Stomme.Fetch", Version = 1, DisplayName = "Fetch", Category = "Data", Description = "Fetch records.", Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } } });
            service.ExtraSteps.Add(RunScriptEntry());
            service.ExtraSteps.Add(new StepCatalogEntry { Type = "Stomme.Query", Version = 1, DisplayName = "Query", Category = "Data", Description = "Query a database.", Inputs = { new() { Name = "Query", DisplayName = "Query", TypeName = "String", UIHint = PipelineInputHints.SqlQuery } } });
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "fetch", Type = "Stomme.Fetch", Name = "Fetch orders", Position = new GraphPosition { X = 30, Y = 40 }, PinnedResult = """[{"id":7,"total":250}]""" },
                    new PipelineGraphStep { Id = "script", Type = "Stomme.RunScript", Name = "Big orders", Position = new GraphPosition { X = 260, Y = 40 }, Inputs = { ["Variables"] = "limit = 100" } },
                    new PipelineGraphStep { Id = "sql", Type = "Stomme.Query", Name = "Orders", Position = new GraphPosition { X = 490, Y = 40 }, Inputs = { ["Query"] = "SELECT 1" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "fetch", SourcePort = "Done", TargetId = "script" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            page.QueryString["step"] = "script";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Script")));
            List<(string Name, string Kind, string Detail, string Insert)> Names() =>
                System.Text.Json.JsonDocument.Parse(cut.Find("#pipelines-editor-input-Script").GetAttribute("data-code-names")!).RootElement.EnumerateArray()
                    .Select(name => (name.GetProperty("name").GetString()!, name.GetProperty("kind").GetString()!, name.GetProperty("detail").GetString()!,
                        name.TryGetProperty("insert", out var insert) ? insert.GetString()! : ""))
                    .ToList();

            var names = Names();
            Assert.Contains(("input", "global", "the text it is given, or the step before's result", ""), names);
            Assert.Contains(("limit", "variable", "a variable of the step", ""), names);
            Assert.Contains(("total", "field", "field · number", ""), names); // the pinned records before it
            Assert.Contains(("parse", "member", "text to records", "parse()"), names);
            Assert.DoesNotContain(names, name => name.Name == "$input");
            Assert.Equal("Tab indents · Ctrl+Space completes · Esc then Tab leaves the box", cut.Find("#pipelines-editor-input-Script-keys").TextContent);

            // Written for n8n's Code node: its names instead.
            cut.Find("#pipelines-editor-input-Style").Change("N8nAllItems");
            names = Names();
            Assert.Contains(("$input", "global", "the items", ""), names);
            Assert.Contains(("all", "member", "every item", "all()"), names);
            Assert.DoesNotContain(names, name => name.Name == "input");

            // A SQL statement's box offers no completion.
            var sqlPage = Page();
            sqlPage.QueryString["step"] = "sql";
            var sql = Render<EditorPage>(parameters => parameters.AddCascadingValue(sqlPage).AddCascadingValue(Module()));
            sql.WaitForAssertion(() => Assert.NotNull(sql.Find("#pipelines-editor-input-Query")));
            Assert.False(sql.Find("#pipelines-editor-input-Query").HasAttribute("data-code-names"));
            Assert.Equal("Tab indents · Esc then Tab leaves the box", sql.Find("#pipelines-editor-input-Query-keys").TextContent);
        }

        // n8n's command bar: the step search finds the editor's own actions too, each with its keys; Enter does the one
        // marked, as its button or its keys do, and empties the search; an action the person cannot do here is not offered.
        [Fact]
        public async Task The_step_search_finds_the_editors_actions_and_does_them()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));

            // Found by a word it answers to, its keys said; Enter adds a sticky note, as Shift+S does, and empties the search.
            // The search's results render after its input: waited for, as a full run's load can make it lag.
            cut.Find("#pipelines-editor-step-search").Input("annotate");
            cut.WaitForAssertion(() => Assert.Equal("Add a sticky note Action · Shift+S", string.Join(" ", cut.Find("#pipelines-editor-command-note").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
            Assert.Empty(cut.FindAll(".db-note"));
            await cut.InvokeAsync(() => cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" }));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-note")));
            Assert.Equal("", cut.Find("#pipelines-editor-step-search").GetAttribute("value"));

            // Undo is offered now there is something to undo, and does it.
            cut.Find("#pipelines-editor-step-search").Input("undo");
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-command-undo")));
            cut.Find("#pipelines-editor-command-undo").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-note")));

            // Not published: running the published version is not offered - testing the pipeline is.
            cut.Find("#pipelines-editor-step-search").Input("run");
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-command-test")));
            Assert.Empty(cut.FindAll("#pipelines-editor-command-run"));
        }

        // Offered only where the site's model can be asked for this person AND scripts run - a script written where none can run is
        // a script nobody can use.
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        public void Ask_ai_is_offered_under_a_script_only_where_the_model_and_scripts_are_on(bool ai, bool scripts, bool offered)
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = ai, Scripts = scripts } };
            service.ExtraSteps.Add(RunScriptEntry());
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "script", Type = "Stomme.RunScript", Name = "Script", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            page.QueryString["step"] = "script";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Script")));

            Assert.Equal(offered, cut.FindAll("#pipelines-editor-script-ai-write").Count == 1);
            if (offered)
            {
                // Nothing before it and no Input given: said, so the author knows how to tell it more.
                Assert.Equal("The AI is told the script, not what it receives - run or pin the step before it to tell it - never a value.", cut.Find("#pipelines-editor-script-ai-told").TextContent.Trim());
            }
        }

        // A link naming a step (find in every pipeline) opens the editor with that step chosen - inside the body it sits in.
        [Fact]
        public void A_link_to_a_step_opens_the_editor_with_it_chosen()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "loop", Type = "Stomme.ForEach", Name = "Each order", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "inner", Type = "Stomme.Log", Name = "Note the order", Parent = "loop", ParentPort = "Body", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.Log", Name = "Done", Position = new GraphPosition { X = 260, Y = 40 } },
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            page.QueryString["step"] = "inner";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("Note the order", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.Equal(new[] { "Note the order" }, cut.FindAll(".db-step-node__title").Select(title => title.TextContent.Trim()).ToArray()); // in the loop's body
        }

        // What changed, said beside Save and publish - sent with the publish, cleared after, and a version's note shown in
        // the versions list.
        [Fact]
        public void What_changed_is_said_with_a_publish_and_shown_in_the_versions_list()
        {
            var service = new FakePipelinesService();
            service.Versions.Add(new PipelineVersionView { Id = "v-1", Version = 1, IsPublished = true, CreatedOnUtc = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc), SavedBy = "ada", Note = "Says hello first" });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-publish-note")));
            cut.Find("#pipelines-editor-publish-note").Input("  Adds the Slack step ");
            cut.Find("#pipelines-editor-publish").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Adds the Slack step" }, service.PublishedNotes));
            cut.WaitForAssertion(() => Assert.EndsWith("What changed: Adds the Slack step", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            Assert.Equal("", cut.Find("#pipelines-editor-publish-note").GetAttribute("value"));

            cut.Find("#pipelines-editor-publish").Click(); // nothing said: none sent
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Adds the Slack step", null }, service.PublishedNotes));

            cut.Find("#pipelines-editor-versions-toggle").Click();
            cut.WaitForAssertion(() => Assert.Equal("Says hello first", cut.Find("#pipelines-editor-version-note-1").TextContent.Trim()));
        }

        // n8n's workflow "Timezone", typed beside the time limit - saved with the graph, undone; "UTC" is none; one this
        // machine does not know is said at once.
        [Fact]
        public void A_pipelines_time_zone_is_typed_saved_and_undone_like_any_change()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-time-zone")));
            Assert.Equal("", cut.Find("#pipelines-editor-time-zone").GetAttribute("value"));
            Assert.Contains("Europe/Stockholm", cut.FindAll("#pipelines-editor-time-zones option").Select(option => option.GetAttribute("value")));

            cut.Find("#pipelines-editor-time-zone").Change(" Europe/Stockholm ");
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-undo").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal("Europe/Stockholm", service.Saved!.TimeZone);

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#pipelines-editor-time-zone").GetAttribute("value")));
            cut.Find("#pipelines-editor-time-zone").Change("UTC");
            Assert.Equal("", cut.Find("#pipelines-editor-time-zone").GetAttribute("value"));
            cut.Find("#pipelines-editor-time-zone").Change("Mars/Olympus_Mons");
            cut.WaitForAssertion(() => Assert.StartsWith("'Mars/Olympus_Mons' is not a time zone this server knows", cut.Find("#pipelines-editor-error").TextContent.Trim()));
        }

        // n8n's "This workflow can be called by" - any pipeline, none, or the ones chosen among the site's, saved with the
        // graph and undone; "only the ones chosen" with none chosen yet admits nobody.
        [Fact]
        public void Who_may_call_a_pipeline_is_chosen_saved_and_undone_like_any_change()
        {
            var service = new FakePipelinesService();
            service.Pipelines.Add(new PipelineView { DefinitionId = "def-1", Name = "This one" });
            service.Pipelines.Add(new PipelineView { DefinitionId = "orders", Name = "Orders" });
            service.Pipelines.Add(new PipelineView { DefinitionId = "billing", Name = "Billing" });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-called-by")));
            Assert.Equal("", cut.Find("#pipelines-editor-called-by").GetAttribute("value"));
            Assert.Empty(cut.FindAll("#pipelines-editor-called-by-add"));

            cut.Find("#pipelines-editor-called-by").Change("only");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-called-by-none")));
            // The site's other pipelines to choose from - not this one.
            Assert.Equal(new[] { "", "billing", "orders" }, cut.FindAll("#pipelines-editor-called-by-add option").Select(option => option.GetAttribute("value")).ToArray());
            cut.Find("#pipelines-editor-called-by-add").Change("orders");
            cut.WaitForAssertion(() => Assert.Equal("Orders", cut.Find("#pipelines-editor-called-by-orders span").TextContent));
            Assert.Equal(new[] { "", "billing" }, cut.FindAll("#pipelines-editor-called-by-add option").Select(option => option.GetAttribute("value")).ToArray());
            cut.Find("#pipelines-editor-called-by-add").Change("billing");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-called-by-billing")));
            Assert.Empty(cut.FindAll("#pipelines-editor-called-by-add")); // every other pipeline chosen: nothing left to add
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal("orders,billing", service.Saved!.CalledBy);

            cut.Find("#pipelines-editor-called-by-orders button").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-called-by-orders")));
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-called-by-orders")));

            cut.Find("#pipelines-editor-called-by").Change(PipelineCallers.Nobody);
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-called-by-list")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(PipelineCallers.Nobody, service.Saved!.CalledBy));
            cut.Find("#pipelines-editor-called-by").Change("");
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Null(service.Saved!.CalledBy));
        }

        // The caller's editor warns of a call the called pipeline would refuse, on the step and in the problems - and not
        // of one it lets through.
        [Fact]
        public void A_call_the_called_pipeline_would_refuse_is_warned_of_in_the_callers_editor()
        {
            var service = new FakePipelinesService();
            service.Pipelines.Add(new PipelineView { DefinitionId = "private", Name = "Private" });
            service.Pipelines.Add(new PipelineView { DefinitionId = "shared", Name = "Shared" });
            service.Others["private"] = new PipelineGraph { CalledBy = PipelineCallers.Only(new[] { "someone-else" }) };
            service.Others["shared"] = new PipelineGraph { CalledBy = PipelineCallers.Only(new[] { "def-1" }) };
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "call", Type = "Stomme.ExecutePipeline", Name = "Run the private one", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["PipelineDefinitionId"] = "private" } },
                    new PipelineGraphStep { Id = "other", Type = "Stomme.ExecutePipeline", Name = "Run the shared one", Position = new GraphPosition { X = 260, Y = 40 }, Inputs = { ["PipelineDefinitionId"] = "shared" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "call", TargetId = "other" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("#pipelines-editor-problems li"), item => item.TextContent.Contains("may be called only by the pipelines its settings name, and this one is not among them")));
            var refusals = cut.FindAll("#pipelines-editor-problems li").Where(item => item.TextContent.Contains("its settings")).Select(item => item.TextContent).ToList();
            Assert.Contains("Run the private one", Assert.Single(refusals)); // the shared one lets this pipeline call it
        }

        [Fact]
        public async Task Typing_and_Enter_adds_a_step_and_the_next_one_lands_connected_to_it()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            // The canvas measures itself through the browser; a test answers with a canvas-sized rectangle.
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));

            // "log", Enter: the first match is added and selected.
            cut.Find("#pipelines-editor-step-search").Input("log");
            Assert.Equal("Log", cut.FindAll("#pipelines-editor-step-results button")[0].TextContent.Trim().Split(' ')[0]);
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("Log", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#pipelines-editor-step-results")); // the query is cleared for the next step

            // "csv", arrow down to the second match, Enter: it lands after the selected Log, connected to it.
            cut.Find("#pipelines-editor-step-search").Input("csv");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
            Assert.Contains("is-active", cut.FindAll("#pipelines-editor-step-results button")[1].ClassName);
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("Records to CSV", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            // Escape clears a query.
            cut.Find("#pipelines-editor-step-search").Input("delay");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Escape" });
            Assert.Empty(cut.FindAll("#pipelines-editor-step-results"));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));

            var saved = service.Saved!;
            Assert.Equal(new[] { "Stomme.Log", "Stomme.ToCsv" }, saved.Steps.Select(step => step.Type));
            var log = saved.Steps[0];
            var toCsv = saved.Steps[1];
            var connection = Assert.Single(saved.Connections);
            Assert.Equal((log.Id, toCsv.Id), (connection.SourceId, connection.TargetId));
            Assert.Equal(log.Position.X + StepPlacement.ColumnWidth, toCsv.Position.X);
            Assert.Equal(log.Position.Y, toCsv.Position.Y);
        }

        // n8n's "Import cURL": an HTTP request step filled from a curl command as one change - what curl does that the
        // step does not said under it, a command that is none refused saying why, and the whole import taken back by one undo.
        [Fact]
        public void An_http_request_step_is_filled_from_a_curl_command_as_one_change()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("Log", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#pipelines-editor-curl-open")); // only the HTTP request step imports one
            cut.Find("#pipelines-editor-step-search").Input("HTTP request");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("HTTP request", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            cut.Find("#pipelines-editor-curl-open").Click();
            // Found driving it: the example's quotes written as entities showed as "&quot;".
            Assert.EndsWith("-d '{\"item\": \"book\"}'", cut.Find("#pipelines-editor-curl-text").GetAttribute("placeholder"));
            cut.Find("#pipelines-editor-curl-text").Input("wget https://example.com");
            cut.Find("#pipelines-editor-curl-import").Click();
            Assert.Equal("Paste a command that starts with curl.", cut.Find("#pipelines-editor-curl-error").TextContent.Trim());

            cut.Find("#pipelines-editor-curl-text").Input("curl -k https://api.example.com/orders -H 'Authorization: Bearer t0k' -H 'Accept: application/json' --json '{\"item\":\"book\"}'");
            cut.Find("#pipelines-editor-curl-import").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-curl-text")));
            var notes = cut.FindAll("#pipelines-editor-curl-notes li").Select(note => note.TextContent).ToList();
            Assert.Contains(notes, note => note.Contains("the step always checks it"));
            Assert.Contains(notes, note => note.Contains("secret store"));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var http = service.Saved!.Steps.Single(step => step.Type == "Stomme.SendHttpRequest");
            Assert.Equal(("https://api.example.com/orders", "POST", "application/json", "Bearer t0k"), (http.Inputs["Url"], http.Inputs["Method"], http.Inputs["ContentType"], http.Inputs["Authorization"]));
            Assert.Equal(("Accept: application/json", "{\"item\":\"book\"}"), (http.Inputs["Headers"], http.Inputs["Body"]));

            // One undo takes the whole import back.
            cut.Find("#pipelines-editor-undo").Click();
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.False(service.Saved!.Steps.Single(step => step.Type == "Stomme.SendHttpRequest").Inputs.ContainsKey("Url")));
            Assert.Equal(2, service.Saved!.Steps.Count); // the step itself stays: only the import was taken back

            // n8n's paste of a curl command onto the canvas: a new HTTP request step filled from it, after the selected one - and
            // one undo takes the step away. Text that is no curl command pastes as before (the editor's own copy, here none).
            cut.InvokeAsync(() => cut.Instance.OnPasteText("just some text"));
            cut.InvokeAsync(() => cut.Instance.OnPasteText("curl -X PUT https://example.com/items/7 -d 'name=Ada'"));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, service.Saved!.Steps.Count));
            var pasted = service.Saved!.Steps[2];
            Assert.Equal(("Stomme.SendHttpRequest", "https://example.com/items/7", "PUT", "name=Ada"), (pasted.Type, pasted.Inputs["Url"], pasted.Inputs["Method"], pasted.Inputs["Body"]));
            Assert.Contains(service.Saved!.Connections, connection => connection.TargetId == pasted.Id);
            cut.Find("#pipelines-editor-undo").Click();
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(2, service.Saved!.Steps.Count));
        }

        // A sub-pipeline's contract: a call whose Input leaves out what the called pipeline requires, or sends what a field
        // cannot hold, is warned about in the caller's editor; a call from an expression is the run's to decide. And the called
        // pipeline's "When called" step says who calls it.
        [Fact]
        public void A_call_is_held_to_what_the_called_pipeline_expects_and_the_called_one_says_who_calls_it()
        {
            var service = new FakePipelinesService();
            service.Pipelines.Add(new PipelineView { DefinitionId = "child", Name = "Child" });
            service.Others["child"] = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "in", Type = "Stomme.WhenCalled", Inputs = { ["Fields"] = "order (number): the order's number\ncustomer", ["RequiredFields"] = "order" } } },
            };
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "call", Type = "Stomme.ExecutePipeline", Name = "Run the child", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["PipelineDefinitionId"] = "child", ["Input"] = "{\"customer\":\"Ada\",\"order\":\"lots\"}" } },
                    new PipelineGraphStep { Id = "again", Type = "Stomme.ExecutePipeline", Name = "Run it from the run", Position = new GraphPosition { X = 260, Y = 40 }, Inputs = { ["PipelineDefinitionId"] = "child" }, InputExpressions = { ["Input"] = "{{ record }}" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "call", TargetId = "again" } },
            };
            service.ExtraSteps.Add(new StepCatalogEntry { Type = "Stomme.WhenCalled", Version = 1, DisplayName = "When called by another pipeline", Category = "Triggers", Kind = "Trigger", Description = "Where a run another pipeline starts enters." });
            service.Callers.Add(new PipelineCallerView { DefinitionId = "parent", Name = "Parent", StepId = "s1", StepName = "Run the child", Waits = true });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("#pipelines-editor-problems li"), item => item.TextContent.Contains("'lots' is not a number, which 'order' holds - the called pipeline would refuse it.")));
            var warnings = cut.FindAll("#pipelines-editor-problems li").Where(item => item.TextContent.Contains("called pipeline")).Select(item => item.TextContent).ToList();
            Assert.Single(warnings); // the call from an expression sends what its run has
            Assert.Contains("Run the child", warnings[0]);

            // The called pipeline's own step says who calls it.
            cut.Find("#pipelines-editor-step-search").Input("When called");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Contains("Parent", cut.Find("#pipelines-editor-callers").TextContent));
            Assert.Contains("its step 'Run the child' - waits for the run", cut.Find("#pipelines-editor-callers").TextContent);
        }

        // n8n's + after a node: each outcome nothing follows has one; pressed, the next step chosen lands after the step,
        // connected from THAT outcome - and the + is gone from it.
        [Fact]
        public void The_plus_on_an_outcome_adds_the_next_step_from_that_outcome()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("decision");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node__add").Count));
            Assert.Equal(new[] { "Add the next step after Decision - True", "Add the next step after Decision - False" }, cut.FindAll(".db-step-node__add").Select(add => add.GetAttribute("title")));

            cut.FindAll(".db-step-node__add")[1].Click();
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            Assert.Equal(new[] { "Add the next step after Decision - True", "Add the next step after Log" }, cut.FindAll(".db-step-node__add").Select(add => add.GetAttribute("title")));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var connection = Assert.Single(service.Saved!.Connections);
            Assert.Equal("False", connection.SourcePort);

            // n8n's connection let go over nothing: the canvas drops a link drawn from an outcome to no port (it adds the link as
            // the drag starts and removes it on the drop) - the step search opens, and the step chosen lands where it was let go,
            // connected from that outcome.
            var diagram = cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            var decision = diagram.Nodes.OfType<Stomme.Modules.Pipelines.Canvas.StepNodeModel>().Single(node => node.Type == "Stomme.FlowDecision");
            var whenTrue = decision.OutputPorts.OfType<Stomme.Modules.Pipelines.Canvas.StepPortModel>().Single(port => port.Outcome == "True");
            cut.InvokeAsync(() =>
            {
                var drawn = new Blazor.Diagrams.Core.Models.LinkModel(new Blazor.Diagrams.Core.Anchors.SinglePortAnchor(whenTrue), new Blazor.Diagrams.Core.Anchors.PositionAnchor(new Blazor.Diagrams.Core.Geometry.Point(620, 410)));
                diagram.Links.Add(drawn);
                diagram.Links.Remove(drawn);
            });
            cut.Find("#pipelines-editor-step-search").Input("csv");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(2, service.Saved!.Connections.Count));
            var placed = service.Saved!.Steps.Single(step => step.Type is not ("Stomme.FlowDecision" or "Stomme.Log"));
            Assert.Equal((620d, 410d), (placed.Position.X, placed.Position.Y));
            Assert.Contains(service.Saved!.Connections, link => link.SourcePort == "True" && link.TargetId == placed.Id);
        }

        // A step put on a connection: halfway, on the row of the step it goes to; with no room, that step and the ones
        // after it move right by what is missing; a connection going back moves nothing.
        [Fact]
        public void A_step_put_on_a_connection_lands_between_and_the_steps_after_it_make_room()
        {
            // A column apart: the new step takes the second one's place, and the second (and on) move one column right.
            Assert.Equal((290d, 40d, 260d), StepPlacement.Between((30, 40), (290, 40), Array.Empty<(double X, double Y)>()));
            // Far enough apart: halfway, on the row of the step the connection goes to, and nothing moves.
            Assert.Equal((430d, 180d, 0d), StepPlacement.Between((30, 40), (830, 180), Array.Empty<(double X, double Y)>()));
            // Going back: the first free spot after the step it leaves.
            Assert.Equal((290d, 180d, 0d), StepPlacement.Between((30, 40), (30, 300), new[] { (30d, 40d), (290d, 40d) }));

            // What moves: every step reached from the one the connection goes to, never through the one it leaves (a loop back).
            var after = StepPlacement.Downstream("b", "a", new (string From, string To)[] { ("a", "b"), ("b", "c"), ("c", "a"), ("c", "d"), ("x", "b") });
            Assert.Equal(new[] { "b", "c", "d" }, after.OrderBy(id => id, StringComparer.Ordinal));
        }

        // n8n's + and bin on a connection: the + puts the step chosen next between the two - from the same outcome, on
        // through its first - the steps after it moved right to make room, all one change Undo takes back; the bin removes the
        // connection. A version open read-only has neither.
        [Fact]
        public void The_plus_on_a_connection_puts_a_step_between_and_the_bin_removes_the_connection()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Third", Position = new GraphPosition { X = 550, Y = 40 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" }, new PipelineGraphConnection { SourceId = "b", TargetId = "c" } },
            };
            service.Versions.Add(new PipelineVersionView { Id = "v-1", Version = 1, IsPublished = true, CreatedOnUtc = new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc) });
            service.VersionGraphs[1] = System.Text.Json.JsonSerializer.Deserialize<PipelineGraph>(System.Text.Json.JsonSerializer.Serialize(service.Loaded))!;
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-connection-actions__insert").Count));
            string[] Connections() => service.Saved!.Connections.Select(link => link.SourceId + ">" + link.TargetId).OrderBy(link => link, StringComparer.Ordinal).ToArray();
            double[] Columns() => service.Saved!.Steps.Where(step => step.Id is "a" or "b" or "c").OrderBy(step => step.Id, StringComparer.Ordinal).Select(step => step.Position.X).ToArray();

            // The + of First -> Second: the step search says where the step chosen goes.
            cut.FindAll(".db-connection-actions__insert")[0].Click();
            cut.WaitForAssertion(() => Assert.Equal("The step you choose goes between First and Second (Escape stops).", cut.Find("#pipelines-editor-inserting").TextContent.Trim()));
            cut.Find("#pipelines-editor-step-search").Input("csv");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            Assert.Empty(cut.FindAll("#pipelines-editor-inserting"));
            Assert.StartsWith("Put Parse CSV between First and Second", cut.Find("#pipelines-editor-message").TextContent.Trim());

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var put = service.Saved!.Steps.Single(step => step.Id is not ("a" or "b" or "c"));
            Assert.Equal((290d, 40d), (put.Position.X, put.Position.Y));
            Assert.Equal(new[] { "a>" + put.Id, "b>c", put.Id + ">b" }.OrderBy(link => link, StringComparer.Ordinal), Connections());
            Assert.Equal(new[] { 30d, 550d, 810d }, Columns()); // Second and Third one column right, First where it was

            // One change: Undo takes the step, the moves and the connections back together.
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, service.Saved!.Steps.Count));
            Assert.Equal(new[] { "a>b", "b>c" }, Connections());
            Assert.Equal(new[] { 30d, 290d, 550d }, Columns());

            // The bin of Second -> Third: that connection gone, and Second's outcome offers its + again (the step is drawn again -
            // found driving: the canvas redraws a step only when it changes).
            Assert.DoesNotContain("Add the next step after Second", cut.FindAll(".db-step-node__add").Select(add => add.GetAttribute("title")));
            cut.FindAll(".db-connection-actions__remove")[1].Click();
            cut.WaitForAssertion(() => Assert.Contains("Add the next step after Second", cut.FindAll(".db-step-node__add").Select(add => add.GetAttribute("title"))));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "a>b" }, Connections()));

            // A version open read-only: its connections drawn, with no buttons on them.
            cut.Find("#pipelines-editor-versions-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-version-open-1")));
            cut.Find("#pipelines-editor-version-open-1").Click();
            cut.WaitForAssertion(() => Assert.Contains("Version 1, read-only.", cut.Find("#pipelines-editor-viewing").TextContent));
            Assert.Equal(2, cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram.Links.Count);
            Assert.Empty(cut.FindAll(".db-connection-actions"));
            Assert.Empty(cut.FindAll(".db-step-node__actions")); // nor the buttons over a step
            cut.FindAll(".db-step-node")[0].ContextMenu(new MouseEventArgs { ClientX = 100, ClientY = 100 }); // nor a right-click menu
            cut.Find("#pipelines-editor-canvas").ContextMenu(new MouseEventArgs { ClientX = 600, ClientY = 600 });
            Assert.Empty(cut.FindAll("#pipelines-editor-menu"));
        }

        // Undo and redo, a typed field undone as one step, copy, paste and delete - through the toolbar the
        // keyboard drives too.
        [Fact]
        public void Undo_redo_copy_paste_and_delete_edit_the_canvas_and_what_is_saved()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            int Nodes() => cut.FindAll(".db-step-node").Count;
            void Add(string query)
            {
                cut.Find("#pipelines-editor-step-search").Input(query);
                cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            }

            bool Titled(string title) => cut.FindAll(".db-step-node__title").Any(node => node.TextContent.Trim() == title);

            Assert.True(cut.Find("#pipelines-editor-undo").HasAttribute("disabled"), "nothing to undo on a freshly loaded pipeline");
            Add("log");
            Add("records");
            cut.WaitForAssertion(() => Assert.Equal(2, Nodes()));

            // Typing a name is ONE step to undo, however many keys - and the step stays selected through it.
            cut.Find("#pipelines-editor-step-name").Input("R");
            cut.Find("#pipelines-editor-step-name").Input("Re");
            cut.Find("#pipelines-editor-step-name").Input("Render");
            cut.WaitForAssertion(() => Assert.True(Titled("Render")));
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.True(Titled("Records to CSV")));
            Assert.Equal(2, Nodes());
            Assert.Equal("Records to CSV", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));

            // The next undo takes the step away; redo brings it back, and its name.
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(1, Nodes()));
            cut.Find("#pipelines-editor-redo").Click();
            cut.WaitForAssertion(() => Assert.Equal(2, Nodes()));
            cut.Find("#pipelines-editor-redo").Click();
            cut.WaitForAssertion(() => Assert.True(Titled("Render")));
            Assert.True(cut.Find("#pipelines-editor-redo").HasAttribute("disabled"));

            // Copy, paste and delete a new step (added with nothing selected after the redo, so it stands alone).
            Add("delay");
            cut.WaitForAssertion(() => Assert.Equal(3, Nodes()));
            cut.Find("#pipelines-editor-copy").Click();
            // Waited for: a selection change queues its render (InvokeAsync), and under a loaded gate the copy's lands after it
            // (it failed once under the fast gate's load; eight of eight alone pass).
            cut.WaitForAssertion(() => Assert.Equal("Copied 1 step.", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            cut.Find("#pipelines-editor-paste").Click();
            cut.WaitForAssertion(() => Assert.Equal(4, Nodes()));
            Assert.Equal(2, cut.FindAll(".db-step-node__title").Count(title => title.TextContent.Trim() == "Delay"));

            // The pasted copy is selected: Delete removes it.
            cut.Find("#pipelines-editor-delete").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, Nodes()));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!;
            Assert.Equal(new[] { "Stomme.Log", "Stomme.ToCsv", "Stomme.Delay" }, saved.Steps.Select(step => step.Type));
            Assert.Equal("Render", saved.Steps[1].Name);
            Assert.Equal(new[] { (saved.Steps[0].Id, saved.Steps[1].Id) }, saved.Connections.Select(c => (c.SourceId, c.TargetId)));
        }

        // Ctrl+A selects every step shown, Escape lets go of them, and Ctrl+Enter does nothing on a pipeline with no published
        // version (there is no Run button to stand for) - each through the call the keyboard script makes.
        [Fact]
        public async Task Select_all_escape_and_run_from_the_keyboard()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            foreach (var query in new[] { "log", "delay", "records" })
            {
                cut.Find("#pipelines-editor-step-search").Input(query);
                cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            }

            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("selectAll"));
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node.is-selected").Count));
            cut.Find("#pipelines-editor-copy").Click();
            Assert.Equal("Copied 3 steps.", cut.Find("#pipelines-editor-message").TextContent.Trim());

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-step-node.is-selected")));
            cut.Find("#pipelines-editor-copy").Click();
            Assert.StartsWith("Select the steps to copy first", cut.Find("#pipelines-editor-message").TextContent.Trim());

            // The same words again are spoken again: the spoken copy's text changes - by a no-break space, not read -
            // since a region whose text did not change says nothing.
            var once = cut.Find("#pipelines-editor-say").TextContent;
            cut.Find("#pipelines-editor-copy").Click();
            Assert.NotEqual(once, cut.Find("#pipelines-editor-say").TextContent);
            Assert.StartsWith("Select the steps to copy first", cut.Find("#pipelines-editor-say").TextContent.Trim());

            // Not published: no Run button, and Ctrl+Enter starts nothing (the fake would throw, and the editor say so).
            Assert.Empty(cut.FindAll("#pipelines-editor-run"));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("run"));
            Assert.Empty(cut.FindAll(".db-pipeline-editor__error"));
        }

        // Problems before publish - on the step, in the list above the canvas, under the input - as the graph
        // changes; Save and publish saves a draft and publishes nothing while one remains; the list leads to the step.
        [Fact]
        public void Problems_show_as_the_graph_changes_and_hold_back_the_publish_until_fixed()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            Assert.Empty(cut.FindAll("#pipelines-editor-problems"));
            AngleSharp.Dom.IElement Node(string title) => cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == title);

            // A Write Line with no text: an error, on the step, in the list and under the input.
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("1 problem to fix before publishing", cut.Find("#pipelines-editor-problems-summary").TextContent.Trim()));
            Assert.Equal("error", Node("Write Line").GetAttribute("data-problems"));
            Assert.Equal("Text needs a value.", cut.Find("#pipelines-editor-input-problem-Text").TextContent.Trim());

            // A Delay that nothing leads to: a warning beside it.
            cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            cut.Find("#pipelines-editor-step-search").Input("delay");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("1 problem to fix before publishing, 1 warning", cut.Find("#pipelines-editor-problems-summary").TextContent.Trim()));
            Assert.Equal("warning", Node("Delay").GetAttribute("data-problems"));

            // Save and publish: the draft is saved, nothing published.
            cut.Find("#pipelines-editor-publish").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.StartsWith("Not published: 1 problem to fix first", cut.Find("#pipelines-editor-error").TextContent.Trim());
            Assert.Equal(0, service.Published);

            // The list leads to the step; a value fixes it, and what is left holds nothing back.
            cut.FindAll("#pipelines-editor-problems li.is-error button")[0].Click();
            cut.WaitForAssertion(() => Assert.Contains("is-selected", Node("Write Line").ClassName));
            cut.Find("#pipelines-editor-input-Text").Input("hello");
            cut.WaitForAssertion(() => Assert.Equal("1 warning - nothing that keeps the pipeline from being published", cut.Find("#pipelines-editor-problems-summary").TextContent.Trim()));
            Assert.Null(Node("Write Line").GetAttribute("data-problems"));
            Assert.Empty(cut.FindAll("#pipelines-editor-input-problem-Text"));

            cut.Find("#pipelines-editor-publish").Click();
            cut.WaitForAssertion(() => Assert.Equal(1, service.Published));
            Assert.Equal("Published version 1.", cut.Find("#pipelines-editor-message").TextContent.Trim());
        }

        // An input as an expression - switched to from its value, the variables (and a record's fields)
        // offered to insert, a preview of what the step gets, a name no variable has flagged, and the template saved.
        [Fact]
        public void An_input_becomes_an_expression_with_suggestions_a_preview_and_its_problems()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));

            // A variable holding a record from the start.
            cut.Find("#pipelines-editor-variable-name").Change("person");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[id^=pipelines-editor-variable-value-]")));
            cut.Find("[id^=pipelines-editor-variable-value-]").Change("{\"name\":\"Ada\",\"town\":\"London\"}");

            // A Write Line, its text switched to an expression.
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-source-Text")));
            cut.Find("#pipelines-editor-source-Text").Change("=expression");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-expression-Text")));
            string[] Suggestions() => cut.FindAll("#pipelines-editor-suggestions-Text button").Select(button => button.TextContent.Trim()).ToArray();
            // The pipeline's variables first, then the built-ins every expression reads (PipelineBuiltIns).
            Assert.Equal(new[] { "{{ person }}", "{{ person.name }}", "{{ person.town }}", "{{ $now }}", "{{ $today }}" }, Suggestions().Take(5));
            Assert.All(cut.FindAll("#pipelines-editor-suggestions-Text details button"), button => Assert.StartsWith("{{ $", button.TextContent.Trim())); // the built-ins folded
            Assert.Equal(3, cut.FindAll("#pipelines-editor-suggestions-Text > button").Count);

            // Typing after an open {{ narrows them - every match in sight; taking one completes it; the preview shows what the step would get.
            cut.Find("#pipelines-editor-expression-Text").Input("Hello {{ na");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "{{ person.name }}", "{{ $pipeline.name }}" }, Suggestions()));
            Assert.Empty(cut.FindAll("#pipelines-editor-suggestions-Text details"));
            cut.Find("#pipelines-editor-suggestions-Text button").Click();
            cut.WaitForAssertion(() => Assert.Equal("Hello {{ person.name }}", cut.Find("#pipelines-editor-expression-Text").GetAttribute("value")));
            Assert.Equal("With the starting values: Hello Ada", cut.Find("#pipelines-editor-preview-Text").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-problems li.is-error"));

            // A name no variable has: the preview says so, and it is a problem of the step's.
            cut.Find("#pipelines-editor-expression-Text").Input("Hello {{ persn.name }}");
            cut.WaitForAssertion(() => Assert.Contains("is-error", cut.Find("#pipelines-editor-preview-Text").ClassName));
            Assert.EndsWith("There is no variable named 'persn'.", cut.Find("#pipelines-editor-preview-Text").TextContent.Trim());
            Assert.Equal("Text reads {{ persn }}, but no variable is named 'persn'.", cut.Find("#pipelines-editor-input-problem-Text").TextContent.Trim());

            cut.Find("#pipelines-editor-expression-Text").Input("Hello {{ person.town }}");
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!.Steps.Single();
            Assert.Equal("Hello {{ person.town }}", saved.InputExpressions["Text"]);
            Assert.DoesNotContain("Text", saved.Inputs.Keys);

            // Back to a value: a template cannot be one, so the field starts empty and the expression is gone.
            cut.Find("#pipelines-editor-source-Text").Change("");
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-expression-Text")));
            Assert.Equal("", cut.Find("#pipelines-editor-input-Text").GetAttribute("value") ?? "");
        }

        // Set Variable's Variable is the variable the step sets - chosen among the pipeline's, never a value, an expression
        // or a script - and the step is marked until one is chosen.
        [Fact]
        public void The_variable_a_step_sets_is_chosen_among_the_pipelines_and_marked_until_it_is()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("total");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[id^=pipelines-editor-variable-value-]")));

            cut.Find("#pipelines-editor-step-search").Input("set variable");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Variable")));
            Assert.Empty(cut.FindAll("#pipelines-editor-source-Variable"));
            Assert.Equal(new[] { "(choose a variable)", "total" }, cut.FindAll("#pipelines-editor-input-Variable option").Select(option => option.TextContent.Trim()));
            Assert.Equal("Variable needs a variable: the one this step sets.", cut.Find("#pipelines-editor-input-problem-Variable").TextContent.Trim());

            var total = cut.FindAll("#pipelines-editor-input-Variable option")[1].GetAttribute("value");
            cut.Find("#pipelines-editor-input-Variable").Change(total);
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-input-problem-Variable")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!.Steps.Single();
            Assert.Equal(total, saved.InputVariables["Variable"]);
            Assert.DoesNotContain("Variable", saved.Inputs.Keys);
        }

        // n8n reads an earlier node's output where it is needed: under an expression, the results of the steps that lead
        // to the step are offered; choosing one puts it in through the variable its Result saves to - made, named after the step
        // and bound on the spot when there is none, so it is kept across a pause as any variable is - and the next use of the same
        // step reuses that variable. One change to undo.
        [Fact]
        public void An_earlier_steps_result_is_put_into_an_expression_through_a_variable_made_for_it()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = "Stomme.FetchOrders", Version = 1, DisplayName = "Fetch orders", Category = "Data", Description = "Read the orders.",
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("fetch orders");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" }); // after it, connected
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-source-Text")));
            cut.Find("#pipelines-editor-source-Text").Change("=expression");

            var offered = cut.Find("#pipelines-editor-earlier-Text").QuerySelectorAll("button");
            Assert.Equal("Fetch orders", Assert.Single(offered).TextContent.Trim());
            offered[0].Click();
            cut.WaitForAssertion(() => Assert.Equal("{{ fetch_orders }}", cut.Find("#pipelines-editor-expression-Text").GetAttribute("value")));
            Assert.Equal("Fetch orders - fetch_orders", cut.Find("#pipelines-editor-earlier-Text button").TextContent.Trim()); // saved as it now

            // The next use of the same step reuses its variable.
            cut.Find("#pipelines-editor-earlier-Text button").Click();
            cut.WaitForAssertion(() => Assert.Equal("{{ fetch_orders }}{{ fetch_orders }}", cut.Find("#pipelines-editor-expression-Text").GetAttribute("value")));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var variable = Assert.Single(service.Saved!.Variables);
            Assert.Equal(("fetch_orders", "Object"), (variable.Name, variable.TypeName));
            var fetch = service.Saved.Steps.Single(step => step.Type == "Stomme.FetchOrders");
            Assert.Equal(variable.Id, fetch.OutputVariables["Result"]);
            Assert.Equal("{{ fetch_orders }}{{ fetch_orders }}", service.Saved.Steps.Single(step => step.Type == "Stomme.WriteLine").InputExpressions["Text"]);

            // One change each: two Undos take both uses back, the variable and its binding with the first.
            cut.Find("#pipelines-editor-undo").Click();
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("Fetch orders", cut.Find("#pipelines-editor-earlier-Text button").TextContent.Trim()));
        }

        // n8n's pinned data: what a step produced in a test is pinned with a click - marked on the step, shown in its
        // inspector to edit, saved with the pipeline and sent with a test of it (the server hands it on in place of running the
        // step); Unpin lets the step run again, and Undo takes either back.
        [Fact]
        public void A_steps_result_is_pinned_from_a_test_edited_saved_and_unpinned()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-step")));
            Assert.Empty(cut.FindAll("#pipelines-editor-pinned"));

            cut.Find("#pipelines-editor-test-step").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-pin-test")));
            cut.Find("#pipelines-editor-pin-test").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-pinned")));
            Assert.Equal("[{\"name\":\"Ada\"}]", cut.Find("#pipelines-editor-pinned-result").GetAttribute("value")); // compact, as the step made it
            Assert.Equal("pinned", cut.Find(".db-step-node__pinned").TextContent.Trim());
            Assert.EndsWith("is pinned: a test hands its pinned result on in place of running it.", cut.Find("#pipelines-editor-message").TextContent.Trim());

            // Edited as n8n's pinned output is, saved with the pipeline, and sent with a test of it.
            cut.Find("#pipelines-editor-pinned-result").Input("[{\"name\":\"Grace\"}]");
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal("[{\"name\":\"Grace\"}]", service.Saved!.Steps.Single().PinnedResult);
            cut.Find("#pipelines-editor-test-draft").Click();
            cut.WaitForAssertion(() => Assert.True(service.Tested!.Whole));
            Assert.Equal("[{\"name\":\"Grace\"}]", service.Tested!.Graph.Steps.Single().PinnedResult);

            // Unpinned: the step runs in a test again; Undo pins it back.
            cut.Find("#pipelines-editor-unpin").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-pinned")));
            Assert.Empty(cut.FindAll(".db-step-node__pinned"));
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".db-step-node__pinned")));
        }

        // Test the selected step - the graph as it stands, the step and the variables' values (the starting
        // ones, or what the author types: kept, and reset on request) go to the server; what it did comes back beside it.
        [Fact]
        public void Test_step_sends_the_step_and_the_values_and_shows_what_it_did()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("who");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[id^=pipelines-editor-variable-value-]")));
            cut.Find("[id^=pipelines-editor-variable-value-]").Change("Grace");

            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-step")));
            Assert.Equal("Grace", cut.Find("[id^=pipelines-editor-test-value-]").GetAttribute("value"));

            // The author pins another value and tests.
            cut.Find("[id^=pipelines-editor-test-value-]").Input("Ada");
            cut.Find("#pipelines-editor-test-step").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-result")));
            var tested = service.Tested!;
            var step = Assert.Single(tested.Graph.Steps);
            Assert.Equal((step.Id, "Stomme.WriteLine"), (tested.StepId, step.Type));
            Assert.Equal("Ada", tested.Values["who"]);
            Assert.StartsWith("Ran on its own: done", cut.Find("#pipelines-editor-test-status").TextContent.Trim());
            Assert.Contains("Hello Ada", cut.Find("#pipelines-editor-test-input").TextContent);
            Assert.Equal("wrote a line", cut.Find("#pipelines-editor-test-log").TextContent.Trim());

            // Reset: back to the starting value.
            cut.Find("#pipelines-editor-test-reset").Click();
            cut.WaitForAssertion(() => Assert.Equal("Grace", cut.Find("[id^=pipelines-editor-test-value-]").GetAttribute("value")));

            // A first step has nothing before it, so no "up to here"; a step placed after it does, and asks for the steps
            // leading to it to run first - the result lists each one beside its own.
            Assert.Empty(cut.FindAll("#pipelines-editor-test-up-to-here"));
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-up-to-here")));
            cut.Find("#pipelines-editor-test-up-to-here").Click();
            cut.WaitForAssertion(() => Assert.True(service.Tested!.UpToHere));
            Assert.Equal(2, service.Tested.Graph.Steps.Count);
            cut.WaitForAssertion(() => Assert.StartsWith("Ran with the steps before it: done", cut.Find("#pipelines-editor-test-status").TextContent.Trim()));
            Assert.Equal(new[] { "Write Line - done" }, cut.FindAll("#pipelines-editor-test-steps li").Select(item => item.TextContent.Trim()));

            // A step before it that failed and took its Failed path is listed as that, not as done.
            cut.Find("#pipelines-editor-find").Input("write line");
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".db-pipeline-editor__find-result")));
            cut.FindAll(".db-pipeline-editor__find-result")[0].Click();
            cut.WaitForAssertion(() => Assert.Equal("Write Line", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            cut.Find("#pipelines-editor-on-failure").Change("failed-path");
            cut.Find("#pipelines-editor-find").Input("log");
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".db-pipeline-editor__find-result")));
            cut.FindAll(".db-pipeline-editor__find-result")[0].Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-up-to-here")));
            cut.Find("#pipelines-editor-test-up-to-here").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Write Line - failed, and took its Failed path: boom" }, cut.FindAll("#pipelines-editor-test-steps li").Select(item => item.TextContent.Trim())));
        }

        // n8n's mapping: each variable, and each field of a record it holds, is offered to drag into an input; one dropped
        // on a value makes the value an expression ending in it (what was typed kept before it); a drop that is not a placeholder,
        // or on an input the step does not have, changes nothing.
        [Fact]
        public async Task A_variable_dropped_on_a_value_makes_it_an_expression_reading_it()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("person");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[id^=pipelines-editor-variable-value-]")));
            cut.Find("[id^=pipelines-editor-variable-value-]").Change("{\"name\":\"Ada\",\"town\":\"London\"}");
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Text")));

            // The pipeline's variables, then the built-ins (PipelineBuiltIns) - each can be dragged in.
            Assert.Equal(new[] { "{{ person }}", "{{ person.name }}", "{{ person.town }}", "{{ $now }}", "{{ $today }}", "{{ $run }}" },
                cut.FindAll("#pipelines-editor-drag-fields [data-drag-text]").Select(chip => chip.GetAttribute("data-drag-text")).Take(6));
            Assert.All(cut.FindAll("#pipelines-editor-drag-fields [data-drag-text]"), chip => Assert.Equal("true", chip.GetAttribute("draggable")));
            // The pipeline's own in sight; the built-ins folded under a toggle, closed, so the step's inputs stay in view.
            Assert.Equal(3, cut.FindAll("#pipelines-editor-drag-fields > [data-drag-text]").Count);
            Assert.False(cut.Find("#pipelines-editor-drag-builtins").HasAttribute("open"));
            Assert.All(cut.FindAll("#pipelines-editor-drag-builtins [data-drag-text]"), chip => Assert.StartsWith("{{ $", chip.GetAttribute("data-drag-text")));

            cut.Find("#pipelines-editor-input-Text").Input("Hello");
            await cut.InvokeAsync(() => cut.Instance.OnDropIntoInput("Text", "{{ person.name }}"));
            cut.WaitForAssertion(() => Assert.Equal("Hello {{ person.name }}", cut.Find("#pipelines-editor-expression-Text").GetAttribute("value")));
            Assert.Contains("Hello Ada", cut.Find("#pipelines-editor-preview-Text").TextContent);

            await cut.InvokeAsync(() => cut.Instance.OnDropIntoInput("Nope", "{{ person }}"));
            await cut.InvokeAsync(() => cut.Instance.OnDropIntoInput("Text", "not a placeholder"));
            Assert.Equal("Hello {{ person.name }}", cut.Find("#pipelines-editor-expression-Text").GetAttribute("value"));

            // The wide view's values to map, as n8n's input panel: each of the pipeline's fields with what it holds, draggable.
            cut.Find("#pipelines-editor-wide").Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-wide-available li")));
            var rows = cut.FindAll("#pipelines-editor-wide-available li").Select(row => (row.QuerySelector("[data-drag-text]")!.GetAttribute("data-drag-text"), row.QuerySelector(".db-pipeline-editor__available-value")!.TextContent.Trim())).ToList();
            Assert.Contains(("{{ person.name }}", "Ada"), rows);
            Assert.Contains(("{{ person.town }}", "London"), rows);
            Assert.DoesNotContain(rows, row => row.Item1!.StartsWith("{{ $"));
            Assert.All(cut.FindAll("#pipelines-editor-wide-available [data-drag-text]"), chip => Assert.Equal("true", chip.GetAttribute("draggable")));
        }

        // Find on the canvas - what is typed lists the steps that hold it, each with what matched; one chosen is
        // selected, and Enter goes to the next (round to the first); nothing found is said.
        [Fact]
        public void A_step_is_found_on_the_canvas_and_gone_to()
        {
            var service = new FakePipelinesService
            {
                Loaded = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "wait", Type = "Stomme.Delay", Name = "Wait for the bank", Position = new GraphPosition { X = 30, Y = 40 } },
                        new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 290, Y = 40 }, Inputs = { ["Text"] = "the bank answered" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "wait", TargetId = "say" } },
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-find")));
            Assert.Empty(cut.FindAll("#pipelines-editor-find-results"));

            cut.Find("#pipelines-editor-find").Input("BANK");
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#pipelines-editor-find-results li").Count));
            Assert.Contains("input 'Text': the bank answered", cut.FindAll("#pipelines-editor-find-results li")[1].TextContent);

            cut.FindAll(".db-pipeline-editor__find-result")[1].Click();
            cut.WaitForAssertion(() => Assert.Equal("Say", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));
            cut.Find("#pipelines-editor-find").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("Wait for the bank", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            cut.Find("#pipelines-editor-find").Input("nothing like it");
            cut.WaitForAssertion(() => Assert.Contains("No step holds \"nothing like it\"", cut.Find("#pipelines-editor-find-none").TextContent));
            cut.Find("#pipelines-editor-find").KeyDown(new KeyboardEventArgs { Key = "Escape" });
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-find-none")));
        }

        // Found driving: a value naming a variable in {{ }} is sent as typed - the input says so, and one click makes it
        // an expression holding the same text, which fills it in.
        [Fact]
        public void A_value_with_a_placeholder_is_made_an_expression_in_one_click()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("who");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Text")));

            cut.Find("#pipelines-editor-input-Text").Input("Hello {{ who }}");
            cut.WaitForAssertion(() => Assert.Contains("sent as typed", cut.Find("#pipelines-editor-input-problem-Text").TextContent));
            cut.Find("#pipelines-editor-make-expression-Text").Click();

            cut.WaitForAssertion(() => Assert.Equal("Hello {{ who }}", cut.Find("#pipelines-editor-expression-Text").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#pipelines-editor-make-expression-Text"));
            Assert.Empty(cut.FindAll("#pipelines-editor-input-Text"));
        }

        // n8n's pinned data: a value typed for a test is kept WITH the pipeline on request - saved with it, marked kept,
        // and what the next session's tests use - and forgotten on request, back to the starting value.
        [Fact]
        public void Test_values_are_kept_with_the_pipeline_on_request_and_forgotten_on_request()
        {
            var service = new FakePipelinesService
            {
                Loaded = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "var-who", Name = "who", TypeName = "String", Value = "Grace", TestValue = "Ada" } } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-test-step")));

            // Opened with a kept value: shown, marked, used - and nothing to keep or reset yet.
            Assert.Equal("Ada", cut.Find("#pipelines-editor-test-value-var-who").GetAttribute("value"));
            Assert.Equal("who (kept)", cut.Find("label[for=pipelines-editor-test-value-var-who]").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-test-keep"));
            Assert.Empty(cut.FindAll("#pipelines-editor-test-reset"));

            // Another value typed is the session's until kept; kept, it is the pipeline's, and the test uses it.
            cut.Find("#pipelines-editor-test-value-var-who").Input("Linus");
            cut.Find("#pipelines-editor-test-keep").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-test-keep")));
            Assert.Equal("Linus", cut.Find("#pipelines-editor-test-value-var-who").GetAttribute("value"));
            cut.Find("#pipelines-editor-test-step").Click();
            cut.WaitForAssertion(() => Assert.Equal("Linus", service.Tested!.Values["who"]));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(("Grace", "Linus"), (service.Saved!.Variables.Single().Value, service.Saved.Variables.Single().TestValue));

            // Forgotten: back to the starting value, and the next save keeps none.
            cut.Find("#pipelines-editor-test-forget").Click();
            cut.WaitForAssertion(() => Assert.Equal("Grace", cut.Find("#pipelines-editor-test-value-var-who").GetAttribute("value")));
            Assert.Equal("who", cut.Find("label[for=pipelines-editor-test-value-var-who]").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-test-forget"));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Null(service.Saved!.Variables.Single().TestValue));
        }

        // The versions - listed with who and when, compared with the canvas in words, opened read-only (nothing
        // can be changed or saved until back), and restored onto the canvas as an ordinary change Undo takes back.
        [Fact]
        public void Versions_are_compared_opened_read_only_and_restored_as_a_change_undo_takes_back()
        {
            var service = new FakePipelinesService();
            service.Versions.Add(new PipelineVersionView { Id = "v-1", Version = 1, IsPublished = true, CreatedOnUtc = new DateTime(2026, 10, 6, 1, 0, 0, DateTimeKind.Utc), SavedBy = "ada" });
            service.VersionGraphs[1] = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "old", Type = "Stomme.Delay", Name = "Wait", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            string[] Titles() => cut.FindAll(".db-step-node__title").Select(title => title.TextContent.Trim()).ToArray();
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Log" }, Titles()));

            cut.Find("#pipelines-editor-versions-toggle").Click();
            cut.WaitForAssertion(() => Assert.Contains("Version 1 · published · 2026-10-06 01:00 UTC · saved by ada", cut.Find("#pipelines-editor-version-1").TextContent));

            // Compared with the canvas: what the canvas has that version 1 has not, and the other way.
            cut.Find("#pipelines-editor-version-compare-1").Click();
            cut.WaitForAssertion(() => Assert.Contains("Added step 'Log'", cut.Find("#pipelines-editor-version-diff").TextContent));
            Assert.Contains("Removed step 'Wait'", cut.Find("#pipelines-editor-version-diff").TextContent);

            // Opened: read-only - no search, no save - and back to the canvas as it was.
            cut.Find("#pipelines-editor-version-open-1").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Wait" }, Titles()));
            Assert.Contains("Version 1, read-only.", cut.Find("#pipelines-editor-viewing").TextContent);
            Assert.True(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("#pipelines-editor-save"));
            cut.Find("#pipelines-editor-viewing-back").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Log" }, Titles()));
            Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled"));

            // Restored: on the canvas as a change, which Undo takes back.
            cut.Find("#pipelines-editor-version-restore-1").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Wait" }, Titles()));
            Assert.StartsWith("Version 1 is on the canvas", cut.Find("#pipelines-editor-message").TextContent.Trim());
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Log" }, Titles()));
        }

        // A version compared is marked on the canvas - a step new since it ("new"), one changed ("changed", what changed on
        // hover), one whose body changed ("changed inside"), a new connection in the theme's "ok" colour - kept up with every change
        // and undo, hidden while a version is open read-only, each line a way to its step, and gone when the author stops comparing.
        [Fact]
        public void A_version_compared_is_marked_on_the_canvas_until_the_author_stops_comparing()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry { Type = "Stomme.ForEach", Version = 1, DisplayName = "For Each", Category = "Control Flow", Description = "Each item in turn.", Bodies = { "Body" } });
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Text"] = "hello" } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Third", Position = new GraphPosition { X = 550, Y = 40 } },
                    new PipelineGraphStep { Id = "loop", Type = "Stomme.ForEach", Name = "Each", Position = new GraphPosition { X = 30, Y = 220 } },
                    new PipelineGraphStep { Id = "inner", Type = "Stomme.Log", Name = "Inner", Position = new GraphPosition { X = 30, Y = 40 }, Parent = "loop", ParentPort = "Body", Inputs = { ["Text"] = "each" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" }, new PipelineGraphConnection { SourceId = "b", TargetId = "c" } },
            };

            // Version 1: First said something else, so did the step in the loop, and Third was not there yet.
            var first = System.Text.Json.JsonSerializer.Deserialize<PipelineGraph>(System.Text.Json.JsonSerializer.Serialize(service.Loaded))!;
            first.Steps.Single(step => step.Id == "a").Inputs["Text"] = "hi";
            first.Steps.Single(step => step.Id == "inner").Inputs["Text"] = "every";
            first.Steps.RemoveAll(step => step.Id == "c");
            first.Connections.RemoveAll(connection => connection.TargetId == "c");
            service.Versions.Add(new PipelineVersionView { Id = "v-1", Version = 1, IsPublished = true, CreatedOnUtc = new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc) });
            service.VersionGraphs[1] = first;
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            AngleSharp.Dom.IElement Node(string title) => cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == title);
            string Marked(string title) => Node(title).GetAttribute("data-change") ?? "";
            string Badge(string title) => Node(title).QuerySelector(".db-step-node__change")?.TextContent.Trim() ?? "";
            int NewConnections() => cut.FindAll("g.diagram-link").Count(link => link.QuerySelectorAll("path[stroke='var(--db-ok)']").Length > 0);
            Assert.Equal(0, NewConnections());

            cut.Find("#pipelines-editor-versions-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-version-compare-1")));
            cut.Find("#pipelines-editor-version-compare-1").Click();
            cut.WaitForAssertion(() => Assert.Equal("added", Marked("Third")));
            Assert.Equal("new since version 1", Badge("Third"));
            Assert.Equal(("changed", "changed since version 1"), (Marked("First"), Badge("First")));
            Assert.Equal("step 'First', input 'Text': 'hi' → 'hello'", Node("First").QuerySelector(".db-step-node__change")!.GetAttribute("title"));
            Assert.Equal(("changed", "changed inside since version 1"), (Marked("Each"), Badge("Each")));
            Assert.Equal("step 'Inner', input 'Text': 'every' → 'each'", Node("Each").QuerySelector(".db-step-node__change")!.GetAttribute("title"));
            Assert.Equal(("", ""), (Marked("Second"), Badge("Second")));
            Assert.Equal(1, NewConnections()); // Second -> Third, drawn in the theme's colour; First -> Second as ever
            Assert.Equal("Since version 1: 1 new step · 2 changed steps · 1 new connection - marked on the canvas.", cut.Find("#pipelines-editor-comparing-summary").TextContent.Trim());

            // Kept up with the canvas: Second switched off is a change of it, and Undo takes that back.
            Node("Second").QuerySelector(".db-step-node__action--toggle")!.Click();
            cut.WaitForAssertion(() => Assert.Equal(("changed", "changed since version 1"), (Marked("Second"), Badge("Second"))));
            Assert.Contains("1 new step · 3 changed steps", cut.Find("#pipelines-editor-comparing-summary").TextContent);
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("", Marked("Second")));

            // A line about a step is a way to it.
            cut.FindAll(".db-pipeline-editor__change").Single(line => line.TextContent.Trim() == "Added step 'Third'").Click();
            cut.WaitForAssertion(() => Assert.Contains("is-selected", Node("Third").ClassName));

            // A version open read-only shows no marks - they are the canvas's - and they are back with it.
            cut.Find("#pipelines-editor-version-open-1").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            Assert.Empty(cut.FindAll(".db-step-node[data-change]"));
            Assert.Empty(cut.FindAll("#pipelines-editor-comparing"));
            Assert.Equal(0, NewConnections());
            cut.Find("#pipelines-editor-viewing-back").Click();
            cut.WaitForAssertion(() => Assert.Equal("added", Marked("Third")));
            Assert.Equal(1, NewConnections());

            // Stopped: no marks, no banner, no list.
            cut.Find("#pipelines-editor-comparing-stop").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-step-node[data-change]")));
            Assert.Empty(cut.FindAll("#pipelines-editor-comparing"));
            Assert.Empty(cut.FindAll("#pipelines-editor-version-diff"));
            Assert.Equal(0, NewConnections());
        }

        // n8n's AI workflow builder: what the author asks goes with the canvas as it stands, and what comes back is ONE
        // change - marked on the canvas as the AI's, with what the AI said - saved as any change is and taken back by Undo. A refusal
        // says why and changes nothing; a canvas the author changed while the model worked is left as the author left it.
        [Fact]
        public async Task Build_with_ai_puts_the_ais_change_on_the_canvas_as_one_marked_change_undo_takes_back()
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = true } };
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Text"] = "hello" } } } };
            service.Design = graph =>
            {
                graph.Steps[0].Inputs["Text"] = "hello there";
                graph.Steps.Add(new PipelineGraphStep { Id = "b", Type = "Stomme.Log", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 }, Inputs = { ["Text"] = "bye" } });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "a", TargetId = "b" });
                return graph;
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            AngleSharp.Dom.IElement Node(string title) => cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == title);
            string Marked(string title) => Node(title).GetAttribute("data-change") ?? "";
            string Badge(string title) => Node(title).QuerySelector(".db-step-node__change")?.TextContent.Trim() ?? "";

            cut.Find("#pipelines-editor-ai-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-ai-instruction")));
            Assert.True(cut.Find("#pipelines-editor-ai-ask").HasAttribute("disabled")); // nothing asked yet
            cut.Find("#pipelines-editor-ai-instruction").Input("Say goodbye after the greeting");
            cut.Find("#pipelines-editor-ai-ask").Click();

            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            var asked = Assert.Single(service.Designs);
            Assert.Equal("Say goodbye after the greeting", asked.Instruction);
            Assert.Equal("hello", Assert.Single(asked.Graph.Steps).Inputs["Text"]); // the canvas as it stood
            Assert.Equal(("added", "added by the AI"), (Marked("Second"), Badge("Second")));
            Assert.Equal(("changed", "changed by the AI"), (Marked("First"), Badge("First")));
            Assert.Equal("The AI's change: 1 new step · 1 changed step · 1 new connection - marked on the canvas.", cut.Find("#pipelines-editor-comparing-summary").TextContent.Trim());
            Assert.Equal("Stop marking", cut.Find("#pipelines-editor-comparing-stop").TextContent.Trim());
            Assert.Equal("The AI says: Added what you asked for.", cut.Find("#pipelines-editor-ai-summary").TextContent.Trim());
            Assert.StartsWith("The AI's change is on the canvas (1 new step · 1 changed step · 1 new connection), marked on it", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.True(string.IsNullOrEmpty(cut.Find("#pipelines-editor-ai-instruction").GetAttribute("value")));

            // Saved as any change is.
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(new[] { "First", "Second" }, service.Saved!.Steps.Select(step => step.Name));
            Assert.Equal("hello there", service.Saved.Steps[0].Inputs["Text"]);

            // One change: Undo takes all of it back, and the marks follow the canvas.
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            Assert.Equal("The AI's change: no difference.", cut.Find("#pipelines-editor-comparing-summary").TextContent.Trim());
            Assert.Empty(cut.FindAll(".db-step-node[data-change]"));

            // A refusal says why, and changes nothing; Ctrl+Enter in the box asks, as the button does.
            service.DesignRefusal = new InvalidOperationException("The AI changed nothing. It said: Which address should it mail?");
            cut.Find("#pipelines-editor-ai-instruction").Input("Mail it");
            cut.Find("#pipelines-editor-ai-instruction").KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });
            cut.WaitForAssertion(() => Assert.Equal("The AI did not change the pipeline: The AI changed nothing. It said: Which address should it mail?", cut.Find("#pipelines-editor-error").TextContent.Trim()));
            Assert.Equal(2, service.Designs.Count);
            Assert.Single(cut.FindAll(".db-step-node"));

            // The author goes on editing while the model works: its answer is not put over what the author did.
            service.DesignRefusal = null;
            var hold = new TaskCompletionSource<bool>();
            service.Hold = hold;
            cut.Find("#pipelines-editor-ai-instruction").Input("Say goodbye after the greeting");
            cut.Find("#pipelines-editor-ai-ask").Click();
            cut.WaitForAssertion(() => Assert.Equal("Working on it…", cut.Find("#pipelines-editor-ai-ask").TextContent.Trim()));
            Assert.True(cut.Find("#pipelines-editor-ai-instruction").HasAttribute("disabled"));
            cut.Find("#pipelines-editor-add-note").Click();
            await cut.InvokeAsync(() => hold.SetResult(true));
            cut.WaitForAssertion(() => Assert.Equal("The canvas changed while the AI worked, so its change was not put on it - ask again.", cut.Find("#pipelines-editor-error").TextContent.Trim()));
            Assert.Single(cut.FindAll(".db-step-node"));
            Assert.Equal("Ask", cut.Find("#pipelines-editor-ai-ask").TextContent.Trim());
        }

        // An answer without a change - a question about the pipeline, or one the AI asks back - is shown in the panel, the canvas left
        // as it is and what was asked kept in the box to add to.
        [Fact]
        public void An_answer_without_a_change_is_shown_and_the_canvas_left_as_it_is()
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = true }, Answer = "It logs a greeting to whoever 'who' names." };
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Text"] = "hello" } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            cut.Find("#pipelines-editor-ai-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-ai-instruction")));
            cut.Find("#pipelines-editor-ai-instruction").Input("What does this pipeline do?");
            cut.Find("#pipelines-editor-ai-ask").Click();

            cut.WaitForAssertion(() => Assert.Equal("The AI says: It logs a greeting to whoever 'who' names.", cut.Find("#pipelines-editor-ai-summary").TextContent.Trim()));
            Assert.Equal("The AI answered without changing the canvas.", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-error"));
            Assert.Empty(cut.FindAll("#pipelines-editor-comparing"));
            Assert.Empty(cut.FindAll(".db-step-node[data-change]"));
            Assert.DoesNotContain("unsaved", cut.Find(".db-pipeline-editor__meta").TextContent);

            // A conversation: the exchange shown, and told to the AI with the next request - so a question asked back is answered.
            Assert.Equal("You: What does this pipeline do?", cut.Find("#pipelines-editor-ai-turns .db-pipeline-editor__ai-asked").TextContent.Trim());
            Assert.True(string.IsNullOrEmpty(cut.Find("#pipelines-editor-ai-instruction").GetAttribute("value")));
            service.Answer = "It is on every hour.";
            cut.Find("#pipelines-editor-ai-instruction").Input("And how often does it run?");
            cut.Find("#pipelines-editor-ai-ask").Click();
            cut.WaitForAssertion(() => Assert.Equal("The AI says: It is on every hour.", cut.Find("#pipelines-editor-ai-summary").TextContent.Trim()));
            var second = service.Designs[1];
            Assert.Equal(("What does this pipeline do?", "It logs a greeting to whoever 'who' names."), (Assert.Single(second.Earlier).Asked, second.Earlier[0].Said));
            Assert.Equal(2, cut.FindAll("#pipelines-editor-ai-turns .db-pipeline-editor__ai-asked").Count);

            // Forgotten on request: the next request starts a new one.
            cut.Find("#pipelines-editor-ai-new").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-ai-turns")));
            cut.Find("#pipelines-editor-ai-instruction").Input("Anything else?");
            cut.Find("#pipelines-editor-ai-ask").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, service.Designs.Count));
            Assert.Empty(service.Designs[2].Earlier);
        }

        // Offered where the server says this person may use the site's model (its capabilities answer that, the module's page reading
        // its own keys only) - in the toolbar, and on an empty canvas beside the first step - and nowhere else.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Build_with_ai_is_offered_only_where_the_server_says_the_person_may(bool ai)
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = ai } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-add-first")));

            Assert.Equal(ai, cut.FindAll("#pipelines-editor-ai-toggle").Count == 1);
            Assert.Equal(ai, cut.FindAll("#pipelines-editor-add-ai").Count == 1);
            if (!ai)
            {
                return;
            }

            cut.Find("#pipelines-editor-add-ai").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-ai-instruction")));
            Assert.Equal("true", cut.Find("#pipelines-editor-ai-toggle").GetAttribute("aria-expanded"));
            cut.Find("#pipelines-editor-ai-instruction").KeyDown(new KeyboardEventArgs { Key = "Escape" });
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-ai")));
            Assert.Empty(service.Designs);
        }

        // n8n's "fix with AI": the problems list asks the builder to fix what it lists - the request written out, as the
        // list words it - and the change comes back marked, the problem gone.
        [Fact]
        public void Fix_with_ai_asks_the_builder_to_fix_what_the_editor_marks()
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = true } };
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "w", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 30, Y = 40 } } } };
            service.Design = graph =>
            {
                graph.Steps[0].Inputs["Text"] = "hello";
                return graph;
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("1 problem to fix before publishing", cut.Find("#pipelines-editor-problems-summary").TextContent.Trim()));
            AngleSharp.Dom.IElement Node(string title) => cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == title);

            cut.Find("#pipelines-editor-problems-ai").Click();

            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-problems")));
            Assert.Equal("Fix what the editor marks on this pipeline:\n- Problem at 'Say': Text needs a value.", Assert.Single(service.Designs).Instruction);
            Assert.Equal("changed", Node("Say").GetAttribute("data-change"));
            Assert.NotNull(cut.Find("#pipelines-editor-ai-summary")); // the panel open, with what the AI said
        }

        // A long list is asked about as far as the server takes a request - whole lines, never one cut in two.
        [Fact]
        public void Fix_with_ai_asks_about_no_more_than_the_server_takes()
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Ai = true } };
            for (var index = 0; index < 60; index++)
            {
                service.Loaded.Steps.Add(new PipelineGraphStep { Id = "w" + index, Type = "Stomme.WriteLine", Name = "Say number " + index, Position = new GraphPosition { X = 30 + 260 * (index % 6), Y = 40 + 140 * (index / 6) } });
            }

            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.StartsWith("60 problems to fix before publishing", cut.Find("#pipelines-editor-problems-summary").TextContent.Trim()));

            cut.Find("#pipelines-editor-problems-ai").Click();

            cut.WaitForAssertion(() => Assert.Single(service.Designs));
            var asked = service.Designs[0].Instruction;
            Assert.InRange(asked.Length, 1500, 2000);
            Assert.EndsWith(": Text needs a value.", asked); // the last line whole
            Assert.Contains("- Problem at 'Say number 0': Text needs a value.", asked);
        }

        // Another pipeline opened in this editor - a caller's link does it - starts clean of the last one's versions. A
        // version left open read-only would have put the LAST pipeline's canvas onto this one with "Back to the canvas".
        [Fact]
        public void Another_pipeline_opened_in_the_editor_leaves_the_last_ones_versions_behind()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "Mine", Position = new GraphPosition { X = 30, Y = 40 } } } };
            service.Others["def-2"] = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Stomme.Delay", Name = "Theirs", Position = new GraphPosition { X = 30, Y = 40 } } } };
            service.Versions.Add(new PipelineVersionView { Id = "v-1", Version = 1, IsPublished = true, CreatedOnUtc = new DateTime(2026, 10, 8, 1, 0, 0, DateTimeKind.Utc) });
            service.VersionGraphs[1] = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "old", Type = "Stomme.Delay", Name = "Old", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            string[] Titles() => cut.FindAll(".db-step-node__title").Select(title => title.TextContent.Trim()).ToArray();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Mine" }, Titles()));

            // Version 1 compared, then open read-only.
            cut.Find("#pipelines-editor-versions-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-version-compare-1")));
            cut.Find("#pipelines-editor-version-compare-1").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-comparing")));
            cut.Find("#pipelines-editor-version-open-1").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Old" }, Titles()));

            // The other pipeline opened here, as its link does: its canvas, open to change, and nothing of the last one's versions.
            page.QueryString["definition"] = "def-2";
            cut.Render();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Theirs" }, Titles()));
            Assert.Empty(cut.FindAll("#pipelines-editor-viewing"));
            Assert.Empty(cut.FindAll("#pipelines-editor-comparing"));
            Assert.Empty(cut.FindAll("#pipelines-editor-versions"));
            Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled"));
        }

        // What was done to the pipeline open, from the site's activity: its own lines only, newest first, read when asked and
        // read again each time; hidden; another pipeline opened here starts with them hidden.
        [Fact]
        public void What_was_done_to_the_pipeline_is_shown_in_its_settings_when_asked()
        {
            var service = new FakePipelinesService();
            service.Audit.Add(new PipelineAuditEntryView { OccurredOnUtc = new DateTime(2026, 10, 9, 3, 24, 0, DateTimeKind.Utc), UserName = "ada", Action = "published", Detail = "what changed: faster", DefinitionId = "def-1" });
            service.Audit.Add(new PipelineAuditEntryView { OccurredOnUtc = new DateTime(2026, 10, 9, 3, 25, 0, DateTimeKind.Utc), UserName = "bo", Action = "archived", DefinitionId = "def-2" });
            service.Audit.Add(new PipelineAuditEntryView { OccurredOnUtc = new DateTime(2026, 10, 9, 3, 20, 0, DateTimeKind.Utc), UserName = "ada", Action = "started a run", DefinitionId = "def-1", RunId = "run-9" });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            static string Text(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

            var page = Page();
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-activity-toggle")));
            Assert.Empty(service.AuditAsked); // nothing read until asked
            cut.Find("#pipelines-editor-activity-toggle").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[]
            {
                "2026-10-09 03:24 UTC ada published - what changed: faster",
                "2026-10-09 03:20 UTC ada started a run - the run",
            }, cut.FindAll("#pipelines-editor-activity-list li").Select(Text).ToArray()));
            Assert.Equal(new[] { "def-1" }, service.AuditAsked);
            Assert.EndsWith("id=run-9", cut.Find("#pipelines-editor-activity-list a").GetAttribute("href"));

            cut.Find("#pipelines-editor-activity-toggle").Click();
            Assert.Empty(cut.FindAll("#pipelines-editor-activity-list"));
            cut.Find("#pipelines-editor-activity-toggle").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "def-1", "def-1" }, service.AuditAsked)); // read again

            page.QueryString["definition"] = "def-3";
            cut.Render();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-activity-list")));
            cut.Find("#pipelines-editor-activity-toggle").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-activity-none")));
        }

        // n8n's "Error workflow" setting, from the pipeline that fails: the pipelines that start when it fails (its own
        // handlers, a draft said to start nothing yet, and the ones watching every pipeline - never another's), and a handler made
        // in a click - watching it, beside it in its folder, published, linked. And another pipeline opened here says its OWN
        // callers and handlers, not the last one's.
        [Fact]
        public void What_starts_when_a_pipeline_fails_is_listed_and_a_handler_made_in_a_click()
        {
            var service = new FakePipelinesService { OpenedFolder = "Finance" };
            service.ExtraSteps.Add(new StepCatalogEntry { Type = "Stomme.WhenCalled", Version = 1, DisplayName = "When called by another pipeline", Category = "Triggers", Kind = "Trigger", Description = "Where a run another pipeline starts enters." });
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "in", Type = "Stomme.WhenCalled", Name = "Called", Position = new GraphPosition { X = 30, Y = 40 } } } };
            service.CallersOf["def-1"] = new List<PipelineCallerView> { new() { DefinitionId = "p1", Name = "Parent", StepId = "s1", StepName = "Run it", Waits = true } };
            service.CallersOf["def-2"] = new List<PipelineCallerView> { new() { DefinitionId = "p2", Name = "Other parent", StepId = "s2", StepName = "Start it", Waits = false } };
            service.Map = new PipelineMapView
            {
                Nodes =
                {
                    new PipelineMapNode { DefinitionId = "def-3", Name = "Picker", IsPublished = true },
                    new PipelineMapNode { DefinitionId = "h1", Name = "Tell ops", IsPublished = true },
                    new PipelineMapNode { DefinitionId = "h2", Name = "On any failure", IsPublished = true, WatchesEvery = true },
                    new PipelineMapNode { DefinitionId = "h3", Name = "Draft handler", IsPublished = false },
                    new PipelineMapNode { DefinitionId = "x", Name = "Someone else", IsPublished = true },
                    new PipelineMapNode { DefinitionId = "h4", Name = "Their handler", IsPublished = true },
                },
                Links =
                {
                    new PipelineMapLink { From = "def-3", To = "h1", Kind = PipelineLinks.OnFailure },
                    new PipelineMapLink { From = "def-3", To = "h3", Kind = PipelineLinks.OnFailure },
                    new PipelineMapLink { From = "x", To = "h4", Kind = PipelineLinks.OnFailure },
                    new PipelineMapLink { From = "def-3", To = "x", Kind = PipelineLinks.Calls },
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            static string Text(AngleSharp.Dom.IElement element) => System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

            // Its "When called" step says who calls it - and another pipeline's, opened here, says who calls THAT one.
            var page = Page();
            page.QueryString["step"] = "in";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Contains("Parent", cut.Find("#pipelines-editor-callers").TextContent));
            page.QueryString["definition"] = "def-2";
            cut.Render();
            cut.WaitForAssertion(() => Assert.Contains("Other parent", cut.Find("#pipelines-editor-callers").TextContent));
            Assert.DoesNotContain("its step 'Run it'", cut.Find("#pipelines-editor-callers").TextContent);

            // A third, nothing selected: what starts when it fails.
            page.QueryString["definition"] = "def-3";
            page.QueryString.Remove("step");
            cut.Render();
            cut.WaitForAssertion(() => Assert.Equal(new[]
            {
                "Tell ops - starts when this one fails",
                "Draft handler - starts when this one fails; a draft, so it does not start until it is published",
                "On any failure - starts when any pipeline fails",
            }, cut.FindAll("#pipelines-editor-on-failure-list li").Select(Text).ToArray()));
            Assert.EndsWith("definition=h1", cut.Find("#pipelines-editor-on-failure-list a").GetAttribute("href"));
            var reads = service.MapReads;

            // A handler made: watching this one, beside it in Finance, published, linked - and the list read again.
            cut.Find("#pipelines-editor-on-failure-make").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("Made 'When 'Picker' fails' and published it", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            Assert.Equal("When 'Picker' fails", service.Created.Single().Name);
            var (madeId, madeGraph) = service.SavedTo.Single(saved => saved.DefinitionId == "def-made-1");
            var watching = madeGraph.Steps.Single(step => step.Type == PipelineFailure.StepType);
            Assert.Equal(("def-3", "When 'Picker' fails"), (watching.Inputs["Pipeline"], watching.Name));
            Assert.Equal(new[] { ("def-made-1", "Finance") }, service.Moved.ToArray());
            Assert.Contains(madeId, service.PublishedIds);
            Assert.EndsWith("definition=def-made-1", cut.Find("#pipelines-editor-on-failure-open").GetAttribute("href"));
            cut.WaitForAssertion(() => Assert.Equal(reads + 1, service.MapReads));

            // Another pipeline opened here: its own handlers - none - and nothing of the last one's made.
            service.Map = new PipelineMapView { Nodes = { new PipelineMapNode { DefinitionId = "def-4", Name = "Picker", IsPublished = true } } };
            page.QueryString["definition"] = "def-4";
            cut.Render();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-on-failure-none")));
            Assert.Empty(cut.FindAll("#pipelines-editor-on-failure-open"));
        }

        // n8n's archive: an archived pipeline opens read-only - said, by whom, with no Save, no step search, its name not
        // editable - and Restore makes it the author's again, a draft.
        [Fact]
        public void An_archived_pipeline_opens_read_only_until_it_is_restored()
        {
            var service = new FakePipelinesService { ArchivedOn = new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc) };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.StartsWith("Archived 2026-10-08 by ada.", cut.Find("#pipelines-editor-archived").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-editor-save"));
            Assert.True(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled"));
            Assert.Equal("STRONG", cut.Find("#pipelines-editor-name").TagName);

            cut.Find("#pipelines-editor-restore").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-archived")));
            Assert.Equal(new[] { "def-1" }, service.Restored);
            Assert.NotNull(cut.Find("#pipelines-editor-save"));
            Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled"));
            Assert.Equal("Restored as a draft - publish it to run it again.", cut.Find("#pipelines-editor-message").TextContent.Trim());
        }

        // n8n's workflow name, edited where it is shown: the author clicks the pipeline's name, types a new one and what
        // it does, and Enter saves both into the draft - a published pipeline takes them when the draft is published. Escape
        // leaves them as they were, and nothing changed saves nothing.
        [Fact]
        public void The_pipelines_name_and_description_are_edited_in_the_editors_header()
        {
            var service = new FakePipelinesService { IsPublished = true };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("Picker", cut.Find("#pipelines-editor-name").TextContent.Trim()));
            Assert.Equal("BUTTON", cut.Find("#pipelines-editor-name").TagName);

            // Escape: as it was, nothing saved - the focus on the name typed into, then back on the name it came from.
            string Focused() => JSInterop.Invocations["stommePipelineEditorFocus"].Last().Arguments[0]!.ToString()!;
            cut.Find("#pipelines-editor-name").Click();
            cut.WaitForAssertion(() => Assert.Equal("Picker", cut.Find("#pipelines-editor-details-name").GetAttribute("value")));
            Assert.Equal("pipelines-editor-details-name", Focused());
            cut.Find("#pipelines-editor-details-name").Input("Something else");
            cut.Find("#pipelines-editor-details-name").KeyDown(new KeyboardEventArgs { Key = "Escape" });
            cut.WaitForAssertion(() => Assert.Equal("Picker", cut.Find("#pipelines-editor-name").TextContent.Trim()));
            Assert.Equal("pipelines-editor-name", Focused());
            Assert.Empty(service.Updated);

            // Saved as it was: nothing to save.
            cut.Find("#pipelines-editor-name").Click();
            cut.WaitForAssertion(() => Assert.Equal("Picker", cut.Find("#pipelines-editor-details-name").GetAttribute("value")));
            cut.Find("#pipelines-editor-details-save").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-details")));
            Assert.Empty(service.Updated);

            // Renamed and described: Enter saves both, into a new draft over the published version.
            cut.Find("#pipelines-editor-name").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-details-description")));
            cut.Find("#pipelines-editor-details-name").Input("  Nightly report ");
            cut.Find("#pipelines-editor-details-description").Input("Mails the day's orders");
            cut.Find("#pipelines-editor-details-description").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("Nightly report", cut.Find("#pipelines-editor-name").TextContent.Trim()));
            var (definitionId, request) = service.Updated.Single();
            Assert.Equal(("def-1", "Nightly report", "Mails the day's orders", (string?)null), (definitionId, request.Name, request.Description, request.Body));
            Assert.Equal("Renamed in a new draft, version 2 - publish it for the running pipeline to take it.", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.StartsWith("version 2 · draft", cut.Find(".db-pipeline-editor__meta").TextContent.Trim());
            Assert.Equal("Mails the day's orders (click to rename it or change this)", cut.Find("#pipelines-editor-name").GetAttribute("title"));
        }

        // Tidy up lays the steps out by their flow, as one change Undo takes back; a connection leaving one of
        // several outcomes carries the outcome's name; each step carries its category's shade.
        [Fact]
        public void Tidy_up_lays_the_flow_out_and_a_named_outcome_labels_its_connection()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            void Add(string query)
            {
                cut.Find("#pipelines-editor-step-search").Input(query);
                cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            }

            // A Decision, a Log after it (on its first outcome, True), then a Delay dropped far away with nothing selected.
            Add("decision");
            Add("log");
            cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            Add("delay");
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            Assert.Contains("True", cut.FindAll(".diagram-link-label, [class*=link-label]").Select(label => label.TextContent.Trim()));
            Assert.All(cut.FindAll(".db-step-node"), node => Assert.Matches("db-step-node--shade-[0-4]", node.ClassName));

            cut.Find("#pipelines-editor-tidy").Click();
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var laid = service.Saved!.Steps.ToDictionary(step => step.Type, step => (step.Position.X, step.Position.Y));
            Assert.Equal((AutoLayout.Left, AutoLayout.Top), laid["Stomme.FlowDecision"]);
            Assert.Equal((AutoLayout.Left + StepPlacement.ColumnWidth, AutoLayout.Top), laid["Stomme.Log"]);
            Assert.Equal((AutoLayout.Left, AutoLayout.Top + StepPlacement.RowHeight), laid["Stomme.Delay"]);
        }

        // A sticky note added, written and saved; a comment on a step shown on it and saved; Delete removes
        // the note and Undo brings it back.
        [Fact]
        public void A_note_and_a_comment_are_written_shown_and_saved()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));

            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-step-comment")));
            cut.Find("#pipelines-editor-step-comment").Input("Says the import started");
            cut.WaitForAssertion(() => Assert.Equal("Says the import started", cut.Find(".db-step-node__comment").TextContent.Trim()));

            cut.Find("#pipelines-editor-add-note").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-note-text")));
            cut.Find("#pipelines-editor-note-text").Input("Ask finance before changing the delimiter");
            cut.WaitForAssertion(() => Assert.Equal("Ask finance before changing the delimiter", cut.Find(".db-note").TextContent.Trim()));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal("Says the import started", service.Saved!.Steps.Single().Comment);
            var note = Assert.Single(service.Saved.Notes);
            Assert.Equal(("Ask finance before changing the delimiter", (string?)null), (note.Text, note.Parent));

            // The note is selected: Delete removes it, Undo brings it back.
            cut.Find("#pipelines-editor-delete").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-note")));
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("Ask finance before changing the delimiter", cut.Find(".db-note").TextContent.Trim()));
        }

        // What happens when a step fails - set in the inspector, said on the step, saved with it.
        [Fact]
        public void When_it_fails_is_set_shown_on_the_step_and_saved()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("failure"); // where an error is saved, below
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("http");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-retries")));
            Assert.Empty(cut.FindAll("#pipelines-editor-retry-delay")); // no delay to set until it retries

            cut.Find("#pipelines-editor-retries").Input("3");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-retry-delay")));
            cut.Find("#pipelines-editor-retry-delay").Input("5");
            cut.Find("#pipelines-editor-on-failure").Change("go-on");
            cut.WaitForAssertion(() => Assert.Equal("↻ 3 × 5 s · goes on if it fails", cut.Find(".db-step-node__reliability").TextContent.Trim()));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!.Steps.Single();
            Assert.Equal((3, 5, true), (saved.Retries, saved.RetryDelaySeconds, saved.ContinueOnError));

            // n8n's error output: the step gains a Failed port, its error saved to a variable that says how to read it - saved with
            // it, going on no longer; back to stopping, the port goes.
            string[] Outcomes() => cut.FindAll(".db-step-node__outcome-label").Select(label => label.TextContent.Trim()).ToArray();
            Assert.Equal(new[] { "done" }, Outcomes());
            cut.Find("#pipelines-editor-on-failure").Change("failed-path");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "done", "Failed" }, Outcomes()));
            Assert.Equal("↻ 3 × 5 s", cut.Find(".db-step-node__reliability").TextContent.Trim());
            var failureId = cut.FindAll("#pipelines-editor-failure-variable option")[1].GetAttribute("value")!;
            cut.Find("#pipelines-editor-failure-variable").Change(failureId);
            cut.WaitForAssertion(() => Assert.Contains("{{ failure.error }}", cut.Markup));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.True(service.Saved!.Steps.Single().FailedPath));
            Assert.Equal((false, failureId), (service.Saved!.Steps.Single().ContinueOnError, service.Saved.Steps.Single().FailureVariable));

            cut.Find("#pipelines-editor-on-failure").Change("stop");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "done" }, Outcomes()));
            Assert.Empty(cut.FindAll("#pipelines-editor-failure-variable"));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.False(service.Saved!.Steps.Single().FailedPath));
            Assert.Null(service.Saved!.Steps.Single().FailureVariable);
        }

        // Export hands the browser what the canvas shows as a pipeline file; Import puts a file on the canvas as
        // a change Undo takes back, says which steps this site lacks, and refuses a file that is not a pipeline with the reason.
        [Fact]
        public void Export_downloads_the_canvas_and_import_puts_a_file_on_it_as_a_change_undo_takes_back()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            string[] Titles() => cut.FindAll(".db-step-node__title").Select(title => title.TextContent.Trim()).ToArray();
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Log" }, Titles()));

            cut.Find("#pipelines-editor-export").Click();
            var download = JSInterop.VerifyInvoke("stommePipelineEditorDownload");
            Assert.Equal("picker.pipeline.json", download.Arguments[0]);
            var exported = PipelineExchange.Read((string)download.Arguments[1]!);
            Assert.Equal("Picker", exported.Name);
            Assert.Equal("Stomme.Log", Assert.Single(exported.Graph!.Steps).Type);

            // A file with two steps and a connection: on the canvas in place of the Log, and Undo brings the Log back.
            var file = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "wait", Type = "Stomme.Delay", Name = "Wait", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 290, Y = 40 }, Inputs = { ["Text"] = "hi" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "wait", TargetId = "say" } },
            };
            var input = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>();
            input.UploadFiles(InputFileContent.CreateFromText(PipelineExchange.Write("Other", null, file), "other.pipeline.json"));
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Wait", "Say" }, Titles()));
            Assert.StartsWith("'other.pipeline.json' is on the canvas", cut.Find("#pipelines-editor-message").TextContent.Trim());
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(new[] { "wait", "say" }, service.Saved!.Steps.Select(step => step.Id));
            Assert.Equal(("wait", "say"), (service.Saved.Connections.Single().SourceId, service.Saved.Connections.Single().TargetId));

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Log" }, Titles()));

            // A step this site lacks is said; a file that is not a pipeline is refused, the canvas untouched.
            file.Steps.Add(new PipelineGraphStep { Id = "x", Type = "Acme.Thing" });
            input.UploadFiles(InputFileContent.CreateFromText(PipelineExchange.Write("Other", null, file), "acme.pipeline.json"));
            cut.WaitForAssertion(() => Assert.Contains("This site has no step Acme.Thing", cut.Find("#pipelines-editor-message").TextContent));
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Log" }, Titles()));
            input.UploadFiles(InputFileContent.CreateFromText("{\"format\":\"n8n\"}", "n8n.json"));
            cut.WaitForAssertion(() => Assert.Contains("'n8n.json' was not imported: The file is not a Nova Globen Stomme pipeline", cut.Find("#pipelines-editor-error").TextContent));
            Assert.Equal(new[] { "Log" }, Titles());
        }

        // The trigger panel - a webhook's address and a call listened for, used as a variable's value for
        // testing; a schedule's next runs, read again as its cron changes.
        [Fact]
        public void A_webhook_shows_its_address_and_a_call_listened_for_and_a_schedule_its_next_runs()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("payload");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("webhook");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-WebhookName")));

            cut.Find("#pipelines-editor-input-WebhookName").Input("incoming");
            cut.WaitForAssertion(() => Assert.Equal("https://localhost/api/Pipelines/webhooks/incoming", cut.Find("#pipelines-editor-webhook-url").TextContent.Trim()));
            Assert.Contains(("Stomme.Webhook", "incoming"), service.TriggerQueries);

            // Listened for: what the call sent; saved to a variable, it becomes that variable's value for testing.
            cut.Find("#pipelines-editor-webhook-listen").Click();
            cut.WaitForAssertion(() => Assert.Equal("{\"order\":7}", cut.Find("#pipelines-editor-webhook-call").TextContent.Trim()));
            Assert.StartsWith("Received 03:04:05 UTC · application/json", cut.Find("#pipelines-editor-webhook-call-head").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-webhook-use"));
            var payload = cut.FindAll("#pipelines-editor-output-Result option").Single(option => option.TextContent.Contains("payload")).GetAttribute("value");
            cut.Find("#pipelines-editor-output-Result").Change(payload);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-webhook-use")));
            cut.Find("#pipelines-editor-webhook-use").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'payload' holds what the call sent", cut.Find("#pipelines-editor-message").TextContent.Trim()));

            // A call that does not come is said.
            service.Call = new WebhookCallView { Received = false };
            cut.Find("#pipelines-editor-webhook-listen").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-webhook-call-none")));

            // A schedule: its next runs, read for the cron typed.
            cut.Find("#pipelines-editor-step-search").Input("schedule");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Cron")));
            cut.Find("#pipelines-editor-input-Cron").Input("0 * * * *");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Tue 2026-10-06 07:00 UTC", "Tue 2026-10-06 08:00 UTC" }, cut.FindAll("#pipelines-editor-schedule-runs li").Select(item => item.TextContent.Trim())));
            Assert.Contains(("Stomme.Schedule", "0 * * * *"), service.TriggerQueries);
            Assert.Null(service.TriggerZones[^1]);

            // In a time zone: read again for it, each run as the zone's clock reads it, the UTC time beside.
            cut.Find("#pipelines-editor-input-TimeZone").Input("Europe/Stockholm");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Tue 2026-10-06 07:00 Europe/Stockholm · 05:00 UTC" }, cut.FindAll("#pipelines-editor-schedule-runs li").Select(item => item.TextContent.Trim())));
            Assert.Equal("Europe/Stockholm", service.TriggerZones[^1]);
        }

        // A webhook's own secret from its panel - made and shown once with a copy, renewed and removed behind the house's
        // dialog, and the panel saying which secret its calls are signed under; an own secret that cannot be read is said.
        [Fact]
        public void A_webhook_gets_its_own_secret_shown_once_and_renews_and_removes_it_behind_a_question()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddLocalization(); // the new secret's and the removal's confirmations

            var page = Page();
            page.RenderMode = RenderModes.Interactive; // as the admin page renders - the dialogs ask in place
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("webhook");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-WebhookName")));
            cut.Find("#pipelines-editor-input-WebhookName").Input("Orders");
            cut.WaitForAssertion(() => Assert.Equal("the site's webhook secret", cut.Find("#pipelines-editor-webhook-signed-with").TextContent.Trim()));
            Assert.Empty(cut.FindAll(".pipelines-editor-webhook-secret-remove"));

            // Made: shown this once, with a copy; the panel now says its own.
            cut.Find("#pipelines-editor-webhook-secret-make").Click();
            cut.WaitForAssertion(() => Assert.Equal("own-secret-1", cut.Find("#pipelines-editor-webhook-secret-value").GetAttribute("value")));
            Assert.Equal(new[] { "own-secret-1" }, service.SecretsMade);
            Assert.Equal(new[] { "orders" }, service.OwnSecrets.ToArray()); // the name as the server keeps it
            cut.WaitForAssertion(() => Assert.Equal("its own secret", cut.Find("#pipelines-editor-webhook-signed-with").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-editor-webhook-secret-make"));
            cut.Find("#pipelines-editor-webhook-secret-copy").Click();
            Assert.Equal("own-secret-1", JSInterop.Invocations["stommePipelineEditorWriteClipboard"].Last().Arguments[0]);
            cut.WaitForAssertion(() => Assert.Equal("Copied", cut.Find("#pipelines-editor-webhook-secret-copy").TextContent.Trim()));
            cut.Find("#pipelines-editor-webhook-secret-hide").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-webhook-secret-value")));

            // A new one asks first; Cancel makes none.
            cut.Find(".pipelines-editor-webhook-secret-renew").Click();
            Assert.Contains("Calls signed with its present one are refused", cut.Find(".db-dialog-body").TextContent);
            cut.Find(".db-dialog-foot .db-btn-neutral").Click();
            Assert.Single(service.SecretsMade);
            cut.Find(".pipelines-editor-webhook-secret-renew").Click();
            cut.Find(".pipelines-editor-webhook-secret-renew-confirm").Click();
            cut.WaitForAssertion(() => Assert.Equal("own-secret-2", cut.Find("#pipelines-editor-webhook-secret-value").GetAttribute("value")));

            // Removed, behind its question: the site's webhook secret again, and the new secret no longer shown.
            cut.Find(".pipelines-editor-webhook-secret-remove").Click();
            Assert.Contains("verified under the site's webhook secret", cut.Find(".db-dialog-body").TextContent);
            cut.Find(".pipelines-editor-webhook-secret-remove-confirm").Click();
            cut.WaitForAssertion(() => Assert.Equal("the site's webhook secret", cut.Find("#pipelines-editor-webhook-signed-with").TextContent.Trim()));
            Assert.Equal(new[] { "orders" }, service.SecretsRemoved);
            Assert.Empty(cut.FindAll("#pipelines-editor-webhook-secret-value"));
            Assert.NotNull(cut.Find("#pipelines-editor-webhook-secret-make"));

            // An own secret that cannot be read: said, and every call refused - no listening - until a new one is made.
            service.OwnSecretUnreadable = true;
            cut.Find("#pipelines-editor-input-WebhookName").Input("unreadable");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-webhook-secret-unreadable")));
            Assert.Empty(cut.FindAll("#pipelines-editor-webhook-listen"));
            Assert.NotNull(cut.Find(".pipelines-editor-webhook-secret-renew"));
        }

        // The keyboard shortcuts in one list (n8n's "?") - from the toolbar or the key, closed by Close or Escape.
        [Fact]
        public async Task The_shortcuts_list_opens_from_the_toolbar_or_the_question_mark_and_closes_on_escape()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(new FakePipelinesService());
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            Assert.Empty(cut.FindAll("#pipelines-editor-shortcuts"));

            cut.Find("#pipelines-editor-shortcuts-toggle").Click();
            var keys = cut.FindAll("#pipelines-editor-shortcuts kbd").Select(key => key.TextContent.Trim()).ToArray();
            Assert.Contains("Ctrl+K or /", keys);
            Assert.Contains("D", keys);
            Assert.Contains("?", keys);
            Assert.Equal(EditorPage.Shortcuts.Length, keys.Length);
            cut.Find("#pipelines-editor-shortcuts-close").Click();
            Assert.Empty(cut.FindAll("#pipelines-editor-shortcuts"));

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("help"));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-shortcuts")));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-shortcuts")));
        }

        // n8n's arrow keys: Right along the connections (the step on the row first), Left back, Up and Down in a column.
        [Fact]
        public void The_arrows_go_along_the_connections_and_up_and_down_a_column()
        {
            var steps = new Dictionary<string, (double X, double Y)> { ["a"] = (30, 40), ["b"] = (290, 40), ["c"] = (290, 180), ["d"] = (550, 40), ["far"] = (900, 400) };
            var links = new (string From, string To)[] { ("a", "c"), ("a", "b"), ("b", "d") };
            Assert.Equal("b", StepNavigation.Next("right", "a", steps, links)); // the one on its row before the one below it
            Assert.Equal("a", StepNavigation.Next("left", "c", steps, links));
            Assert.Equal("c", StepNavigation.Next("down", "b", steps, links));
            Assert.Equal("b", StepNavigation.Next("up", "c", steps, links));
            Assert.Null(StepNavigation.Next("right", "d", steps, links));       // nothing follows it
            Assert.Null(StepNavigation.Next("up", "a", steps, links));          // nothing above it in its column
            Assert.Null(StepNavigation.Next("right", "gone", steps, links));
        }

        // n8n's canvas keys: the arrows move the selection (nothing selected, nothing moves), Enter opens the selected
        // step in the wide view, Ctrl+D duplicates (the clipboard left alone), Ctrl+X cuts to it, Shift+S adds a note, and +, -
        // and 0 zoom about the middle; every one of them in the shortcuts list.
        [Fact]
        public async Task The_canvas_keys_go_along_open_duplicate_cut_add_a_note_and_zoom()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Below", Position = new GraphPosition { X = 290, Y = 180 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" }, new PipelineGraphConnection { SourceId = "a", TargetId = "c" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            // The canvas of the moment: a paste lays the body out again on a new diagram.
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            string Selected() => string.Join(",", Diagram().GetSelectedModels().OfType<StepNodeModel>().Select(node => node.Name));
            Task Key(string command) => cut.InvokeAsync(() => cut.Instance.OnShortcut(command));

            // What is spoken: regions there before anything is said - a region made with its text in it is not announced
            // by every screen reader - and out of sight.
            string Said(string id) => cut.Find("#" + id).TextContent.Trim();
            Assert.Equal(("status", "polite", ""), (cut.Find("#pipelines-editor-say").GetAttribute("role"), cut.Find("#pipelines-editor-say").GetAttribute("aria-live"), Said("pipelines-editor-say")));
            Assert.Equal(("alert", ""), (cut.Find("#pipelines-editor-say-error").GetAttribute("role"), Said("pipelines-editor-say-error")));
            Assert.Equal(("status", ""), (cut.Find("#pipelines-editor-say-step").GetAttribute("role"), Said("pipelines-editor-say-step")));
            Assert.All(cut.FindAll("[id^='pipelines-editor-say']"), region => Assert.Contains("db-visually-hidden", region.ClassList));

            await Key("right");
            Assert.Equal("", Selected());
            await cut.InvokeAsync(() => Diagram().SelectModel(Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == "a"), true));
            await Key("right");
            Assert.Equal("Second", Selected());
            Assert.Equal("Second, Delay", Said("pipelines-editor-say-step")); // the step reached, spoken: its name and its type
            await Key("down");
            Assert.Equal("Below", Selected());
            Assert.Equal("Below, Log", Said("pipelines-editor-say-step"));
            await Key("left");
            Assert.Equal("First", Selected());
            await Key("up");
            Assert.Equal("First", Selected()); // nothing above it
            Assert.Equal("No step that way", Said("pipelines-editor-say-step"));
            var once = cut.Find("#pipelines-editor-say-step").TextContent;
            await Key("up");
            Assert.NotEqual(once, cut.Find("#pipelines-editor-say-step").TextContent); // said again, though the words are the same
            Assert.Equal("No step that way", Said("pipelines-editor-say-step"));

            await Key("open");
            cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("#pipelines-editor-wide").GetAttribute("aria-pressed")));
            Assert.NotEmpty(cut.FindAll("#pipelines-editor-wide-in"));
            cut.Find("#pipelines-editor-wide").Click();

            await Key("duplicate");
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            Assert.Equal("Duplicated 1 step.", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.Equal("Duplicated 1 step.", Said("pipelines-editor-say")); // shown, and spoken
            Assert.True(cut.Find("#pipelines-editor-paste").HasAttribute("disabled"), "a duplicate leaves the clipboard as it was");
            Assert.Equal("First", Selected()); // the copy, under the same name, is what is selected
            Assert.NotEqual("a", Diagram().GetSelectedModels().OfType<StepNodeModel>().Single().StepId);

            var cutText = await cut.InvokeAsync(() => cut.Instance.CutSelection());
            Assert.Single(PipelineExchange.Read(cutText).Graph.Steps);
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            Assert.False(cut.Find("#pipelines-editor-paste").HasAttribute("disabled"));

            await Key("note");
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-note")));

            var start = Diagram().Zoom;
            await Key("zoomIn");
            Assert.Equal(Math.Min(start * 1.2, 2), Diagram().Zoom, 3);
            await Key("zoomOut");
            Assert.Equal(start, Diagram().Zoom, 3);
            await Key("zoomIn");
            await Key("zoomReset");
            Assert.Equal(1, Diagram().Zoom, 3);

            var listed = EditorPage.Shortcuts.Select(shortcut => shortcut.Keys).ToList();
            Assert.Contains("Enter", listed);
            Assert.Contains("F2", listed);
            Assert.Contains("Arrow keys", listed);
            Assert.Contains("Ctrl+X", listed);
            Assert.Contains("Ctrl+D", listed);
            Assert.Contains("Shift+S", listed);
            Assert.Contains("1", listed);
            Assert.Contains("0, + and -", listed);
        }

        // A step as a screen reader says it: the note on the step with its signs in words, its problems, switched off.
        [Fact]
        public void A_step_is_spoken_with_its_run_its_problems_and_whether_it_is_off()
        {
            var node = new StepNodeModel("x", Catalog.Single(entry => entry.Type == "Stomme.Log"), "Stomme.Log", "Tell", new Blazor.Diagrams.Core.Geometry.Point(0, 0));
            Assert.Equal("Tell, Log", EditorPage.Spoken(node));

            node.ShowRun("handled", "failed → Failed path · ×2 · 1.2 s");
            node.ShowProblems(new[] { new PipelineGraphProblem { StepId = "x", Step = "Tell", Severity = PipelineGraphProblems.Warning, Message = "Nothing follows it." } }, Array.Empty<PipelineGraphProblem>());
            node.SetDisabled(true);
            Assert.Equal("Tell, Log, failed then Failed path, times 2, 1.2 s, 1 problem, switched off", EditorPage.Spoken(node));

            var named = new StepNodeModel("y", Catalog.Single(entry => entry.Type == "Stomme.Log"), "Stomme.Log", null, new Blazor.Diagrams.Core.Geometry.Point(0, 0));
            Assert.Equal("Log", EditorPage.Spoken(named)); // unnamed: its type, once
        }

        // n8n's buttons over a step: switch it off and back on, open it in the wide view, test it on its own (its result
        // in the inspector), delete it - each one change Undo takes back.
        [Fact]
        public void The_buttons_over_a_step_switch_it_open_it_test_it_and_delete_it()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node__actions").Count));
            AngleSharp.Dom.IElement Step(string name) => cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == name);
            AngleSharp.Dom.IElement Button(string name, string action) => Step(name).QuerySelector(".db-step-node__action--" + action)!;

            Button("Second", "toggle").Click();
            cut.WaitForAssertion(() => Assert.Contains("is-disabled", Step("Second").ClassName));
            Assert.Equal("true", Button("Second", "toggle").GetAttribute("aria-pressed"));
            Assert.StartsWith("Switched off: Second", cut.Find("#pipelines-editor-message").TextContent.Trim());
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.DoesNotContain("is-disabled", Step("Second").ClassName));

            Button("Second", "open").Click();
            cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("#pipelines-editor-wide").GetAttribute("aria-pressed")));
            Assert.Equal("Second", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));
            cut.Find("#pipelines-editor-wide").Click();

            Button("First", "test").Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-test-result")));
            Assert.Equal("a", service.Tested!.StepId);
            Assert.False(service.Tested.UpToHere);
            Assert.Equal("First", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));

            Button("Second", "delete").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "a>b" }, service.Saved!.Connections.Select(link => link.SourceId + ">" + link.TargetId)));
        }

        // n8n's "Convert to sub-workflow": the chosen steps moved from their menu (or Alt+X) into a new pipeline - what it
        // does said first - made, saved and published; on the canvas the call stands where they stood, one change Undo takes back.
        // Steps that cannot move say why, and nothing is made.
        [Fact]
        public async Task Steps_moved_into_a_sub_pipeline_are_run_where_they_stood()
        {
            var service = new FakePipelinesService
            {
                OpenedFolder = "Orders/Intake", // the sub-pipeline is made beside it
                Loaded = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", TypeName = "String", Value = "ada" }, new PipelineGraphVariable { Id = "v-loud", Name = "loud", TypeName = "Object" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Name = "Orders", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["WebhookName"] = "orders" } },
                        new PipelineGraphStep { Id = "shout", Type = "Stomme.SetVariable", Name = "Shout", Position = new GraphPosition { X = 290, Y = 40 }, InputVariables = { ["Variable"] = "v-loud" }, InputExpressions = { ["Value"] = "{{ who | upper }}" } },
                        new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 550, Y = 40 }, InputExpressions = { ["Text"] = "{{ loud }}" } },
                        new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Name = "After", Position = new GraphPosition { X = 810, Y = 40 }, InputExpressions = { ["Text"] = "{{ loud }}!" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "hook", TargetId = "shout" },
                        new PipelineGraphConnection { SourceId = "shout", TargetId = "say" },
                        new PipelineGraphConnection { SourceId = "say", TargetId = "after" },
                    },
                },
            };
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = PipelineSubPipelines.CallType, Version = 1, DisplayName = "Execute Pipeline", Category = "Composition",
                Inputs =
                {
                    new() { Name = "PipelineDefinitionId", DisplayName = "Pipeline Definition", TypeName = "String", UIHint = "pipeline-definition-picker" },
                    new() { Name = "Input", DisplayName = "Input", TypeName = "IDictionary`2" },
                    new() { Name = "WaitForCompletion", DisplayName = "Wait For Completion", TypeName = "Boolean" },
                    new() { Name = "GoOnWhenItFails", DisplayName = "Go on when it fails", TypeName = "Boolean" },
                },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "ExecutePipelineResult" } },
            });
            service.ExtraSteps.Add(new StepCatalogEntry { Type = PipelineSubPipelines.SetOutputType, Version = 1, DisplayName = "Set Output", Category = "Composition", Inputs = { new() { Name = "OutputName", DisplayName = "Output", TypeName = "String" }, new() { Name = "OutputValue", DisplayName = "Output Value", TypeName = "Object" } } });
            service.ExtraSteps.Add(new StepCatalogEntry { Type = PipelineCallContracts.WhenCalledType, Version = 1, DisplayName = "When called by another pipeline", Category = "Triggers", Kind = "Trigger", Inputs = { new() { Name = "Fields", DisplayName = "Fields", TypeName = "String" } } });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            StepNodeModel Node(string id) => Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == id);
            string[] Titles() => cut.FindAll(".db-step-node .db-step-node__title").Select(title => title.TextContent.Trim()).OrderBy(title => title, StringComparer.Ordinal).ToArray();

            // With the trigger among them: refused, saying why - and nothing is made.
            await cut.InvokeAsync(() =>
            {
                Diagram().SelectModel(Node("hook"), true);
                Diagram().SelectModel(Node("shout"), false);
            });
            cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == "Shout").ContextMenu(new MouseEventArgs { ClientX = 400, ClientY = 120 });
            cut.WaitForAssertion(() => Assert.Equal("Move 2 steps into a sub-pipeline…", cut.Find("#pipelines-editor-menu-sub-pipeline span").TextContent.Trim()));
            cut.Find("#pipelines-editor-menu-sub-pipeline").Click();
            cut.WaitForAssertion(() => Assert.Equal("These steps cannot move: 'Orders' starts the pipeline - a sub-pipeline is started by its caller.", cut.Find("#pipelines-editor-sub-refusal").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-editor-sub-move"));
            cut.Find("#pipelines-editor-sub-close").Click();
            Assert.Empty(service.Created);

            // Shout and Say, with Alt+X: what moving them does, said first - then moved.
            await cut.InvokeAsync(() =>
            {
                Diagram().UnselectAll(); // a step already selected stays as it is - the trigger with it
                Diagram().SelectModel(Node("shout"), true);
                Diagram().SelectModel(Node("say"), false);
            });
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("subPipeline"));
            cut.WaitForAssertion(() => Assert.Equal("Moves 2 steps into a new pipeline, which this one then runs where they stood and waits for - a failure there fails the run here. It sends 'who' and 'loud'. It gets back 'loud'.",
                cut.Find("#pipelines-editor-sub-what").TextContent.Trim()));
            Assert.Equal("Picker - Shout", cut.Find("#pipelines-editor-sub-name").GetAttribute("value"));
            cut.Find("#pipelines-editor-sub-name").Input("Make it loud");
            cut.Find("#pipelines-editor-sub-move").Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-sub-made")));

            // The new pipeline: made, saved with the steps between its start and what it gives back, and published.
            var made = Assert.Single(service.Created);
            Assert.Equal(("Make it loud", "Moved out of 'Picker'."), (made.Name, made.Description));
            var saved = Assert.Single(service.SavedTo);
            Assert.Equal("def-made-1", saved.DefinitionId);
            Assert.Equal(new[] { ("def-made-1", "Orders/Intake") }, service.Moved.ToArray());
            Assert.Equal(new[] { PipelineCallContracts.WhenCalledType, "Stomme.SetVariable", "Stomme.SetVariable", "Stomme.SetVariable", "Stomme.WriteLine", PipelineSubPipelines.SetOutputType },
                saved.Graph.Steps.Select(step => step.Type));
            Assert.Equal(new[] { "def-made-1" }, service.PublishedIds);

            // On the canvas: the call where they stood, selected; what they set given back after it; the link to the new pipeline.
            Assert.Equal(new[] { "After", "Orders", "Run 'Make it loud'", "loud from 'Make it loud'" }, Titles());
            Assert.Equal("Run 'Make it loud'", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));
            // n8n's "open sub-workflow": the pipeline the call runs, a click away.
            Assert.Equal("Open 'Make it loud'", cut.Find("#pipelines-editor-open-called-PipelineDefinitionId").TextContent.Trim());
            Assert.Contains("definition=def-made-1", cut.Find("#pipelines-editor-open-called-PipelineDefinitionId").GetAttribute("href"));
            var callNode = Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.Type == PipelineSubPipelines.CallType);
            Assert.Equal((290d, 40d), (callNode.Position.X, callNode.Position.Y));
            Assert.Equal("Open 'Make it loud'", cut.Find("#pipelines-editor-sub-open").TextContent.Trim());
            Assert.Contains("definition=def-made-1", cut.Find("#pipelines-editor-sub-open").GetAttribute("href"));
            Assert.StartsWith("Moved 2 steps into 'Make it loud', published", cut.Find("#pipelines-editor-message").TextContent.Trim());

            // One change: Undo puts the steps back.
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "After", "Orders", "Say", "Shout" }, Titles()));
        }

        // A variable renamed from the variables list - every expression and condition reading it follows, one change Undo
        // takes back; a name taken is refused, saying so.
        [Fact]
        public void A_variable_renamed_is_renamed_where_it_is_read()
        {
            var service = new FakePipelinesService
            {
                Loaded = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-who", Name = "who", Value = "ada" }, new PipelineGraphVariable { Id = "v-n", Name = "n" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 30, Y = 40 }, InputExpressions = { ["Text"] = "Hi {{ who | upper }}" } },
                        new PipelineGraphStep
                        {
                            Id = "big", Type = "Stomme.FlowDecision", Name = "Ada?", Position = new GraphPosition { X = 290, Y = 40 },
                            InputConditions = { ["Condition"] = new PipelineCondition { Rules = { new PipelineConditionRule { Left = "{{ who }}", Operator = PipelineConditions.EqualsOperator, Right = "ada" } } } },
                        },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "say", TargetId = "big" } },
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-variable-rename-v-who")));
            string Listed() => string.Join(", ", cut.FindAll("#pipelines-editor-variables li strong").Select(name => name.TextContent.Trim()));

            cut.Find("#pipelines-editor-variable-rename-v-who").Click();
            cut.WaitForAssertion(() => Assert.Equal("who", cut.Find("#pipelines-editor-variable-rename-name").GetAttribute("value")));
            cut.Find("#pipelines-editor-variable-rename-name").Input("N");
            cut.Find("#pipelines-editor-variable-rename-apply").Click();
            cut.WaitForAssertion(() => Assert.Equal("There is already a variable named 'N'.", cut.Find("#pipelines-editor-error").TextContent.Trim()));

            cut.Find("#pipelines-editor-variable-rename-name").Input("person");
            cut.Find("#pipelines-editor-variable-rename-name").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("Renamed 'who' to 'person' - and the 2 expressions and conditions reading it.", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            Assert.Equal("person, n", Listed());
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal("Hi {{ person | upper }}", service.Saved!.Steps.Single(step => step.Id == "say").InputExpressions["Text"]);
            Assert.Equal("{{ person }}", service.Saved.Steps.Single(step => step.Id == "big").InputConditions["Condition"].Rules[0].Left);
            Assert.Equal("person", service.Saved.Variables.Single(variable => variable.Id == "v-who").Name);

            // One change: Undo takes the name and what reads it back together.
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal("who, n", Listed()));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal("Hi {{ who | upper }}", service.Saved!.Steps.Single(step => step.Id == "say").InputExpressions["Text"]));
        }

        // n8n's right-click menu: on a step, what the keys and the buttons over it do; on the canvas, a step or a note added
        // where it was opened, Paste once something is copied, select all, tidy up, fit. Escape closes it.
        [Fact]
        public async Task The_right_click_menu_offers_on_a_step_and_on_the_canvas_what_the_keys_do()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            string[] Items() => cut.FindAll("#pipelines-editor-menu .db-pipeline-editor__menu-item span").Select(item => item.TextContent.Trim()).ToArray();
            AngleSharp.Dom.IElement Step(string name) => cut.FindAll(".db-step-node").Single(node => node.QuerySelector(".db-step-node__title")!.TextContent.Trim() == name);

            // On a step: it is selected, and the menu offers what the keys and its buttons do.
            Step("Second").ContextMenu(new MouseEventArgs { ClientX = 400, ClientY = 120 });
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-menu")));
            Assert.Equal(new[] { "Open", "Test this step", "Rename", "Connect to…", "Disconnect…", "Switch off", "Copy", "Duplicate", "Move into a sub-pipeline…", "Delete" }, Items());
            Assert.Equal("Second", cut.Find("#pipelines-editor-step-name").GetAttribute("value"));
            cut.Find("#pipelines-editor-menu-duplicate").Click();
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            Assert.Empty(cut.FindAll("#pipelines-editor-menu"));

            // On the canvas: nothing copied yet, so no Paste; a note where it was opened.
            var noteAt = Diagram().GetRelativeMousePoint(500, 400);
            cut.Find("#pipelines-editor-canvas").ContextMenu(new MouseEventArgs { ClientX = 500, ClientY = 400 });
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Add a step here", "Add a sticky note here", "Select all", "Tidy up", "Fit to the canvas" }, Items()));
            cut.Find("#pipelines-editor-menu-add-note").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-note")));
            var note = Diagram().Nodes.OfType<NoteNodeModel>().Single();
            Assert.Equal((noteAt.X, noteAt.Y), (note.Position.X, note.Position.Y));

            // "Add a step here": the step chosen next lands on that spot, connected to nothing.
            var stepAt = Diagram().GetRelativeMousePoint(700, 500);
            cut.Find("#pipelines-editor-canvas").ContextMenu(new MouseEventArgs { ClientX = 700, ClientY = 500 });
            // Waited for like the two menus above: under the gate's load the item was not rendered yet (it failed once in 6,393 runs).
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-menu-add-step")));
            cut.Find("#pipelines-editor-menu-add-step").Click();
            cut.Find("#pipelines-editor-step-search").Input("csv");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var added = service.Saved!.Steps.Single(step => step.Type == "Stomme.ParseCsv");
            Assert.Equal((stepAt.X, stepAt.Y), (added.Position.X, added.Position.Y));
            Assert.DoesNotContain(service.Saved.Connections, link => link.TargetId == added.Id || link.SourceId == added.Id);

            // Copied from a step's menu, Paste joins the canvas's; Escape closes a menu.
            Step("First").ContextMenu(new MouseEventArgs { ClientX = 100, ClientY = 120 });
            cut.Find("#pipelines-editor-menu-copy").Click();
            cut.Find("#pipelines-editor-canvas").ContextMenu(new MouseEventArgs { ClientX = 600, ClientY = 600 });
            cut.WaitForAssertion(() => Assert.Contains("Paste", Items()));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-menu")));

            // A key that acts closes it too: D (it switched the step off), and Ctrl+X (it took the step the menu was for).
            Step("First").ContextMenu(new MouseEventArgs { ClientX = 100, ClientY = 120 });
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-menu")));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("disable"));
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-menu")));
            Assert.Contains("is-disabled", Step("First").ClassName);
            Step("First").ContextMenu(new MouseEventArgs { ClientX = 100, ClientY = 120 });
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pipelines-editor-menu")));
            await cut.InvokeAsync(() => cut.Instance.CutSelection());
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-menu")));
            Assert.DoesNotContain(cut.FindAll(".db-step-node__title"), title => title.TextContent.Trim() == "First");
        }

        // n8n's "Test workflow": Test pipeline sends the pipeline as it stands, unsaved changes and all, as one whole test;
        // what each step did is on the canvas as a run's is, chosen in the run picker as the pipeline tested, with no run page to
        // link to, and how it went above the canvas with its log; a step's data is in the inspector.
        [Fact]
        public void Test_pipeline_runs_the_pipeline_as_it_stands_and_shows_each_step_on_the_canvas()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));

            // An unsaved change goes along: a third step added and not saved.
            cut.Find("#pipelines-editor-step-search").Input("csv");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            Assert.Null(service.Saved);

            cut.Find("#pipelines-editor-test-draft").Click();
            cut.WaitForAssertion(() => Assert.Equal("Tested the pipeline as it stands: finished - 3 steps ran. Nothing was stored.", cut.Find("#pipelines-editor-draft-test-summary").TextContent.Trim()));
            Assert.True(service.Tested!.Whole);
            Assert.Equal(3, service.Tested.Graph.Steps.Count);
            Assert.All(cut.FindAll(".db-step-node"), node => Assert.Contains("is-run-completed", node.ClassName));
            Assert.Equal(new[] { "the whole pipeline ran" }, cut.FindAll("#pipelines-editor-draft-test-log li").Select(line => line.TextContent.Trim()));
            Assert.Equal("the pipeline as it stands, tested", cut.Find("#pipelines-editor-run-select option:checked, #pipelines-editor-run-select option[value='draft-test']").TextContent.Trim());
            Assert.Empty(cut.FindAll("#pipelines-editor-run-page"));

            // A step's data, as for a run shown.
            var diagram = cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            cut.InvokeAsync(() => diagram.SelectModel(diagram.Nodes.OfType<StepNodeModel>().Single(node => node.StepId == "a"), true));
            cut.WaitForAssertion(() => Assert.Contains("a done", cut.Markup));

            // n8n's "3 items" on the connection First took to Second; no run shown, no count.
            Assert.Equal(new[] { "3 records" }, cut.FindAll(".db-connection-count").Select(count => count.TextContent.Trim()));
            cut.Find("#pipelines-editor-run-select").Change("");
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".db-connection-count")));
        }

        // n8n's "3 items" on a connection only where the run went: a Decision's branch not taken carries no count, the
        // step it goes to never having run.
        [Fact]
        public void A_branch_not_taken_carries_no_count()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.FlowDecision", Name = "Decide", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "yes", Type = "Stomme.Log", Name = "Yes", Position = new GraphPosition { X = 290, Y = 40 } },
                    new PipelineGraphStep { Id = "no", Type = "Stomme.Log", Name = "No", Position = new GraphPosition { X = 290, Y = 180 } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "a", SourcePort = "True", TargetId = "yes" },
                    new PipelineGraphConnection { SourceId = "a", SourcePort = "False", TargetId = "no" },
                },
            };
            service.WholeTestSkips.Add("no"); // the run went down True
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            cut.Find("#pipelines-editor-test-draft").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-connection-count")));
            var diagram = cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            var counted = diagram.Links.Single(link => link.Labels.OfType<RunCountLabelModel>().Any());
            Assert.Equal("yes", (((Blazor.Diagrams.Core.Anchors.SinglePortAnchor)counted.Target).Port.Parent as StepNodeModel)!.StepId);
            Assert.Equal("3 records", counted.Labels.OfType<RunCountLabelModel>().Single().Content);
        }

        // n8n's note colours: picked in a note's inspector, shown on the note, saved with it - one change Undo takes back.
        [Fact]
        public void A_notes_colour_is_picked_shown_saved_and_undone()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Notes = { new PipelineGraphNote { Id = "n1", Text = "Read me", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-note")));
            var diagram = cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            cut.InvokeAsync(() => diagram.SelectModel(diagram.Nodes.OfType<NoteNodeModel>().Single(), true));
            cut.WaitForAssertion(() => Assert.True(cut.Find("#pipelines-editor-note-color-accent").HasAttribute("checked")));
            Assert.Equal(new[] { "Accent", "Green", "Amber", "Red", "Grey" }, cut.FindAll("#pipelines-editor-note-colors label").Select(label => label.TextContent.Trim()));

            cut.Find("#pipelines-editor-note-color-red").Change(true);
            cut.WaitForAssertion(() => Assert.Contains("db-note--red", cut.Find(".db-note").ClassName));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(PipelineNoteColors.Red, service.Saved?.Notes.Single().Color));

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.DoesNotContain("db-note--", cut.Find(".db-note").ClassName));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Null(service.Saved!.Notes.Single().Color));
        }

        // The right-click menu from the keyboard (Shift+F10, the menu key): with nothing selected the canvas's, a note from
        // it landing in the canvas's middle; with a step selected, that step's - each with its first entry given the focus.
        [Fact]
        public async Task Shift_F10_opens_the_menu_of_the_canvas_or_of_the_selected_step()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("menu"));
            cut.WaitForAssertion(() => Assert.Equal("The canvas", cut.Find("#pipelines-editor-menu").GetAttribute("aria-label")));
            cut.WaitForAssertion(() => JSInterop.VerifyInvoke("stommePipelineEditorFocusMenu"));
            var middle = (X: (600 - Diagram().Pan.X) / Diagram().Zoom, Y: (400 - Diagram().Pan.Y) / Diagram().Zoom);
            cut.Find("#pipelines-editor-menu-add-note").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-note")));
            var note = Diagram().Nodes.OfType<NoteNodeModel>().Single();
            Assert.Equal((middle.X, middle.Y), (note.Position.X, note.Position.Y));

            await cut.InvokeAsync(() => Diagram().SelectModel(Diagram().Nodes.OfType<StepNodeModel>().Single(), true));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("menu"));
            cut.WaitForAssertion(() => Assert.Equal("Step First", cut.Find("#pipelines-editor-menu").GetAttribute("aria-label")));
            Assert.Contains("Shift+F10 or the menu key", EditorPage.Shortcuts.Select(shortcut => shortcut.Keys));
        }

        // A connection without the pointer: "Connect to…" in a step's menu (Shift+F10 opens it) lists the steps on the
        // canvas its outcome may go to, the nearest first - not itself, nor one it goes to already; the one chosen is connected
        // as a drawn connection is, said, and taken back by Undo. A step with several outcomes has an entry for each.
        [Fact]
        public async Task Connect_to_in_a_steps_menu_connects_it_without_the_pointer()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Below", Position = new GraphPosition { X = 290, Y = 180 } },
                    new PipelineGraphStep { Id = "d", Type = "Stomme.FlowDecision", Name = "Decide", Position = new GraphPosition { X = 550, Y = 40 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            StepNodeModel Node(string id) => Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == id);
            string[] Connections() => Diagram().Links
                .Select(link => ((link.Source as Blazor.Diagrams.Core.Anchors.SinglePortAnchor)?.Port?.Parent as StepNodeModel)?.StepId + ">"
                    + ((link.Target as Blazor.Diagrams.Core.Anchors.SinglePortAnchor)?.Port?.Parent as StepNodeModel)?.StepId)
                .OrderBy(connection => connection, StringComparer.Ordinal).ToArray();

            await cut.InvokeAsync(() => Diagram().SelectModel(Node("a"), true));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("menu"));
            cut.Find("#pipelines-editor-menu-connect").Click();
            cut.WaitForAssertion(() => Assert.Equal("Connect First to", cut.Find("#pipelines-editor-menu").GetAttribute("aria-label")));
            Assert.Equal(new[] { "Below", "Decide" }, cut.FindAll("#pipelines-editor-menu .db-pipeline-editor__menu-item").Select(item => item.TextContent.Trim()));

            cut.Find("#pipelines-editor-menu-to-c").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-menu")));
            Assert.Equal(new[] { "a>b", "a>c" }, Connections());
            Assert.Equal("Connected First to Below.", cut.Find("#pipelines-editor-message").TextContent.Trim());

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "a>b" }, Connections()));

            // Several outcomes, an entry each - and the outcome chosen is the one connected.
            await cut.InvokeAsync(() => Diagram().SelectModel(Node("d"), true));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("menu"));
            Assert.Equal(("Connect True to…", "Connect False to…"), (cut.Find("#pipelines-editor-menu-connect-0").TextContent.Trim(), cut.Find("#pipelines-editor-menu-connect-1").TextContent.Trim()));
            cut.Find("#pipelines-editor-menu-connect-1").Click();
            cut.WaitForAssertion(() => Assert.Equal("Connect Decide (False) to", cut.Find("#pipelines-editor-menu").GetAttribute("aria-label")));
            cut.Find("#pipelines-editor-menu-to-a").Click();
            cut.WaitForAssertion(() => Assert.Contains("d>a", Connections()));
            Assert.Equal("False", Assert.Single(Node("d").OutputPorts.Where(port => port.Links.Count > 0)).Outcome);
            Assert.Equal("Connected Decide (False) to First.", cut.Find("#pipelines-editor-message").TextContent.Trim());
        }

        // n8n's empty canvas: the first step a press away - the step chosen next centred in the canvas's middle - and the
        // prompt gone once there is a step.
        [Fact]
        public void An_empty_canvas_offers_the_first_step_in_its_middle()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-add-first")));
            var diagram = cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            cut.WaitForAssertion(() => Assert.True(diagram.Container is { Width: > 0 }));
            var middle = ((600 - diagram.Pan.X) / diagram.Zoom - CanvasFit.DefaultStepWidth / 2, (400 - diagram.Pan.Y) / diagram.Zoom - CanvasFit.DefaultStepHeight / 2);

            cut.Find("#pipelines-editor-add-first").Click();
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            var node = cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram.Nodes.OfType<StepNodeModel>().Single();
            Assert.Equal(middle, (node.Position.X, node.Position.Y));
            Assert.Empty(cut.FindAll("#pipelines-editor-empty"));
        }

        // n8n's dotted canvas: a dot every 20 of the diagram's pixels, spaced by the zoom (thinned out while that is under 8
        // pixels) and moved with the pan - the component following the diagram's pan and zoom itself.
        [Fact]
        public void The_canvas_grid_follows_the_pan_and_the_zoom()
        {
            Assert.Equal("background-size:20px 20px;background-position:0px 0px;", CanvasGrid.StyleFor(1, 0, 0));
            Assert.Equal("background-size:40px 40px;background-position:35px 35px;", CanvasGrid.StyleFor(2, 35, -5));
            Assert.Equal("background-size:12px 12px;background-position:5px 4px;", CanvasGrid.StyleFor(0.3, -7, 100)); // 6px, doubled

            var diagram = new Blazor.Diagrams.BlazorDiagram();
            var cut = Render<CanvasGrid>(parameters => parameters.Add(grid => grid.Diagram, diagram));
            Assert.Equal(CanvasGrid.StyleFor(1, 0, 0), cut.Find(".db-pipeline-editor__grid").GetAttribute("style"));
            cut.InvokeAsync(() => diagram.SetPan(30, 50));
            cut.WaitForAssertion(() => Assert.Equal(CanvasGrid.StyleFor(1, 30, 50), cut.Find(".db-pipeline-editor__grid").GetAttribute("style")));
            cut.InvokeAsync(() => diagram.SetZoom(1.5));
            cut.WaitForAssertion(() => Assert.Equal(CanvasGrid.StyleFor(1.5, 30, 50), cut.Find(".db-pipeline-editor__grid").GetAttribute("style")));
        }

        // And a connection taken away without the pointer: "Disconnect…" in a step's menu lists its connections, where
        // its outcomes go and what comes into it (the outcome named on a step with several); the one chosen goes, as with its own
        // ✕, said and brought back by Undo. A step with no connection has no such entry.
        [Fact]
        public async Task Disconnect_in_a_steps_menu_takes_a_connection_away_without_the_pointer()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Below", Position = new GraphPosition { X = 290, Y = 180 } },
                    new PipelineGraphStep { Id = "d", Type = "Stomme.FlowDecision", Name = "Decide", Position = new GraphPosition { X = 550, Y = 40 } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "a", TargetId = "b" },
                    new PipelineGraphConnection { SourceId = "d", SourcePort = "False", TargetId = "a" },
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".db-step-node").Count));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            StepNodeModel Node(string id) => Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == id);
            string[] Connections() => Diagram().Links
                .Select(link => ((link.Source as Blazor.Diagrams.Core.Anchors.SinglePortAnchor)?.Port?.Parent as StepNodeModel)?.StepId + ">"
                    + ((link.Target as Blazor.Diagrams.Core.Anchors.SinglePortAnchor)?.Port?.Parent as StepNodeModel)?.StepId)
                .OrderBy(connection => connection, StringComparer.Ordinal).ToArray();

            await cut.InvokeAsync(() => Diagram().SelectModel(Node("c"), true));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("menu"));
            cut.WaitForAssertion(() => Assert.Equal("Step Below", cut.Find("#pipelines-editor-menu").GetAttribute("aria-label")));
            Assert.Empty(cut.FindAll("#pipelines-editor-menu-disconnect")); // nothing to take away
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));

            await cut.InvokeAsync(() => Diagram().SelectModel(Node("a"), true));
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("menu"));
            cut.Find("#pipelines-editor-menu-disconnect").Click();
            cut.WaitForAssertion(() => Assert.Equal("Disconnect First", cut.Find("#pipelines-editor-menu").GetAttribute("aria-label")));
            Assert.Equal(new[] { "to Second", "from Decide (False)" }, cut.FindAll("#pipelines-editor-menu .db-pipeline-editor__menu-item").Select(item => item.TextContent.Trim()));

            cut.Find("#pipelines-editor-menu-disconnect-1").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "a>b" }, Connections()));
            Assert.Empty(cut.FindAll("#pipelines-editor-menu"));
            Assert.Equal("Removed the connection from Decide (False) to First.", cut.Find("#pipelines-editor-message").TextContent.Trim());

            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "a>b", "d>a" }, Connections()));
        }

        // n8n's Tidy up of the steps chosen (Shift+Alt+T, as the button and the canvas's menu): only they are laid out,
        // by the connections among them, kept where they were and still chosen after; the step not chosen stays put.
        [Fact]
        public async Task Tidy_up_with_steps_chosen_lays_out_only_them()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 600, Y = 500 } },
                    new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Third", Position = new GraphPosition { X = 300, Y = 700 } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            StepNodeModel Node(string id) => Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == id);
            (double X, double Y) At(string id) => (Node(id).Position.X, Node(id).Position.Y);

            await cut.InvokeAsync(() =>
            {
                Diagram().SelectModel(Node("b"), true);
                Diagram().SelectModel(Node("c"), false);
            });
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("tidy"));

            // Nothing chosen leads to either (a is not chosen): one column, in graph order, from the corner they had.
            cut.WaitForAssertion(() => Assert.Equal((300d, 500d), At("b")));
            Assert.Equal((300d, 500d + StepPlacement.RowHeight), At("c"));
            Assert.Equal((30d, 40d), At("a"));
            Assert.Equal(new[] { "b", "c" }, Diagram().GetSelectedModels().OfType<StepNodeModel>().Select(node => node.StepId).OrderBy(id => id, StringComparer.Ordinal));
            Assert.Equal("Tidied up the 2 steps chosen, by the connections among them.", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.Contains("Shift+Alt+T", EditorPage.Shortcuts.Select(shortcut => shortcut.Keys));

            // Nothing chosen: the whole canvas, as before - a leading to b.
            await cut.InvokeAsync(() => Diagram().UnselectAll());
            await cut.InvokeAsync(() => cut.Instance.OnShortcut("tidy"));
            cut.WaitForAssertion(() => Assert.Equal((AutoLayout.Left, AutoLayout.Top), At("a")));
            Assert.Equal((AutoLayout.Left + StepPlacement.ColumnWidth, AutoLayout.Top), At("b"));
        }

        // A layout without the pointer: Shift and an arrow move what is chosen - steps and notes - a dot of the grid that way,
        // a run of presses one change Undo takes back whole, and a screen reader told what moved; nothing chosen, nothing moves.
        [Fact]
        public async Task Shift_and_an_arrow_move_what_is_chosen_a_dot_of_the_grid()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph
            {
                Steps =
                {
                    new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } },
                    new PipelineGraphStep { Id = "b", Type = "Stomme.Delay", Name = "Second", Position = new GraphPosition { X = 290, Y = 40 } },
                },
                Notes = { new PipelineGraphNote { Id = "n", Text = "Read me", Position = new GraphPosition { X = 30, Y = 200 } } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".db-step-node").Count));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            (double X, double Y) At(string id) => Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == id) is { } node ? (node.Position.X, node.Position.Y) : default;
            (double X, double Y) Note() => Diagram().Nodes.OfType<NoteNodeModel>().Single() is { } note ? (note.Position.X, note.Position.Y) : default;
            Task Key(string command) => cut.InvokeAsync(() => cut.Instance.OnShortcut(command));

            await Key("nudgeright");
            Assert.Equal((30d, 40d), At("a")); // nothing chosen

            await cut.InvokeAsync(() => Diagram().SelectModel(Diagram().Nodes.OfType<StepNodeModel>().Single(node => node.StepId == "a"), true));
            await Key("nudgeright");
            await Key("nudgeright");
            await Key("nudgedown");
            Assert.Equal((70d, 60d), At("a"));
            Assert.Equal((290d, 40d), At("b"));
            Assert.Equal("Moved First down", cut.Find("#pipelines-editor-say-step").TextContent.Trim());

            await cut.InvokeAsync(() => Diagram().SelectModel(Diagram().Nodes.OfType<NoteNodeModel>().Single(), false));
            await Key("nudgeleft");
            Assert.Equal(((50d, 60d), (10d, 200d)), (At("a"), Note())); // the note chosen too moves with it
            Assert.Equal("Moved 2 chosen left", cut.Find("#pipelines-editor-say-step").TextContent.Trim());

            cut.Find("#pipelines-editor-undo").Click(); // the run of presses, whole
            cut.WaitForAssertion(() => Assert.Equal((30d, 40d), At("a")));
            Assert.Equal((30d, 200d), Note());
            Assert.Contains("Shift+arrow keys", EditorPage.Shortcuts.Select(shortcut => shortcut.Keys));
        }

        // n8n's zoom buttons: in and out about the middle, the zoom shown - following a zoom from elsewhere (the wheel) -
        // and pressed back to 100 %.
        [Fact]
        public async Task The_zoom_buttons_zoom_and_show_the_zoom_the_wheel_set_too()
        {
            var service = new FakePipelinesService();
            service.Loaded = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First", Position = new GraphPosition { X = 30, Y = 40 } } } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));
            Blazor.Diagrams.BlazorDiagram Diagram() => cut.FindComponent<Blazor.Diagrams.Components.DiagramCanvas>().Instance.BlazorDiagram;
            string Shown() => cut.Find("#pipelines-editor-zoom-reset").TextContent.Trim();

            await cut.InvokeAsync(() => Diagram().SetZoom(1));
            cut.WaitForAssertion(() => Assert.Equal("100 %", Shown()));
            cut.Find("#pipelines-editor-zoom-in").Click();
            cut.WaitForAssertion(() => Assert.Equal("120 %", Shown()));
            Assert.Equal(1.2, Diagram().Zoom, 3);
            cut.Find("#pipelines-editor-zoom-out").Click();
            cut.WaitForAssertion(() => Assert.Equal("100 %", Shown()));

            await cut.InvokeAsync(() => Diagram().SetZoom(0.5)); // the wheel's way
            cut.WaitForAssertion(() => Assert.Equal("50 %", Shown()));
            cut.Find("#pipelines-editor-zoom-reset").Click();
            cut.WaitForAssertion(() => Assert.Equal("100 %", Shown()));
            Assert.Equal(1, Diagram().Zoom, 3);
        }

        // A refused save says why, in the server's words, on either runtime: a refusal over HTTP (WebAssembly) or the handler's own
        // exception (Server), without the parameter name .NET appends; anything else keeps the general message.
        [Theory]
        [InlineData("refusal", "The pipeline was refused: 'Mars/Olympus_Mons' is not a time zone this server knows.")]
        [InlineData("argument", "The pipeline was refused: 'Mars/Olympus_Mons' is not a time zone this server knows.")]
        [InlineData("other", "The pipeline was refused. An input may hold a value its type cannot take, or a step is no longer offered.")]
        public void A_refused_save_says_why(string kind, string expected)
        {
            var service = new FakePipelinesService
            {
                SaveRefusal = kind switch
                {
                    "refusal" => new Stomme.Services.ServiceRefusalException("'Mars/Olympus_Mons' is not a time zone this server knows.", System.Net.HttpStatusCode.BadRequest),
                    "argument" => new ArgumentException("'Mars/Olympus_Mons' is not a time zone this server knows.", "graph"),
                    _ => new System.Net.Http.HttpRequestException("connection reset"),
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("schedule");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Cron")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find("#pipelines-editor-error").TextContent.Trim()));
        }

        // The "Pipeline failed" trigger - the runs that start it, read off the pipeline chosen (every pipeline when none, the
        // pipeline being edited not offered), and a sample failure shaped as a run's, as a variable's value for testing.
        [Fact]
        public void A_pipeline_failed_trigger_says_which_runs_start_it_and_offers_a_sample_failure()
        {
            var service = new FakePipelinesService();
            service.Pipelines.Add(new PipelineView { Id = "v-1", DefinitionId = "def-1", Name = "Picker", Version = 1, IsLatest = true }); // the one being edited
            service.Pipelines.Add(new PipelineView { Id = "v-2", DefinitionId = "def-2", Name = "Nightly report", Version = 1, IsLatest = true });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("failure");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("pipeline failed");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Pipeline")));

            Assert.Equal(new[] { "(every pipeline on the site)", "Nightly report" }, cut.FindAll("#pipelines-editor-input-Pipeline option").Select(option => option.TextContent.Trim()));
            Assert.StartsWith("When a run of any pipeline on this site fails", cut.Find("#pipelines-editor-failure-watch").TextContent.Trim());
            cut.Find("#pipelines-editor-input-Pipeline").Change("def-2");
            cut.WaitForAssertion(() => Assert.StartsWith("When a run of 'Nightly report' fails.", cut.Find("#pipelines-editor-failure-watch").TextContent.Trim()));

            // Saved to a variable, a sample failure of the pipeline watched becomes that variable's value for testing.
            Assert.Empty(cut.FindAll("#pipelines-editor-failure-use"));
            var failure = cut.FindAll("#pipelines-editor-output-Result option").Single(option => option.TextContent.Contains("failure")).GetAttribute("value");
            cut.Find("#pipelines-editor-output-Result").Change(failure);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-failure-use")));
            cut.Find("#pipelines-editor-failure-use").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'failure' holds a sample failure", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            using var sample = System.Text.Json.JsonDocument.Parse(cut.Find("#pipelines-editor-test-value-" + failure).GetAttribute("value")!);
            Assert.Equal("Nightly report", sample.RootElement.GetProperty("pipeline").GetString());
            Assert.False(string.IsNullOrEmpty(sample.RootElement.GetProperty("error").GetString()));

            // A later step's expression is previewed with the test values once there are some - not the empty starting value.
            cut.Find("#pipelines-editor-step-search").Input("write");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-source-Text")));
            cut.Find("#pipelines-editor-source-Text").Change("=expression");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-expression-Text")));
            cut.Find("#pipelines-editor-expression-Text").Input("{{ failure.pipeline }} failed: {{ failure.error }}");
            cut.WaitForAssertion(() => Assert.Equal("With the test values: Nightly report failed: The request failed: 503 Service Unavailable.",
                cut.Find("#pipelines-editor-preview-Text").TextContent.Trim()));
        }

        // The "File added" trigger (n8n's Local File Trigger) - which files start it, read off its inputs as the author sets
        // them, its folder opened on the Files page, and a sample file in that folder as a variable's value for testing.
        [Fact]
        public void A_file_added_trigger_says_which_files_start_it_and_offers_a_sample_file()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = PipelineFileEvent.StepType, Version = 1, DisplayName = "File added", Category = "Triggers", Kind = "Trigger",
                Description = "Start the pipeline when a file is put in a folder of the site's pipeline folder.",
                Inputs =
                {
                    new() { Name = "Folder", DisplayName = "Folder", TypeName = "String", UIHint = PipelineInputHints.PipelineFile },
                    new() { Name = "Pattern", DisplayName = "Files matching", TypeName = "String" },
                    new() { Name = "Subfolders", DisplayName = "Include subfolders", TypeName = "Boolean" },
                    new() { Name = "Changed", DisplayName = "Changed files too", TypeName = "Boolean" },
                },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("file");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("file added");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Folder")));

            Assert.StartsWith("When a file is put in the site's pipeline folder - one run a file. Once it is published: files already there then start nothing.",
                cut.Find("#pipelines-editor-file-watch").TextContent.Trim());
            cut.Find("#pipelines-editor-input-Folder").Input("inbox");
            cut.Find("#pipelines-editor-input-Pattern").Input("*.csv;*.CSV, *.xlsx");
            cut.Find("#pipelines-editor-input-Subfolders").Change(true);
            cut.Find("#pipelines-editor-input-Changed").Change(true);
            cut.WaitForAssertion(() => Assert.StartsWith("When a file named *.csv, *.xlsx is put in 'inbox' or a folder inside it, or one there is written again - one run a file.",
                cut.Find("#pipelines-editor-file-watch").TextContent.Trim()));
            Assert.EndsWith("folder=inbox", cut.Find("#pipelines-editor-file-folder").GetAttribute("href"));

            // Saved to a variable, a sample file in the folder watched, one the trigger would take, becomes its value for testing.
            Assert.Empty(cut.FindAll("#pipelines-editor-file-use"));
            var file = cut.FindAll("#pipelines-editor-output-Result option").Single(option => option.TextContent.Contains("file")).GetAttribute("value");
            cut.Find("#pipelines-editor-output-Result").Change(file);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-file-use")));
            cut.Find("#pipelines-editor-file-use").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'file' holds a sample file", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            using var sample = System.Text.Json.JsonDocument.Parse(cut.Find("#pipelines-editor-test-value-" + file).GetAttribute("value")!);
            Assert.Equal(("inbox/invoice-1001.csv", "add"), (sample.RootElement.GetProperty("path").GetString(), sample.RootElement.GetProperty("event").GetString()));

            // n8n's "Fetch test event": the newest file there its names take - none said so; then one, a .txt passed over.
            cut.Find("#pipelines-editor-file-newest").Click();
            cut.WaitForAssertion(() => Assert.Equal("There is no file in 'inbox' its names take - put one there on the Files page.", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            service.Files["inbox"] = null;
            service.Files["inbox/orders.csv"] = new byte[] { 1, 2, 3 };
            service.Files["inbox/zz-notes.txt"] = new byte[] { 1 }; // last by name: taken if its name were not held to the pattern
            cut.Find("#pipelines-editor-file-newest").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'file' holds inbox/orders.csv, the newest file in 'inbox'", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            using var newest = System.Text.Json.JsonDocument.Parse(cut.Find("#pipelines-editor-test-value-" + file).GetAttribute("value")!);
            Assert.Equal(("inbox/orders.csv", 3L), (newest.RootElement.GetProperty("path").GetString(), newest.RootElement.GetProperty("size").GetInt64()));
        }

        // The "Telegram message received" trigger (n8n's Telegram Trigger) - which messages start it, how it gets them, and a
        // sample message to test the next steps with.
        [Fact]
        public void A_telegram_message_received_trigger_says_which_messages_start_it_and_offers_a_sample_message()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = PipelineTelegramEvent.StepType, Version = 1, DisplayName = "Telegram message received", Category = "Triggers", Kind = "Trigger",
                Description = "Start the pipeline when one of the site's Telegram bots gets a message.",
                Inputs = { new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", UIHint = PipelineInputHints.ConnectionPicker, ConnectorTypes = new() { "telegram" } } },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("message");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("telegram message received");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-telegram-watch")));

            var watch = cut.Find("#pipelines-editor-telegram-watch").TextContent.Trim();
            Assert.StartsWith("When the bot of the connection chosen gets a message", watch);
            Assert.Contains("a bot with a webhook set (n8n's trigger sets one) gets none here until the webhook is deleted", watch);
            Assert.Contains("messages already waiting start nothing", watch);

            Assert.Empty(cut.FindAll("#pipelines-editor-telegram-use"));
            var variable = cut.FindAll("#pipelines-editor-output-Result option").Single(option => option.TextContent.Contains("message")).GetAttribute("value");
            cut.Find("#pipelines-editor-output-Result").Change(variable);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-telegram-use")));
            cut.Find("#pipelines-editor-telegram-use").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'message' holds a sample message", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            using var sample = System.Text.Json.JsonDocument.Parse(cut.Find("#pipelines-editor-test-value-" + variable).GetAttribute("value")!);
            Assert.Equal(("Where is order 1001?", 123456789L), (sample.RootElement.GetProperty("text").GetString(), sample.RootElement.GetProperty("chat").GetProperty("id").GetInt64()));
        }

        // The "Email received" trigger (n8n's Email Trigger (IMAP)) - which messages start it and what it does with them,
        // read off its inputs as the author sets them, and a sample message - its attachment's path when they are kept.
        [Fact]
        public void An_email_received_trigger_says_which_messages_start_it_and_offers_a_sample_message()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = PipelineEmailEvent.StepType, Version = 1, DisplayName = "Email received", Category = "Triggers", Kind = "Trigger",
                Description = "Start the pipeline when an e-mail arrives in a mailbox of one of the site's IMAP connections.",
                Inputs =
                {
                    new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", UIHint = PipelineInputHints.ConnectionPicker },
                    new() { Name = "Mailbox", DisplayName = "Mailbox", TypeName = "String" },
                    new() { Name = "MarkRead", DisplayName = "Mark as read", TypeName = "Boolean" },
                    new() { Name = "KeepAttachments", DisplayName = "Keep attachments", TypeName = "Boolean" },
                },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("email");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("email received");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Mailbox")));

            Assert.StartsWith("When a message arrives in 'INBOX' of the connection chosen - one run a message, followed by its UID, so one a person read first still starts its run. Once it is published",
                cut.Find("#pipelines-editor-email-watch").TextContent.Trim());
            cut.Find("#pipelines-editor-input-Mailbox").Input("Invoices");
            cut.Find("#pipelines-editor-input-MarkRead").Change(true);
            cut.Find("#pipelines-editor-input-KeepAttachments").Change(true);
            cut.WaitForAssertion(() => Assert.StartsWith("When a message arrives in 'Invoices' of the connection chosen - one run a message, followed by its UID, so one a person read first still starts its run; each is then marked read; its attachments are kept in the pipeline folder, under mail/.",
                cut.Find("#pipelines-editor-email-watch").TextContent.Trim()));

            Assert.Empty(cut.FindAll("#pipelines-editor-email-use"));
            var email = cut.FindAll("#pipelines-editor-output-Result option").Single(option => option.TextContent.Contains("email")).GetAttribute("value");
            cut.Find("#pipelines-editor-output-Result").Change(email);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-email-use")));
            cut.Find("#pipelines-editor-email-use").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'email' holds a sample message", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            using var sample = System.Text.Json.JsonDocument.Parse(cut.Find("#pipelines-editor-test-value-" + email).GetAttribute("value")!);
            Assert.Equal(("Invoices", "Invoice 1001"), (sample.RootElement.GetProperty("mailbox").GetString(), sample.RootElement.GetProperty("subject").GetString()));
            Assert.StartsWith("mail/", sample.RootElement.GetProperty("attachments")[0].GetProperty("path").GetString());

            // n8n's "Fetch test event": offered once a connection is chosen; the newest message read through it becomes the value, or
            // what is wrong is said.
            Assert.Empty(cut.FindAll("#pipelines-editor-email-fetch"));
            var connection = cut.Find("#pipelines-editor-input-Connection");
            if (connection.TagName == "SELECT")
            {
                connection.Change("6f9b7d1e-2c4a-4b8e-9f1d-3a5c7e9b1d2f");
            }
            else
            {
                connection.Input("6f9b7d1e-2c4a-4b8e-9f1d-3a5c7e9b1d2f");
            }

            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-email-fetch")));
            cut.Find("#pipelines-editor-email-fetch").Click();
            cut.WaitForAssertion(() => Assert.Equal("'INBOX' holds no message to test with.", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            service.NewestEmail = new PipelineTestEventView { Value = "{\"subject\":\"Real invoice\",\"uid\":42}" };
            cut.Find("#pipelines-editor-email-fetch").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'email' holds the newest message in 'Invoices'", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            Assert.Equal("{\"subject\":\"Real invoice\",\"uid\":42}", cut.Find("#pipelines-editor-test-value-" + email).GetAttribute("value"));
            Assert.Equal(("6f9b7d1e-2c4a-4b8e-9f1d-3a5c7e9b1d2f", "Invoices"), service.EmailFetches[^1]);
        }

        // n8n's Form trigger: its fields edited as a list (undone and redone; a dropdown with no choices a problem before
        // publish), the page's address in its panel (and said when the site serves no forms), a sample submission shaped by the fields
        // as a variable's value for testing, and the fields saved as the input's value.
        [Fact]
        public void A_form_trigger_edits_its_fields_says_its_address_and_offers_a_sample_submission()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            const string fields = "#pipelines-editor-form-fields-Fields";

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("message");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("form submitted");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-FormName")));

            cut.Find("#pipelines-editor-input-FormName").Input("contact");
            cut.WaitForAssertion(() => Assert.Equal("https://localhost/api/Pipelines/forms/contact", cut.Find("#pipelines-editor-form-url").TextContent.Trim()));
            Assert.Equal("https://localhost/api/Pipelines/forms/contact", cut.Find("#pipelines-editor-form-url").GetAttribute("href"));
            Assert.Empty(cut.FindAll("#pipelines-editor-form-off"));
            Assert.Contains(("Stomme.Form", "contact"), service.TriggerQueries);

            // No field yet: the problems panel says the form needs some; the first one added is the person's name.
            Assert.Empty(cut.FindAll(fields + " fieldset"));
            Assert.Contains("Fields needs a value.", cut.Find("#pipelines-editor-problems").TextContent);
            cut.Find(fields + "-add").Click();
            Assert.Equal("Name", cut.Find(fields + "-label-0").GetAttribute("value"));
            cut.Find(fields + "-add").Click();
            cut.Find(fields + "-label-1").Input("Topic");
            cut.Find(fields + "-type-1").Change(PipelineFormFieldTypes.Dropdown);
            cut.WaitForAssertion(() => Assert.Contains("Fields: 'Topic' is a dropdown with no choices.", cut.Find("#pipelines-editor-problems").TextContent));
            cut.Find(fields + "-options-1").Change("Question\n\n Other ");
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-problems")));
            Assert.Equal("Question\nOther", cut.Find(fields + "-options-1").GetAttribute("value"));
            Assert.Equal("{{ answers.name }}", cut.Find("#pipelines-editor-form-result code").TextContent.Trim());

            // Undo takes the fields back - changes in quick succession to one input are one step, as typing is - and redo returns them.
            cut.Find("#pipelines-editor-undo").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(fields + " fieldset")));
            cut.Find("#pipelines-editor-redo").Click();
            cut.WaitForAssertion(() => Assert.Equal("Question\nOther", cut.Find(fields + "-options-1").GetAttribute("value")));

            // Saved to a variable, a sample submission becomes that variable's value for testing.
            var message = cut.FindAll("#pipelines-editor-output-Result option").Single(option => option.TextContent.Contains("message")).GetAttribute("value");
            cut.Find("#pipelines-editor-output-Result").Change(message);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-form-use")));
            Assert.Equal("{{ message.name }}", cut.Find("#pipelines-editor-form-result code").TextContent.Trim());
            cut.Find("#pipelines-editor-form-use").Click();
            cut.WaitForAssertion(() => Assert.StartsWith("'message' holds a sample submission", cut.Find("#pipelines-editor-message").TextContent.Trim()));
            Assert.Equal("""{"name":"Ada Lovelace","topic":"Question"}""", cut.Find("#pipelines-editor-test-value-" + message).GetAttribute("value"));

            // A file field (n8n's File field) asks what kinds of file it takes - each with its dot, in lower case - not for a hint.
            cut.Find(fields + "-add").Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find(fields + "-label-2"))); // its render can come after the click returns, under load
            cut.Find(fields + "-label-2").Input("Invoice");
            cut.Find(fields + "-type-2").Change(PipelineFormFieldTypes.File);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find(fields + "-accept-2")));
            Assert.Empty(cut.FindAll(fields + "-placeholder-2"));
            cut.Find(fields + "-accept-2").Change(".pdf, PNG");
            cut.WaitForAssertion(() => Assert.Equal(".pdf, .png", cut.Find(fields + "-accept-2").GetAttribute("value")));

            // A hidden field (n8n's Hidden Field) is never required and has no hint: it asks the value it carries when the address gives none.
            cut.Find(fields + "-add").Click();
            cut.Find(fields + "-type-3").Change(PipelineFormFieldTypes.Hidden);
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find(fields + "-value-3")));
            Assert.Empty(cut.FindAll(fields + "-required-3"));
            Assert.Empty(cut.FindAll(fields + "-placeholder-3"));
            Assert.Equal("Value when the address gives none", cut.Find($"label[for={fields[1..]}-value-3]").TextContent.Trim());
            cut.Find(fields + "-name-3").Change("source");
            cut.Find(fields + "-value-3").Change("web");

            // What is saved is the fields as the editor wrote them.
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = PipelineForms.Parse(service.Saved!.Steps.Single(step => step.Type == PipelineForms.StepType).Inputs["Fields"], out _)!;
            Assert.Equal(new[] { ("Name", "text", ""), ("Topic", "dropdown", "Question|Other"), ("Invoice", "file", ""), ("", "hidden", "") }, saved.Select(field => (field.Label, field.Type, string.Join("|", field.Options))));
            Assert.Equal(new[] { ".pdf", ".png" }, saved[2].Accept);
            Assert.Equal(("source", "web"), (saved[3].Name, saved[3].Value));
        }

        // A file step's path chosen from the site's pipeline folder (the Files page's): Browse opens the folder, a folder is
        // gone into, a file chosen sets the path; for a path to write, "Use this folder" keeps the name it has, and Browse starts
        // where the path is; an input a variable feeds offers nothing to browse.
        [Fact]
        public void A_file_steps_path_is_chosen_from_the_sites_pipeline_folder()
        {
            var service = new FakePipelinesService();
            StepCatalogEntry FileStep(string type, string name) => new()
            {
                Type = type, Version = 1, DisplayName = name, Category = "Files", Description = name + " in the site's pipeline folder.", Kind = "Action",
                Inputs = { new() { Name = "Path", DisplayName = "Path", TypeName = "String", IsRequired = true, UIHint = PipelineInputHints.PipelineFile } },
                Outputs = { new() { Name = "Result", DisplayName = "Result", TypeName = "String" } },
            };
            service.ExtraSteps.Add(FileStep("Stomme.ReadTextFile", "Read text file"));
            service.ExtraSteps.Add(FileStep("Stomme.WriteTextFile", "Write text file"));
            service.Files["reports"] = null;
            service.Files["reports/q3.csv"] = new byte[] { 1, 2, 3 };
            service.Files["readme.txt"] = new byte[] { 1 };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("read text file");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Path")));

            cut.Find("#pipelines-editor-files-Path-browse").Click();
            string[] Listed() => cut.FindAll("#pipelines-editor-files-Path-list button").Select(button => button.TextContent.Trim()).ToArray();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "reports/", "readme.txt" }, Listed()));
            cut.Find("#pipelines-editor-files-Path-open-0").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "q3.csv" }, Listed()));
            cut.Find("#pipelines-editor-files-Path-choose-0").Click();
            cut.WaitForAssertion(() => Assert.Equal("reports/q3.csv", cut.Find("#pipelines-editor-input-Path").GetAttribute("value")));
            Assert.Empty(cut.FindAll("#pipelines-editor-files-Path")); // closed once chosen
            Assert.False(cut.Find("#pipelines-editor-undo").HasAttribute("disabled")); // a change like any other

            // A path to write: the name typed is kept when a folder is chosen; Browse then starts in that folder.
            cut.Find("#pipelines-editor-step-search").Input("write text file");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#pipelines-editor-input-Path").GetAttribute("value") ?? ""));
            cut.Find("#pipelines-editor-input-Path").Input("out.csv");
            cut.Find("#pipelines-editor-files-Path-browse").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "reports/", "readme.txt" }, Listed()));
            Assert.Equal("Set the path to out.csv", cut.Find("#pipelines-editor-files-Path-use-folder").GetAttribute("title"));
            cut.Find("#pipelines-editor-files-Path-open-0").Click();
            cut.WaitForAssertion(() => Assert.Equal("Set the path to reports/out.csv", cut.Find("#pipelines-editor-files-Path-use-folder").GetAttribute("title")));
            cut.Find("#pipelines-editor-files-Path-use-folder").Click();
            cut.WaitForAssertion(() => Assert.Equal("reports/out.csv", cut.Find("#pipelines-editor-input-Path").GetAttribute("value")));
            cut.Find("#pipelines-editor-files-Path-browse").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "q3.csv" }, Listed())); // where the path is

            // Closed, Browse is offered again; made an expression, the input holds no plain path to choose.
            cut.Find("#pipelines-editor-files-Path-close").Click();
            Assert.NotEmpty(cut.FindAll("#pipelines-editor-files-Path-browse"));
            cut.Find("#pipelines-editor-source-Path").Change("=expression");
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-files-Path-browse")));
        }

        // n8n's AI Agent: an agent's Tools offer the site's published tools - one click adds a name, a listed one is ticked -
        // and a tool trigger's panel says who calls it and what goes back.
        [Fact]
        public void An_ai_agent_offers_the_sites_tools_and_a_tool_trigger_says_who_calls_it()
        {
            var service = new FakePipelinesService();
            service.AgentTools.Add(new AgentToolView { Name = "find_order", Description = "Finds an order.", PipelineName = "Order lookup" });
            service.AgentTools.Add(new AgentToolView { Name = "weather", Description = "Says the weather." });
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("ai agent");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Tools")));

            string[] Chips() => cut.FindAll("#pipelines-editor-agent-tools button").Select(chip => chip.TextContent.Trim()).ToArray();
            Assert.Equal(new[] { "+ find_order", "+ weather" }, Chips());
            Assert.Equal("Finds an order. - runs 'Order lookup'", cut.FindAll("#pipelines-editor-agent-tools button")[0].GetAttribute("title"));
            cut.FindAll("#pipelines-editor-agent-tools button")[1].Click();
            cut.FindAll("#pipelines-editor-agent-tools button")[0].Click();
            Assert.Equal("weather\nfind_order", cut.Find("#pipelines-editor-input-Tools").GetAttribute("value"));
            Assert.Equal(new[] { "✓ find_order", "✓ weather" }, Chips());
            Assert.True(cut.FindAll("#pipelines-editor-agent-tools button")[0].HasAttribute("disabled"));

            // A tool trigger says it is called by agents, under its name.
            cut.Find("#pipelines-editor-step-search").Input("ai agent tool");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-ToolName")));
            cut.Find("#pipelines-editor-input-ToolName").Input("find_order");
            cut.WaitForAssertion(() => Assert.StartsWith("Once it is published, an AI agent step on this site may call it as 'find_order'", cut.Find("#pipelines-editor-agent-tool-note").TextContent.Trim()));
        }

        // A site that does not serve forms says so in the panel.
        [Fact]
        public void A_form_trigger_says_when_the_site_serves_no_forms()
        {
            var service = new FakePipelinesService { FormsEnabled = false };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("form submitted");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-FormName")));
            cut.Find("#pipelines-editor-input-FormName").Input("contact");
            cut.WaitForAssertion(() => Assert.Equal("The site does not serve forms: Pipelines:Forms:Enabled is off.", cut.Find("#pipelines-editor-form-off").TextContent.Trim()));
        }

        // A Decision's condition built from rules and previewed; a Switch's cases and a Fork's branches as the
        // step's outcome ports - a renamed case keeps its connection, a removed one takes it away - all saved as written.
        [Fact]
        public void A_condition_is_built_from_rules_and_a_switch_and_a_fork_name_their_outcomes()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            void Add(string query)
            {
                cut.Find("#pipelines-editor-step-search").Input(query);
                cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            }

            string[] Outcomes(int node) => cut.FindAll(".db-step-node")[node].QuerySelectorAll(".db-step-node__outcome-label").Select(label => label.TextContent.Trim()).ToArray();

            // A variable to test, starting at 250.
            cut.Find("#pipelines-editor-variable-name").Change("total");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("input[id^='pipelines-editor-variable-value-']").Change("250");

            // A Decision whose condition is "total is greater than 100": it holds with the starting value; at 300, it does not.
            Add("decision");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-source-Condition")));
            cut.Find("#pipelines-editor-source-Condition").Change("=condition");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-condition-Condition-left-0")));
            cut.Find("#pipelines-editor-condition-Condition-left-0").Input("{{ total }}");
            cut.Find("#pipelines-editor-condition-Condition-op-0").Change("greater");
            cut.Find("#pipelines-editor-condition-Condition-right-0").Input("100");
            cut.WaitForAssertion(() => Assert.Equal("With the starting values: it holds", cut.Find("#pipelines-editor-condition-Condition-preview").TextContent.Trim()));
            cut.Find("#pipelines-editor-condition-Condition-right-0").Input("300");
            cut.WaitForAssertion(() => Assert.Equal("With the starting values: it does not hold", cut.Find("#pipelines-editor-condition-Condition-preview").TextContent.Trim()));

            // A second rule, then taken away; a rule that tests nothing is a problem until it tests something.
            cut.Find("#pipelines-editor-condition-Condition-add").Click();
            cut.WaitForAssertion(() => Assert.Contains("tests nothing", cut.Find("#pipelines-editor-problems").TextContent));
            cut.Find("#pipelines-editor-condition-Condition-remove-1").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-condition-Condition-left-1")));

            // A Switch after it: two cases, each a port beside Default; the first renamed keeps its port, the second removed goes.
            cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));
            Add("switch");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-case-add")));
            Assert.Equal(new[] { "Default" }, Outcomes(1));
            cut.Find("#pipelines-editor-case-add").Click();
            cut.Find("#pipelines-editor-case-add").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Case 1", "Case 2", "Default" }, Outcomes(1)));

            // A Log added after it leaves by its first free outcome, Case 1. Back on the Switch (through its problem - its cases
            // test nothing yet), Case 1 renamed keeps that connection; Case 2 removed goes.
            Add("log");
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".db-step-node").Count));
            cut.FindAll(".db-pipeline-editor__problem").First(problem => problem.TextContent.Contains("Switch (flow)")).Click();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-case-label-0")));
            cut.Find("#pipelines-editor-case-label-0").Input("Gold");
            cut.Find("#pipelines-editor-case-0-left-0").Input("{{ total }}");
            cut.Find("#pipelines-editor-case-0-right-0").Input("gold");
            cut.Find("#pipelines-editor-case-remove-1").Click();
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Gold", "Default" }, Outcomes(1)));
            Assert.Contains("Gold", cut.FindAll(".diagram-link-label, [class*=link-label]").Select(label => label.TextContent.Trim()));
            cut.InvokeAsync(() => cut.Instance.OnShortcut("escape"));

            // A Fork with two branches, one per line.
            Add("fork");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Branches")));
            Assert.Equal(new[] { "done" }, Outcomes(3));
            cut.Find("#pipelines-editor-input-Branches").Input("Mail\nArchive");
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Mail", "Archive" }, Outcomes(3)));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!.Steps.ToDictionary(step => step.Type);
            Assert.Equal("{{ total }} is greater than 300", PipelineConditions.Describe(saved["Stomme.FlowDecision"].InputConditions["Condition"]));
            Assert.Empty(saved["Stomme.FlowDecision"].Inputs);
            Assert.Equal(new[] { "Gold" }, saved["Stomme.FlowSwitch"].Cases.Select(@case => @case.Label));
            Assert.Equal("{{ total }} is equal to gold", PipelineConditions.Describe(saved["Stomme.FlowSwitch"].Cases[0].Condition));
            Assert.Equal("[\"Mail\",\"Archive\"]", saved["Stomme.FlowFork"].Inputs["Branches"]);
            var switchId = saved["Stomme.FlowSwitch"].Id;
            var logId = saved["Stomme.Log"].Id;
            Assert.Equal(("Gold", logId), service.Saved.Connections.Where(connection => connection.SourceId == switchId).Select(connection => (connection.SourcePort, connection.TargetId)).Single());
        }

        // JavaScript is offered only where the site runs scripts; what it gives is previewed by the server's
        // sandbox with the values the other previews use, and the script is saved as written.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Javascript_is_offered_where_scripts_run_and_previewed_by_the_server(bool scripts)
        {
            var service = new FakePipelinesService { Capabilities = new PipelineCapabilitiesView { Scripts = scripts } };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-variable-name").Change("who");
            cut.Find("#pipelines-editor-variable-add").Click();
            // The new variable's row, once rendered - under the full suite's load it was not yet (seen under the fast gate).
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[id^='pipelines-editor-variable-value-']")));
            cut.Find("input[id^='pipelines-editor-variable-value-']").Change("world");
            cut.Find("#pipelines-editor-step-search").Input("write line");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-source-Text")));

            var options = cut.FindAll("#pipelines-editor-source-Text option").Select(option => option.TextContent.Trim()).ToList();
            Assert.Equal(scripts, options.Contains("JavaScript"));
            if (!scripts)
            {
                return;
            }

            cut.Find("#pipelines-editor-source-Text").Change("=script");
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-script-Text")));
            cut.Find("#pipelines-editor-script-Text").Input("'Hello ' + who");
            cut.WaitForAssertion(() => Assert.Equal("With the starting values: ran 'Hello ' + who with who=world", cut.Find("#pipelines-editor-script-preview-Text").TextContent.Trim()), TimeSpan.FromSeconds(5));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!.Steps.Single();
            Assert.Equal("'Hello ' + who", saved.InputScripts["Text"]);
            Assert.False(saved.Inputs.ContainsKey("Text"));
        }

        // D switches the selected steps off (greyed, said on the step, saved) and back on; the inspector's box does the same.
        [Fact]
        public async Task A_step_is_switched_off_and_on_from_the_keyboard_and_the_inspector()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("disable"));
            cut.WaitForAssertion(() => Assert.Contains("is-disabled", cut.Find(".db-step-node").ClassName));
            Assert.Equal("switched off", cut.Find(".db-step-node__disabled").TextContent.Trim());
            Assert.True(cut.Find("#pipelines-editor-step-disabled").HasAttribute("checked"));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.True(service.Saved!.Steps.Single().Disabled);

            await cut.InvokeAsync(() => cut.Instance.OnShortcut("disable"));
            cut.WaitForAssertion(() => Assert.DoesNotContain("is-disabled", cut.Find(".db-step-node").ClassName));
            cut.Find("#pipelines-editor-step-disabled").Change(true);
            cut.WaitForAssertion(() => Assert.Contains("is-disabled", cut.Find(".db-step-node").ClassName));
        }

        // Copy writes a pipeline file of the selection and the variables it uses (for the system clipboard); the browser's paste of
        // such a file - copied in another pipeline - adds its steps and the variables it brings; other text pastes the editor's copy.
        [Fact]
        public async Task Steps_travel_between_pipelines_through_the_system_clipboard_with_their_variables()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            string[] Titles() => cut.FindAll(".db-step-node__title").Select(title => title.TextContent.Trim()).ToArray();
            cut.Find("#pipelines-editor-variable-name").Change("who");
            cut.Find("#pipelines-editor-variable-add").Click();
            cut.Find("#pipelines-editor-step-search").Input("write line");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-source-Text")));
            cut.Find("#pipelines-editor-source-Text").Change("=expression");
            cut.Find("#pipelines-editor-expression-Text").Input("Hello {{ who }}");

            // Copy: the selected Write Line, as a pipeline file carrying 'who'.
            var copied = PipelineExchange.Read(await cut.InvokeAsync(() => cut.Instance.CopySelection()));
            Assert.Equal("Stomme.WriteLine", Assert.Single(copied.Graph!.Steps).Type);
            Assert.Equal("who", Assert.Single(copied.Graph.Variables).Name);

            // A paste of steps copied in ANOTHER pipeline: a Delay and a Write Line reading 'city', which this pipeline lacks.
            var elsewhere = new PipelineGraph
            {
                Variables = { new PipelineGraphVariable { Id = "v-city", Name = "city", TypeName = "String", Value = "Lund" } },
                Steps =
                {
                    new PipelineGraphStep { Id = "x1", Type = "Stomme.Delay", Name = "Wait", Position = new GraphPosition { X = 30, Y = 300 } },
                    new PipelineGraphStep { Id = "x2", Type = "Stomme.WriteLine", Name = "Say city", Position = new GraphPosition { X = 290, Y = 300 }, InputExpressions = { ["Text"] = "{{ city }}" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "x1", TargetId = "x2" } },
            };
            await cut.InvokeAsync(() => cut.Instance.OnPasteText(PipelineExchange.Write(null, null, elsewhere)));
            cut.WaitForAssertion(() => Assert.Equal(new[] { "Write Line", "Wait", "Say city" }, Titles()));
            Assert.Equal("Pasted, with the variable 'city' it uses.", cut.Find("#pipelines-editor-message").TextContent.Trim());

            // Text that is no pipeline file pastes the editor's own copy.
            await cut.InvokeAsync(() => cut.Instance.OnPasteText("just some text"));
            cut.WaitForAssertion(() => Assert.Equal(4, Titles().Length));

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(new[] { "who", "city" }, service.Saved!.Variables.Select(variable => variable.Name));
            Assert.Equal(2, service.Saved.Steps.Count(step => step.InputExpressions.GetValueOrDefault("Text") == "Hello {{ who }}"));
            Assert.Single(service.Saved.Connections);
        }

        // Nodes copied in n8n (its Ctrl+C puts the workflow's JSON on the clipboard) pasted here: brought over as steps,
        // connected as they were, with the note of what is left to finish beside them - and said.
        [Fact]
        public async Task Nodes_copied_in_n8n_paste_as_steps_with_what_is_left_to_finish()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-add-first")));
            const string copied = """
                {"nodes":[
                  {"parameters":{"amount":2,"unit":"seconds"},"name":"Pause","type":"n8n-nodes-base.wait","typeVersion":1.1,"position":[100,100]},
                  {"parameters":{"conditions":{}},"name":"Check","type":"n8n-nodes-base.if","typeVersion":2,"position":[320,100]}],
                 "connections":{"Pause":{"main":[[{"node":"Check","type":"main","index":0}]]}},"pinData":{}}
                """;
            await cut.InvokeAsync(() => cut.Instance.OnPasteText(copied));

            cut.WaitForAssertion(() => Assert.Equal(new[] { "Pause", "Check" }, cut.FindAll(".db-step-node__title").Select(title => title.TextContent.Trim())));
            Assert.Single(cut.FindAll(".diagram-link"));
            Assert.Equal("Pasted n8n's nodes as 2 steps - the note beside them says what is left to finish.", cut.Find("#pipelines-editor-message").TextContent.Trim());
            Assert.Contains("'Check': set its condition", cut.Find(".db-note").TextContent);
        }

        // A step that names one of the site's connections offers them by name and keeps the id; an editor that may not list them
        // (the Connectivity module refuses) types the id instead.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_connection_is_chosen_by_name_where_the_editor_may_list_them(bool listed)
        {
            var service = new FakePipelinesService();
            var crm = Guid.Parse("6f1c2d4e-0000-4000-8000-000000000001");
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddSingleton<Stomme.Modules.Connectivity.Services.IConnectionsService>(new FakeConnections(listed, crm));

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("connection");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Connection")));

            var field = cut.Find("#pipelines-editor-input-Connection");
            if (listed)
            {
                Assert.Equal("SELECT", field.TagName);
                Assert.Equal(new[] { "(choose a connection)", "Billing · Postgres", "CRM · REST API" }, field.QuerySelectorAll("option").Select(option => option.TextContent.Trim()));
                field.Change(crm.ToString());
            }
            else
            {
                Assert.Equal("INPUT", field.TagName);
                field.Input(crm.ToString());
            }

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(crm.ToString(), service.Saved!.Steps.Single().Inputs["Connection"]);
        }

        // n8n's node per service: a connection is offered in the palette by its name or its connector, with each step
        // that suits its connector - one that reads, the step that extracts; one that sends, the step that sends - and added with
        // the connection chosen; the step's operation is then chosen among that connector's own (send left to its own step), the
        // one chosen saying what it does and what it takes.
        [Fact]
        public void A_connection_is_offered_by_name_or_connector_and_its_operation_chosen_among_its_connectors_own()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = "Stomme.SendThroughConnection", Version = 1, DisplayName = "Send through connection", Category = "HTTP", Description = "Send an HTTP request through a connection.",
                Inputs = { new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", UIHint = PipelineInputHints.ConnectionPicker } },
            });
            var crm = Guid.Parse("6f1c2d4e-0000-4000-8000-000000000001");
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddSingleton<Stomme.Modules.Connectivity.Services.IConnectionsService>(new FakeConnections(true, crm, FakeConnections.Connectors()));

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            string[] Offers() => cut.FindAll("[id^='pipelines-editor-connection-offer-']").Select(offer => string.Join(" ", offer.TextContent.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))).ToArray();

            cut.Find("#pipelines-editor-step-search").Input("postgres");
            Assert.Equal(new[] { "Billing Postgres · Extract from connection" }, Offers()); // it reads; it does not send
            cut.Find("#pipelines-editor-step-search").Input("crm");
            Assert.Equal(new[] { "CRM REST API · Extract from connection", "CRM REST API · Send through connection" }, Offers());

            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Equal(crm.ToString(), cut.Find("#pipelines-editor-input-Connection").GetAttribute("value")));
            Assert.Empty(Offers()); // the query cleared for the next step

            var operation = cut.Find("#pipelines-editor-input-Operation");
            Assert.Equal("SELECT", operation.TagName);
            Assert.Equal(new[] { "(its first: List records)", "List records", "Search records" }, operation.QuerySelectorAll("option").Select(option => option.TextContent.Trim()));
            Assert.Contains("Reads a page of records.", cut.Find("#pipelines-editor-operation-help").TextContent); // the first, until one is chosen
            operation.Change("search");
            cut.WaitForAssertion(() => Assert.Contains("Finds records by a term.", cut.Find("#pipelines-editor-operation-help").TextContent));
            Assert.Equal(new[] { "term - Search term (required): What to look for.", "path - Path", "query - Filter: Its own syntax - kind:order." },
                cut.FindAll("#pipelines-editor-operation-parameters li").Select(item => string.Join(" ", item.TextContent.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))));
            Assert.Empty(cut.FindAll("#pipelines-editor-operation-query")); // a one-line search stays a parameter; the Query box is a statement's

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var saved = service.Saved!.Steps.Single();
            Assert.Equal(("Stomme.ExtractFromConnection", crm.ToString(), "search"), (saved.Type, saved.Inputs["Connection"], saved.Inputs["Operation"]));

            // A database's statement: its SQL query in the step's Query box, said so in its own words, and not listed again.
            var billing = cut.FindAll("#pipelines-editor-input-Connection option").Single(option => option.TextContent.Contains("Billing", StringComparison.Ordinal)).GetAttribute("value")!;
            cut.Find("#pipelines-editor-input-Connection").Change(billing);
            cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("#pipelines-editor-input-Operation option"), option => option.GetAttribute("value") == "query"));
            cut.Find("#pipelines-editor-input-Operation").Change("query");
            cut.WaitForAssertion(() => Assert.Equal("Its SQL query goes in Query: The statement - its values @name.", cut.Find("#pipelines-editor-operation-query").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-editor-operation-parameters li"));
        }

        // n8n's "Create new credential" from the node: a connection made under the step's picker (the Connections page's
        // own form), then listed and chosen for the step; one the server refuses says why and chooses nothing; and the connection
        // chosen is tested from there, the server's verdict said beside it.
        [Fact]
        public void A_connection_is_made_from_the_step_chosen_for_it_and_tested()
        {
            var service = new FakePipelinesService();
            var crm = Guid.Parse("6f1c2d4e-0000-4000-8000-000000000001");
            var connections = new FakeConnections(true, crm, FakeConnections.Connectors());
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddSingleton<Stomme.Modules.Connectivity.Services.IConnectionsService>(connections);

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("extract");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-new-connection-open")));
            Assert.Empty(cut.FindAll("#pipelines-editor-connection-test")); // nothing chosen yet to test

            // Refused: said in the server's words; nothing chosen.
            connections.Refusal = "You may not manage this site's connections.";
            cut.Find("#pipelines-editor-new-connection-open").Click();
            cut.Find("#pipelines-editor-new-connection-connector").Change("postgres");
            cut.Find("#pipelines-editor-new-connection-name").Change("Orders DB");
            cut.Find("#pipelines-editor-new-connection-save").Click();
            cut.WaitForAssertion(() => Assert.Equal("The connection was not saved: You may not manage this site's connections.", cut.Find("#pipelines-editor-new-connection-refusal").TextContent.Trim()));
            Assert.Equal("", cut.Find("#pipelines-editor-input-Connection").GetAttribute("value") ?? "");

            // Made: listed, chosen, the form closed.
            connections.Refusal = null;
            cut.Find("#pipelines-editor-new-connection-endpoint").Change("Host=orders.db.test;Database=orders");
            cut.Find("#pipelines-editor-new-connection-credential-password").Change("test-only-secret");
            cut.Find("#pipelines-editor-new-connection-save").Click();
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#pipelines-editor-new-connection")));
            var made = Assert.Single(connections.Made);
            Assert.Equal(("Orders DB", "postgres", "Host=orders.db.test;Database=orders"), (made.Request.Name, made.Request.ConnectorType, made.Request.Endpoint));
            Assert.Equal("test-only-secret", made.Request.Credentials!["password"]);
            var picker = cut.Find("#pipelines-editor-input-Connection");
            Assert.Equal(made.Id.ToString(), picker.GetAttribute("value"));
            Assert.Contains("Orders DB · Postgres", picker.QuerySelectorAll("option").Select(option => option.TextContent.Trim()));
            Assert.Equal("The connection 'Orders DB' was made and chosen - test it before the pipeline runs.", cut.Find("#pipelines-editor-message").TextContent.Trim());

            cut.Find("#pipelines-editor-connection-test").Click();
            cut.WaitForAssertion(() => Assert.Equal("Connected to PostgreSQL. (Healthy)", cut.Find("#pipelines-editor-connection-test-result").TextContent.Trim()));
            Assert.Equal(new[] { made.Id }, connections.Tested);

            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal(made.Id.ToString(), service.Saved!.Steps.Single().Inputs["Connection"]);
        }

        // A step that names the connections it takes (Send chat message: Slack, Telegram, Discord) is offered only those - in
        // its picker, in a connection made from it, in the step search - with nothing to offer saying which kinds it wants; one of
        // another kind chosen before stays named and says it is not one the step can use.
        [Fact]
        public void A_step_that_names_the_connections_it_takes_is_offered_only_those()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = "Stomme.SendChatMessage", Version = 1, DisplayName = "Send chat message", Category = "Notifications", Description = "Send a message to a chat.",
                Inputs =
                {
                    new() { Name = "Connection", DisplayName = "Connection", TypeName = "String", UIHint = PipelineInputHints.ConnectionPicker, ConnectorTypes = new() { "slack", "telegram", "discord" } },
                    new() { Name = "To", DisplayName = "To", TypeName = "String" },
                },
            });
            var crm = Guid.Parse("6f1c2d4e-0000-4000-8000-000000000001");
            var connections = new FakeConnections(true, crm, FakeConnections.Connectors());
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());
            Services.AddSingleton<Stomme.Modules.Connectivity.Services.IConnectionsService>(connections);

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("send chat message");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-editor-input-Connection")));

            // None of the site's connections (a REST API, a Postgres) is one it takes: nothing offered, the kinds it wants said.
            Assert.Equal(new[] { "(choose a connection)" }, cut.Find("#pipelines-editor-input-Connection").QuerySelectorAll("option").Select(option => option.TextContent.Trim()));
            Assert.Equal("This site has no Slack, Telegram or Discord connection yet - make one here, or under Admin, Connections.", cut.Find("#pipelines-editor-no-connection-Connection").TextContent.Trim());
            cut.Find("#pipelines-editor-new-connection-open").Click();
            Assert.Equal(new[] { "discord", "slack", "telegram" }, cut.Find("#pipelines-editor-new-connection-connector").QuerySelectorAll("option").Select(option => option.GetAttribute("value")).Where(value => !string.IsNullOrEmpty(value)));

            // A Slack connection on the site (read when the editor opens): the one offered - and in the step search, with this step
            // and not with Extract from connection, since a chat service reads nothing.
            connections.Extra.Add(new Stomme.Connectivity.ConnectionView(Guid.Parse("6f1c2d4e-0000-4000-8000-000000000009"), "Team chat", "slack", "Slack", "https://slack.com/api", new Dictionary<string, string>(), new[] { "token" }, 30, "Untested", null));
            var again = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            again.WaitForAssertion(() => Assert.False(again.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            again.Find("#pipelines-editor-step-search").Input("team");
            again.WaitForAssertion(() => Assert.Equal(new[] { "Team chat Slack · Send chat message" }, again.FindAll("[id^='pipelines-editor-connection-offer-']").Select(offer => string.Join(" ", offer.TextContent.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))))); // the connections load after the editor, under load later
            again.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            again.WaitForAssertion(() => Assert.Equal("6f1c2d4e-0000-4000-8000-000000000009", again.Find("#pipelines-editor-input-Connection").GetAttribute("value")));
            Assert.Equal(new[] { "(choose a connection)", "Team chat · Slack" }, again.Find("#pipelines-editor-input-Connection").QuerySelectorAll("option").Select(option => option.TextContent.Trim()));
            again.Find("#pipelines-editor-step-search").Input("crm"); // a REST API: never offered with Send chat message
            Assert.Equal(new[] { "CRM REST API · Extract from connection" }, again.FindAll("[id^='pipelines-editor-connection-offer-']").Select(offer => string.Join(" ", offer.TextContent.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))));
            again.Find("#pipelines-editor-step-search").Input("");

            // One of another kind chosen before stays named, saying so.
            again.Find("#pipelines-editor-input-Connection").Change(crm.ToString());
            again.WaitForAssertion(() => Assert.Contains("CRM · REST API (not one this step can use)", again.Find("#pipelines-editor-input-Connection").QuerySelectorAll("option").Select(option => option.TextContent.Trim())));
        }

        private sealed class FakeConnections(bool listed, Guid crm, List<Stomme.Connectivity.ConnectorView>? connectors = null) : Stomme.Modules.Connectivity.Services.IConnectionsService
        {
            /// <summary>More connections the site has, after the two it always has.</summary>
            public List<Stomme.Connectivity.ConnectionView> Extra { get; } = new();

            /// <summary>When set, making a connection is refused with these words.</summary>
            public string? Refusal { get; set; }

            public List<(Guid Id, Stomme.Connectivity.ConnectionRequest Request)> Made { get; } = new();

            public List<Guid> Tested { get; } = new();

            private static Stomme.Connectivity.ConnectorFieldView Field(string name, string display, bool required = false, string? description = null, string type = "Text") =>
                new(name, display, type, required, description, null, null, null, null, Array.Empty<Stomme.Connectivity.ConnectorFieldOptionView>(), null);

            private static Stomme.Connectivity.ConnectorView Connector(string type, string display, params Stomme.Connectivity.ConnectorOperationView[] operations) =>
                new(type, display, "None", "Url", false, Array.Empty<Stomme.Connectivity.ConnectorFieldView>(), Array.Empty<Stomme.Connectivity.ConnectorAuthFieldView>(),
                    Array.Empty<Stomme.Connectivity.ConnectorCategoryView>(), operations, null, null, "");

            private static readonly Stomme.Connectivity.ConnectorCategoryView[] Messaging = { new("messaging", "Messaging", 8) };

            private static readonly Stomme.Connectivity.ConnectorCategoryView[] DataAndFiles = { new("data-and-files", "Data & Files", 1) };

            // The REST API reads (list, search) and sends; Postgres reads only.
            public static List<Stomme.Connectivity.ConnectorView> Connectors() => new()
            {
                Connector("restapi", "REST API",
                    new("list", "List records", "Reads a page of records.", new[] { Field("path", "Path") }),
                    new("send", "Send a request", "Sends a request.", Array.Empty<Stomme.Connectivity.ConnectorFieldView>()),
                    new("search", "Search records", "Finds records by a term.", new[] { Field("term", "Search term", required: true, description: "What to look for."), Field("path", "Path"), Field("query", "Filter", description: "Its own syntax - kind:order.") })) with { Categories = DataAndFiles },
                Connector("postgres", "Postgres", new Stomme.Connectivity.ConnectorOperationView("query", "SQL query", "Runs a statement.", new[] { Field("query", "SQL query", required: true, description: "The statement - its values @name.", type: "Multiline") })) with
                {
                    AuthType = "Basic",
                    EndpointKind = "ConnectionString",
                    Categories = DataAndFiles,
                    AuthFields = new[] { new Stomme.Connectivity.ConnectorAuthFieldView("username", "Username", false), new Stomme.Connectivity.ConnectorAuthFieldView("password", "Password", true) },
                },
                // The chat services send messages and read nothing.
                Connector("slack", "Slack", new Stomme.Connectivity.ConnectorOperationView("message", "Send a message", "Sends a text message.", new[] { Field("to", "To", required: true), Field("text", "Text", required: true) })) with { Categories = Messaging },
                Connector("telegram", "Telegram", new Stomme.Connectivity.ConnectorOperationView("message", "Send a message", "Sends a text message.", new[] { Field("to", "To", required: true), Field("text", "Text", required: true) })) with { Categories = Messaging },
                Connector("discord", "Discord", new Stomme.Connectivity.ConnectorOperationView("message", "Send a message", "Sends a text message.", new[] { Field("to", "To", required: true), Field("text", "Text", required: true) })) with { Categories = Messaging },
            };

            private static Stomme.Connectivity.ConnectionView View(Guid id, string name, string connector) =>
                new(id, name, connector.Replace(" ", "").ToLowerInvariant(), connector, "https://example.com", new Dictionary<string, string>(), Array.Empty<string>(), 30, "Untested", null);

            public Task<List<Stomme.Connectivity.ConnectionView>> ListAsync() => listed
                ? Task.FromResult(new List<Stomme.Connectivity.ConnectionView> { View(crm, "CRM", "REST API"), View(Guid.NewGuid(), "Billing", "Postgres") }.Concat(Extra).ToList())
                : throw new InvalidOperationException("You may not list this site's connections.");

            public Task<Stomme.Connectivity.ConnectionView> GetAsync(Guid id) => throw new NotSupportedException();
            public Task<List<Stomme.Connectivity.ConnectorView>> ListConnectorsAsync() => connectors is null ? throw new NotSupportedException() : Task.FromResult(connectors);
            public Task<Stomme.Connectivity.ConnectionView> CreateAsync(Stomme.Connectivity.ConnectionRequest request)
            {
                if (Refusal is { } refusal)
                {
                    throw new InvalidOperationException(refusal);
                }

                var id = Guid.NewGuid();
                Made.Add((id, request));
                return Task.FromResult(View(id, request.Name, "Postgres") with { ConnectorType = request.ConnectorType });
            }
            public Task<Stomme.Connectivity.ConnectionView> UpdateAsync(Guid id, Stomme.Connectivity.ConnectionRequest request) => throw new NotSupportedException();
            public Task<Stomme.Connectivity.ConnectionTestView> TestAsync(Guid id)
            {
                Tested.Add(id);
                return Task.FromResult(new Stomme.Connectivity.ConnectionTestView(true, Stomme.Connectivity.ConnectorOutcome.Succeeded, "Connected to PostgreSQL.", "Healthy"));
            }
            public Task DeleteAsync(Guid id) => throw new NotSupportedException();
            public Task<Stomme.Connectivity.ConnectionEgressView> GetEgressAsync() => throw new NotSupportedException();
            public Task<Stomme.Connectivity.ConnectionEgressView> SaveEgressAsync(string allowedHosts) => throw new NotSupportedException();
        }

        // The pipelines list says where a discussion is still open - "2 open comments" under the pipeline's name, a way to
        // its editor - and nothing where none is.
        [Fact]
        public void The_pipelines_list_says_where_a_discussion_is_open()
        {
            var service = new FakePipelinesService();
            service.Pipelines.Add(new PipelineView { Id = "v-1", DefinitionId = "def-1", Name = "Nightly report", Version = 1, IsLatest = true, IsPublished = true });
            service.Pipelines.Add(new PipelineView { Id = "v-2", DefinitionId = "def-2", Name = "Quiet one", Version = 1, IsLatest = true });
            service.Comments.Add(new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "a", UserName = "bob", Text = "One" });
            service.Comments.Add(new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "a", UserName = "bob", Text = "Two" });
            service.Comments.Add(new PipelineStepCommentView { Id = Guid.NewGuid(), StepId = "a", UserName = "bob", Text = "Done", Resolved = true });
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddLocalization(); // the row's delete confirmation
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);

            var cut = Render<IndexPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-pipelines-discussion")));
            var link = cut.Find(".db-pipelines-discussion a");
            Assert.Equal("2 open comments", link.TextContent.Trim());
            Assert.Contains("definition=def-1", link.GetAttribute("href"));
            Assert.Contains("Nightly report", link.Closest("td")!.TextContent);
        }

        // Seen with fresh eyes: below a list of 32 pipelines, New pipeline was out of sight, and six buttons
        // alike to a row made Delete look like Edit. New pipeline opens the form at the top (open by itself on a site with none);
        // a row's other actions sit behind More.
        [Fact]
        public void New_pipeline_opens_at_the_top_and_a_rows_other_actions_sit_behind_more()
        {
            var service = new FakePipelinesService();
            service.Pipelines.Add(new PipelineView { Id = "v-1", DefinitionId = "def-1", Name = "Nightly report", Version = 1, IsLatest = true, IsPublished = true });
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddLocalization(); // the row's delete confirmation
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);

            var cut = Render<IndexPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-pipelines-more")));
            Assert.Empty(cut.FindAll("#pipelines-new"));
            Assert.Equal("false", cut.Find("#pipelines-new-open").GetAttribute("aria-expanded"));
            cut.Find("#pipelines-new-open").Click();
            Assert.NotNull(cut.Find("#pipelines-new-name"));
            Assert.True(cut.Markup.IndexOf("id=\"pipelines-new\"", StringComparison.Ordinal) < cut.Markup.IndexOf("Nightly report", StringComparison.Ordinal), "the form comes before the list");

            // A starter chosen names the pipeline after itself, until the author types a name of their own.
            cut.Find("#pipelines-new-starter").Change("hourly-check");
            Assert.Equal("Hourly HTTP check", cut.Find("#pipelines-new-name").GetAttribute("value"));
            Assert.False(cut.Find("#pipelines-create").HasAttribute("disabled"));
            cut.Find("#pipelines-new-starter").Change("greeting");
            Assert.Equal("Say hello", cut.Find("#pipelines-new-name").GetAttribute("value"));
            cut.Find("#pipelines-new-name").Change("Morning check");
            cut.Find("#pipelines-new-starter").Change("hourly-check");
            Assert.Equal("Morning check", cut.Find("#pipelines-new-name").GetAttribute("value"));
            cut.Find("#pipelines-new-starter").Change("");
            Assert.Equal("Morning check", cut.Find("#pipelines-new-name").GetAttribute("value"));

            var more = cut.Find(".db-pipelines-more");
            Assert.Equal("More", more.QuerySelector("summary")!.TextContent.Trim());
            // n8n's archive where Delete was: nothing is lost, so not in the danger colours - the archive's Delete is.
            Assert.DoesNotContain("db-btn-danger", more.QuerySelectorAll(".db-pipelines-more__actions .db-btn").Last().ClassName);
            Assert.Equal(new[] { "Duplicate", "Move to folder…", "Retract", "Archive" }, more.QuerySelectorAll(".db-pipelines-more__actions .db-btn").Select(button => button.TextContent.Trim()));
            Assert.Equal(new[] { "Edit", "Runs", "Run" }, more.ParentElement!.Children.Where(child => !child.ClassList.Contains("db-pipelines-more")).Select(child => child.TextContent.Trim()));
        }

        // A graph whose steps are piled on one spot (made through the API, a file or the assistant without positions) opens
        // laid out, said so, and a save keeps the layout.
        [Fact]
        public void A_graph_whose_steps_are_piled_on_one_spot_opens_laid_out()
        {
            var service = new FakePipelinesService
            {
                Loaded = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "a", Type = "Stomme.Log", Name = "First" },
                        new PipelineGraphStep { Id = "b", Type = "Stomme.Log", Name = "Second" },
                        new PipelineGraphStep { Id = "c", Type = "Stomme.Log", Name = "Third" },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "a", TargetId = "b" }, new PipelineGraphConnection { SourceId = "b", TargetId = "c" } },
                },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Contains("Its steps were piled on one spot, so they are laid out - Save keeps it.", cut.Markup));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            var spots = service.Saved!.Steps.Select(step => (step.Position.X, step.Position.Y)).ToList();
            Assert.Equal(3, spots.Distinct().Count());
            Assert.True(spots[0].X < spots[1].X && spots[1].X < spots[2].X, "left to right by its flow");
        }

        // A new pipeline from a starter or from a file - sent to the server whole, then opened in the editor.
        [Fact]
        public void A_new_pipeline_starts_from_a_starter_or_a_file_and_opens_in_the_editor()
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

            var cut = Render<IndexPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pipelines-empty")));

            cut.Find("#pipelines-new-starter").Change("hourly-check");
            Assert.StartsWith("Every hour, call an address", cut.Find("#pipelines-new-starter-description").TextContent.Trim());
            cut.Find("#pipelines-new-name").Change("Watch the shop");
            cut.Find("#pipelines-create").Click();

            cut.WaitForAssertion(() => Assert.Single(service.Imported));
            var fromStarter = service.Imported[0];
            Assert.Equal("Watch the shop", fromStarter.Name);
            Assert.Equal(PipelineStarters.Find("hourly-check")!.Graph().Steps.Select(step => step.Type), fromStarter.Graph.Steps.Select(step => step.Type));
            Assert.Contains("definition=def-new", navigation.Uri);
            Assert.EndsWith("Editor", new Uri(navigation.Uri).AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

            // A file: named as it says (nothing typed), its graph sent as read.
            var file = PipelineExchange.Write("From a file", "Exported elsewhere", PipelineStarters.Find("greeting")!.Graph());
            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText(file, "from-a-file.pipeline.json"));
            cut.WaitForAssertion(() => Assert.Equal(2, service.Imported.Count));
            Assert.Equal(("From a file", "Exported elsewhere"), (service.Imported[1].Name, service.Imported[1].Description));
            Assert.Equal("Hello, {{ who }}!", service.Imported[1].Graph.Steps.Single().InputExpressions["Message"]);

            // Not a pipeline: refused here with the reason, nothing sent.
            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("[1,2]", "list.json"));
            cut.WaitForAssertion(() => Assert.Contains("'list.json' was not imported", cut.Find("#pipelines-error").TextContent));
            Assert.Equal(2, service.Imported.Count);

            // n8n's export of many workflows - each a draft of its own, the list saying how many came over.
            var many = "[" + N8nWorkflowImportTests.Workflow + ",{\"name\":\"Second\",\"nodes\":[],\"connections\":{}}]";
            cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText(many, "workflows.json"));
            cut.WaitForAssertion(() => Assert.Equal(4, service.Imported.Count));
            Assert.Equal(new[] { "Order alerts", "Second" }, service.Imported.Skip(2).Select(request => request.Name));
            cut.WaitForAssertion(() => Assert.Equal("Imported 2 of 2 pipelines as drafts - each brought over from n8n has a note on its canvas of what is left to finish.", cut.Find("#pipelines-message").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#pipelines-error"));
        }

        // Fit on a canvas the browser has not drawn (0 x 0) took the whole editor down with the library's own ZoomToFit -
        // found driving it. It now does nothing there, and on a drawn canvas it centres the steps at no more than their size.
        [Theory]
        [InlineData(0, 0, "translate(0px, 0px) scale(1)")]
        [InlineData(1200, 800, "translate(460px, 310px) scale(1)")]
        public void Fit_works_on_a_drawn_canvas_and_does_no_harm_on_one_that_is_not(int width, int height, string transform)
        {
            var service = new FakePipelinesService();
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, width, height));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(Page()).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.False(cut.Find("#pipelines-editor-step-search").HasAttribute("disabled")));
            cut.Find("#pipelines-editor-step-search").Input("log");
            cut.Find("#pipelines-editor-step-search").KeyDown(new KeyboardEventArgs { Key = "Enter" });
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".db-step-node")));

            cut.Find("#pipelines-editor-fit").Click();

            // The editor is still there, and the canvas shows the step where Fit put it (an unmeasured step counts at its
            // default 220 x 100, placed at 30, 40 - so its middle, 140, 90, lands on the canvas's middle).
            Assert.Single(cut.FindAll(".db-step-node"));
            var layers = cut.FindAll("[style*='scale(']").Select(layer => layer.GetAttribute("style")).ToList();
            Assert.NotEmpty(layers);
            Assert.All(layers, style => Assert.Contains(transform, style));
        }

        // Found driving the Guardrails step: a tick no value was given for shows the step's own default, as the run reads it - a tick on
        // unless switched off (CSV's Has header) shows ticked; one switched off shows unticked and stays so, written as false.
        [Fact]
        public void A_tick_with_no_value_shows_the_steps_default_as_the_run_reads_it()
        {
            var service = new FakePipelinesService();
            service.ExtraSteps.Add(new StepCatalogEntry
            {
                Type = "Stomme.ReadCsvish", Version = 1, DisplayName = "Read CSV-ish", Category = "Data", Description = "Reads records.",
                Inputs =
                {
                    new() { Name = "HasHeader", DisplayName = "Has header", TypeName = "Boolean", UIHint = "checkbox", DefaultValue = "True" },
                    new() { Name = "Trim", DisplayName = "Trim", TypeName = "Boolean", UIHint = "checkbox" },
                    new() { Name = "Quote", DisplayName = "Quote", TypeName = "Boolean", UIHint = "checkbox", DefaultValue = "True" },
                },
            });
            service.Loaded = new PipelineGraph
            {
                Steps = { new PipelineGraphStep { Id = "read", Type = "Stomme.ReadCsvish", Name = "Read it", Position = new GraphPosition { X = 30, Y = 40 }, Inputs = { ["Quote"] = "false" } } },
            };
            JSInterop.Mode = JSRuntimeMode.Loose;
            JSInterop.Setup<Blazor.Diagrams.Core.Geometry.Rectangle>(_ => true).SetResult(new Blazor.Diagrams.Core.Geometry.Rectangle(0, 0, 1200, 800));
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton<ILogService>(new NoOpLogService());
            Services.AddSingleton<IPipelinesService>(service);
            Services.AddSingleton<ICollaborationService>(new UnusedCollaborationService());

            var page = Page();
            page.QueryString["step"] = "read";
            var cut = Render<EditorPage>(parameters => parameters.AddCascadingValue(page).AddCascadingValue(Module()));
            cut.WaitForAssertion(() => Assert.Equal("Read it", cut.Find("#pipelines-editor-step-name").GetAttribute("value")));

            bool Ticked(string name) => cut.Find("#pipelines-editor-input-" + name).HasAttribute("checked");
            Assert.True(Ticked("HasHeader"));  // no value: its default, on
            Assert.False(Ticked("Trim"));      // no value, no default: off
            Assert.False(Ticked("Quote"));     // switched off: off, its default notwithstanding

            cut.Find("#pipelines-editor-input-HasHeader").Change(false);
            cut.WaitForAssertion(() => Assert.False(Ticked("HasHeader")));
            cut.Find("#pipelines-editor-save").Click();
            cut.WaitForAssertion(() => Assert.NotNull(service.Saved));
            Assert.Equal("false", Assert.Single(service.Saved!.Steps).Inputs["HasHeader"]);
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page() => new()
        {
            Alias = Alias(),
            // The installation's collaboration switch is off: the editor's collaboration panel asks nothing.
            Site = new Site { SiteId = 1, Name = "Site", Settings = new Dictionary<string, string> { [HostSwitches.SettingName(HostSwitches.Collaboration)] = "false" } },
            Page = new Page { PageId = 1, SiteId = 1, Path = "admin/pipelines", Name = "Pipelines" },
            User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Admin + ";" },
            QueryString = new Dictionary<string, string> { ["definition"] = "def-1" },
            UrlParameters = "",
            Action = "",
        };

        /// <summary>The key rows as the module definition seeds them - every key to Administrators.</summary>
        private static ModuleModel Module() => new()
        {
            ModuleId = 32,
            SiteId = 1,
            PermissionList = PermissionKeys.ParseKeys(PermissionKeys.Declare(PipelinesPermissions.Declarations))
                .SelectMany(key => key.Roles.Select(role => new Permission(1, key.Resource, PermissionKeys.KeyEntityId, key.Operation, role, null, true)))
                .Concat(new[] { new Permission(PermissionNames.View, RoleNames.Registered, true) })
                .ToList(),
        };

        /// <summary>A clock whose timers fire only when the test says so - the discussion's read, the run poll.</summary>
        private sealed class DiscussionClock : TimeProvider
        {
            public List<DiscussionTimer> Timers { get; } = new();

            public override System.Threading.ITimer CreateTimer(System.Threading.TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = new DiscussionTimer(callback, state, dueTime);
                Timers.Add(timer);
                return timer;
            }
        }

        private sealed class DiscussionTimer(System.Threading.TimerCallback callback, object? state, TimeSpan dueTime) : System.Threading.ITimer
        {
            public TimeSpan DueTime { get; } = dueTime;

            public bool Armed { get; private set; } = dueTime != System.Threading.Timeout.InfiniteTimeSpan;

            public bool Disposed { get; private set; }

            public void Fire()
            {
                Assert.True(Armed && !Disposed, "the timer is not armed");
                Armed = false;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Armed = !Disposed && dueTime != System.Threading.Timeout.InfiniteTimeSpan;
                return !Disposed;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakePipelinesService : IPipelinesService
        {
            public PipelineGraph? Saved { get; private set; }

            private static PipelineView View() => new() { Id = "v-1", DefinitionId = "def-1", Name = "Picker", Version = 1, IsLatest = true };

            /// <summary>Steps this test adds to the shared catalog.</summary>
            public List<StepCatalogEntry> ExtraSteps { get; } = new();

            public Task<List<StepCatalogEntry>> GetStepsAsync() => Task.FromResult(Catalog.Concat(ExtraSteps).ToList());
            public Task<PipelineDetail> GetPipelineAsync(string definitionId) => Task.FromResult(new PipelineDetail { Id = "v-1", DefinitionId = definitionId, Name = "Picker", Version = 1, IsLatest = true, IsPublished = IsPublished, ArchivedOnUtc = ArchivedOn, ArchivedBy = ArchivedOn is null ? null : "ada", Folder = OpenedFolder });

            /// <summary>The folder the pipeline opened sits in.</summary>
            public string OpenedFolder { get; set; } = "";

            /// <summary>Whether the pipeline opened is published (its version 1).</summary>
            public bool IsPublished { get; set; }

            /// <summary>When the pipeline opened was archived; null when it is not.</summary>
            public DateTime? ArchivedOn { get; set; }
            public List<PipelineView> Pipelines { get; } = new();
            public Task<List<PipelineView>> GetPipelinesAsync(bool withLastRuns = false) => Task.FromResult(Pipelines.ToList());
            /// <summary>The graph the editor opens (a copy each time, as from the server).</summary>
            public PipelineGraph Loaded { get; set; } = new();

            /// <summary>Other pipelines' graphs, by definition id - a sub-pipeline the opened one calls.</summary>
            public Dictionary<string, PipelineGraph> Others { get; } = new();

            public Task<PipelineGraph> GetGraphAsync(string definitionId) =>
                Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<PipelineGraph>(System.Text.Json.JsonSerializer.Serialize(Others.TryGetValue(definitionId, out var other) ? other : Loaded))!);

            /// <summary>When set, the save throws it - the server refusing the graph.</summary>
            public Exception SaveRefusal { get; set; }

            public Task<PipelineView> SaveGraphAsync(string definitionId, PipelineGraph graph)
            {
                if (SaveRefusal is not null)
                {
                    throw SaveRefusal;
                }

                Saved = graph;
                SavedTo.Add((definitionId, graph));
                return Task.FromResult(View());
            }

            /// <summary>Each graph saved, with the pipeline it was saved to, in order.</summary>
            public List<(string DefinitionId, PipelineGraph Graph)> SavedTo { get; } = new();

            public Task<GraphSaveResult> SaveGraphMergedAsync(string definitionId, PipelineGraph graph, PipelineGraph loaded) => throw new NotSupportedException();

            /// <summary>The pipelines made, in order - each answered as "def-made-n".</summary>
            public List<CreatePipelineRequest> Created { get; } = new();

            public Task<PipelineView> CreatePipelineAsync(CreatePipelineRequest request)
            {
                Created.Add(request);
                return Task.FromResult(new PipelineView { Id = "v-made-" + Created.Count, DefinitionId = "def-made-" + Created.Count, Name = request.Name, Description = request.Description, Version = 1, IsLatest = true });
            }

            /// <summary>What was imported as a new pipeline, in order.</summary>
            public List<ImportPipelineRequest> Imported { get; } = new();

            public Task<PipelineView> ImportPipelineAsync(ImportPipelineRequest request)
            {
                Imported.Add(request);
                return Task.FromResult(new PipelineView { Id = "v-new", DefinitionId = "def-new", Name = request.Name, Version = 1, IsLatest = true });
            }

            /// <summary>What the trigger panel is told, and the call a listen hears.</summary>
            public List<(string Type, string Value)> TriggerQueries { get; } = new();

            /// <summary>Whether the site serves forms, as the trigger panel is told.</summary>
            public bool FormsEnabled { get; set; } = true;

            /// <summary>The time zone each schedule query named, in order (null: UTC).</summary>
            public List<string> TriggerZones { get; } = new();

            public Task<TriggerInfoView> GetTriggerInfoAsync(string type, string value, string zone = null)
            {
                TriggerQueries.Add((type, value));
                TriggerZones.Add(zone);
                if (type == PipelineForms.StepType)
                {
                    return Task.FromResult(new TriggerInfoView { Kind = "Form", Enabled = FormsEnabled, Path = "api/Pipelines/forms/" + value });
                }

                return Task.FromResult(type == "Stomme.Webhook"
                    ? new TriggerInfoView
                    {
                        Kind = "Webhook", Enabled = true, Signed = !OwnSecretUnreadable, SignatureHeader = "X-Signature-256", Path = "api/Pipelines/webhooks/" + value,
                        OwnSecret = OwnSecrets.Contains(value) && !OwnSecretUnreadable, OwnSecretUnreadable = OwnSecretUnreadable,
                    }
                    : zone is null
                        ? new TriggerInfoView
                        {
                            Kind = "Schedule", Enabled = true, CheckedEverySeconds = 60,
                            NextRunsUtc = { new DateTime(2026, 10, 6, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc) },
                        }
                        : new TriggerInfoView
                        {
                            Kind = "Schedule", Enabled = true, CheckedEverySeconds = 60, TimeZone = zone,
                            NextRunsUtc = { new DateTime(2026, 10, 6, 5, 0, 0, DateTimeKind.Utc) },
                            NextRunsInZone = { new DateTime(2026, 10, 6, 7, 0, 0) },
                        });
            }

            public WebhookCallView Call { get; set; } = new() { Received = true, ReceivedAtUtc = new DateTime(2026, 10, 6, 3, 4, 5, DateTimeKind.Utc), ContentType = "application/json", Body = "{\"order\":7}" };

            public Task<WebhookCallView> ListenForWebhookAsync(string name, int seconds) => Task.FromResult(Call);
            public PipelineTestEventView NewestEmail { get; set; } = new() { Problem = "'INBOX' holds no message to test with." };
            public List<(string Connection, string Mailbox)> EmailFetches { get; } = new();
            public Task<PipelineTestEventView> FetchNewestEmailAsync(string connection, string mailbox)
            {
                EmailFetches.Add((connection, mailbox));
                return Task.FromResult(NewestEmail);
            }

            /// <summary>The webhook names with a secret of their own; each new secret made, in order; each removal asked for.</summary>
            public HashSet<string> OwnSecrets { get; } = new(StringComparer.OrdinalIgnoreCase); // the receiver is case-blind

            public bool OwnSecretUnreadable { get; set; }

            public List<string> SecretsMade { get; } = new();

            public List<string> SecretsRemoved { get; } = new();

            public Task<PipelineWebhookSecretView> RenewWebhookSecretAsync(string name)
            {
                var secret = "own-secret-" + (SecretsMade.Count + 1);
                SecretsMade.Add(secret);
                OwnSecrets.Add(name);
                OwnSecretUnreadable = false;
                return Task.FromResult(new PipelineWebhookSecretView { Name = name, Secret = secret });
            }

            public Task RemoveWebhookSecretAsync(string name)
            {
                SecretsRemoved.Add(name);
                OwnSecrets.Remove(name);
                OwnSecretUnreadable = false;
                return Task.CompletedTask;
            }

            /// <summary>What the site allows, and each preview asked for (answered with the script and its values).</summary>
            public PipelineCapabilitiesView Capabilities { get; set; } = new();

            public List<ScriptPreviewRequest> Previews { get; } = new();

            public Task<PipelineCapabilitiesView> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

            public Task<ScriptPreviewView> PreviewScriptAsync(ScriptPreviewRequest request)
            {
                Previews.Add(request);
                return Task.FromResult(new ScriptPreviewView { Value = "ran " + request.Script + " with who=" + request.Values.GetValueOrDefault("who") });
            }

            /// <summary>What the editor asked the AI, and how the server answers: the canvas it was sent, changed - or a refusal.</summary>
            public List<PipelineGraphDesignRequest> Designs { get; } = new();

            public Func<PipelineGraph, PipelineGraph>? Design { get; set; }

            /// <summary>When set, the AI answers without changing anything: no graph comes back, only this.</summary>
            public string? Answer { get; set; }

            public Exception? DesignRefusal { get; set; }

            /// <summary>When set, the answer waits for it - the model at work, while the person goes on editing.</summary>
            public TaskCompletionSource<bool>? Hold { get; set; }

            public async Task<PipelineGraphDesignView> DesignGraphAsync(PipelineGraphDesignRequest request)
            {
                Designs.Add(request);
                if (Hold is not null)
                {
                    await Hold.Task;
                }

                if (DesignRefusal is not null)
                {
                    throw DesignRefusal;
                }

                if (Answer is not null)
                {
                    return new PipelineGraphDesignView { Graph = null, Summary = Answer };
                }

                var copy = System.Text.Json.JsonSerializer.Deserialize<PipelineGraph>(System.Text.Json.JsonSerializer.Serialize(request.Graph))!;
                return new PipelineGraphDesignView { Graph = Design?.Invoke(copy) ?? copy, Summary = "Added what you asked for." };
            }

            /// <summary>The scripts asked of the AI, and what it answers - or the refusal it throws.</summary>
            public List<PipelineScriptRequest> ScriptsAsked { get; } = new();

            public PipelineScriptView Written { get; set; } = new() { Script = "JSON.stringify(JSON.parse(input))" };

            public Exception? ScriptRefusal { get; set; }

            public Task<PipelineScriptView> WriteScriptAsync(PipelineScriptRequest request)
            {
                ScriptsAsked.Add(request);
                return ScriptRefusal is not null ? Task.FromException<PipelineScriptView>(ScriptRefusal) : Task.FromResult(Written);
            }

            /// <summary>The scripts checked, each request as asked; and the problem the "parser" finds in each (none, unless set).</summary>
            public List<PipelineScriptCheckRequest> ScriptChecks { get; } = new();

            public Func<PipelineScriptCheck, string?> ScriptProblem { get; set; } = _ => null;

            public Task<PipelineScriptCheckView> CheckScriptsAsync(PipelineScriptCheckRequest request)
            {
                ScriptChecks.Add(request);
                return Task.FromResult(new PipelineScriptCheckView { Problems = request.Scripts.Select(check => ScriptProblem(check)!).ToList() });
            }

            /// <summary>The pipeline's comments as the server keeps them; each write changes them as the server would.</summary>
            public List<PipelineStepCommentView> Comments { get; } = new();

            /// <summary>The comment writes, in order: "add step text", "resolve id true", "delete id".</summary>
            public List<string> CommentWrites { get; } = new();

            /// <summary>When set, a comment write throws it - the server refusing.</summary>
            public Exception? CommentRefusal { get; set; }

            private int _commentClock;

            public Task<List<PipelineStepCommentView>> GetStepCommentsAsync(string definitionId) => Task.FromResult(Comments.Select(Copy).ToList());

            /// <summary>The people a comment may mention, as the server lists them.</summary>
            public List<PipelineMentionView> People { get; } = new();

            public Task<List<PipelineMentionView>> GetMentionablePeopleAsync() => Task.FromResult(People.ToList());

            public Task<Dictionary<string, int>> GetOpenCommentCountsAsync() =>
                Task.FromResult(Comments.Where(comment => !comment.Resolved).GroupBy(comment => "def-1").ToDictionary(group => group.Key, group => group.Count()));

            public Task<PipelineStepCommentView> AddStepCommentAsync(string definitionId, PipelineStepCommentRequest request)
            {
                CommentWrites.Add("add " + request.StepId + " " + request.Text);
                if (CommentRefusal is not null)
                {
                    return Task.FromException<PipelineStepCommentView>(CommentRefusal);
                }

                var comment = new PipelineStepCommentView
                {
                    Id = Guid.NewGuid(), StepId = request.StepId, Text = request.Text.Trim(), UserName = "ada", Mine = true, CanDelete = true,
                    CreatedOnUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc).AddMinutes(++_commentClock),
                };
                Comments.Add(comment);
                var answer = Copy(comment);
                foreach (System.Text.RegularExpressions.Match named in System.Text.RegularExpressions.Regex.Matches(request.Text, @"@([\w.\-]+)"))
                {
                    var name = named.Groups[1].Value.TrimEnd('.');
                    if (People.FirstOrDefault(person => string.Equals(person.UserName, name, StringComparison.OrdinalIgnoreCase)) is { } person)
                    {
                        answer.Told.Add(person.DisplayName);
                    }
                    else
                    {
                        answer.NotTold.Add(name);
                    }
                }

                return Task.FromResult(answer);
            }

            public Task<PipelineStepCommentView> SetStepCommentResolvedAsync(string definitionId, Guid id, bool resolved)
            {
                CommentWrites.Add("resolve " + id + " " + resolved.ToString().ToLowerInvariant());
                if (CommentRefusal is not null)
                {
                    return Task.FromException<PipelineStepCommentView>(CommentRefusal);
                }

                if (Comments.FirstOrDefault(comment => comment.Id == id) is not { } found)
                {
                    return Task.FromResult<PipelineStepCommentView>(null!);
                }

                found.Resolved = resolved;
                found.ResolvedBy = resolved ? "ada" : null;
                found.ResolvedOnUtc = resolved ? new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc) : null;
                return Task.FromResult(Copy(found));
            }

            public Task<bool> DeleteStepCommentAsync(string definitionId, Guid id)
            {
                CommentWrites.Add("delete " + id);
                if (CommentRefusal is not null)
                {
                    return Task.FromException<bool>(CommentRefusal);
                }

                return Task.FromResult(Comments.RemoveAll(comment => comment.Id == id) > 0);
            }

            private static PipelineStepCommentView Copy(PipelineStepCommentView comment) =>
                System.Text.Json.JsonSerializer.Deserialize<PipelineStepCommentView>(System.Text.Json.JsonSerializer.Serialize(comment))!;

            /// <summary>The name and description changes asked for; each answers as a new draft, version 2.</summary>
            public List<(string DefinitionId, UpdatePipelineRequest Request)> Updated { get; } = new();

            public Task<PipelineView> UpdatePipelineAsync(string definitionId, UpdatePipelineRequest request)
            {
                Updated.Add((definitionId, request));
                return Task.FromResult(new PipelineView { Id = "v-2", DefinitionId = definitionId, Name = request.Name, Description = request.Description, Version = 2, IsLatest = true });
            }

            public int Published { get; private set; }

            /// <summary>The pipelines published, in order.</summary>
            public List<string> PublishedIds { get; } = new();

            public Task<PublishResult> PublishPipelineAsync(string definitionId, string note = null)
            {
                Published++;
                PublishedIds.Add(definitionId);
                PublishedNotes.Add(note);
                return Task.FromResult(new PublishResult { Succeeded = true });
            }

            /// <summary>What each publish said changed, in order (null for none).</summary>
            public List<string> PublishedNotes { get; } = new();

            public Task RetractPipelineAsync(string definitionId) => throw new NotSupportedException();
            public Task ArchivePipelineAsync(string definitionId) => throw new NotSupportedException();

            /// <summary>The pipelines restored from the archive - and the one opened is so no longer archived.</summary>
            public List<string> Restored { get; } = new();

            public Task RestorePipelineAsync(string definitionId)
            {
                Restored.Add(definitionId);
                ArchivedOn = null;
                return Task.CompletedTask;
            }
            public Task DeletePipelineAsync(string definitionId) => throw new NotSupportedException();

            public Task<List<string>> SetTagsAsync(string definitionId, List<string> tags) => throw new NotSupportedException();
            /// <summary>Each pipeline moved to a folder, in order.</summary>
            public List<(string DefinitionId, string Folder)> Moved { get; } = new();

            public Task<string> SetFolderAsync(string definitionId, string folder)
            {
                Moved.Add((definitionId, folder));
                return Task.FromResult(folder);
            }

            public Task<PipelineFolderMoveResult> MoveFolderAsync(PipelineFolderMoveRequest request) => throw new NotSupportedException();

            public Task<List<PipelineFoundView>> FindInPipelinesAsync(string text) => throw new NotSupportedException();

            /// <summary>The map the editor is told (the pipelines that start when this one fails); how many times it was read.</summary>
            public PipelineMapView Map { get; set; } = new();

            public int MapReads { get; private set; }

            public Task<PipelineMapView> GetMapAsync()
            {
                MapReads++;
                return Task.FromResult(Map);
            }

            /// <summary>The site's pipeline activity the editor is told, and each pipeline it was asked for.</summary>
            public List<PipelineAuditEntryView> Audit { get; } = new();

            public List<string> AuditAsked { get; } = new();

            public Task<List<PipelineAuditEntryView>> GetAuditAsync(string definitionId = null, int take = PipelineAuditLimits.DefaultTake)
            {
                AuditAsked.Add(definitionId);
                return Task.FromResult(Audit.Where(entry => definitionId is null || entry.DefinitionId == definitionId).Take(take).ToList());
            }

            public Task<List<AgentToolView>> GetAgentToolsAsync() => Task.FromResult(AgentTools.ToList());

            /// <summary>The site's variables, as the editor is told; what a save sets.</summary>
            public Dictionary<string, string> SiteVariables { get; set; } = new();

            public Task<Dictionary<string, string>> GetSiteVariablesAsync() => Task.FromResult(new Dictionary<string, string>(SiteVariables));

            public Task<Dictionary<string, string>> SetSiteVariablesAsync(Dictionary<string, string> variables)
            {
                SiteVariables = new Dictionary<string, string>(variables);
                return Task.FromResult(new Dictionary<string, string>(SiteVariables));
            }

            public List<PipelineSiteTableView> SiteTables { get; set; } = new();

            public Task<List<PipelineSiteTableView>> GetSiteTablesAsync() => Task.FromResult(SiteTables.ToList());

            public Task<PipelineSiteTableView> CreateSiteTableAsync(PipelineSiteTableRequest request) => throw new NotSupportedException();

            public Task<PipelineSiteTableView> UpdateSiteTableAsync(string tableId, PipelineSiteTableRequest request) => throw new NotSupportedException();

            public Task DeleteSiteTableAsync(string tableId) => throw new NotSupportedException();

            public Task<PipelineTableRowsView> GetTableRowsAsync(string tableId, int skip, int take, string search = null, string sort = null, bool descending = false) => throw new NotSupportedException();

            public Task<Dictionary<string, string>> AddTableRowAsync(string tableId, PipelineTableRowRequest request) => throw new NotSupportedException();

            public Task<Dictionary<string, string>> UpdateTableRowAsync(string tableId, string rowId, PipelineTableRowRequest request) => throw new NotSupportedException();

            public Task DeleteTableRowAsync(string tableId, string rowId) => throw new NotSupportedException();

            public Task<PipelineTableCsv> ExportTableCsvAsync(string tableId) => throw new NotSupportedException();

            public Task<PipelineRunReview> SetRunReviewAsync(string runId, PipelineRunReview review) => throw new NotSupportedException();

            public Task<List<PipelineEvaluationView>> GetEvaluationsAsync(string definitionId) => throw new NotSupportedException();

            public Task<PipelineEvaluationView> GetEvaluationAsync(string evaluationId) => throw new NotSupportedException();

            public Task<PipelineEvaluationView> StartEvaluationAsync(string definitionId, PipelineEvaluationRequest request) => throw new NotSupportedException();

            public Task<PipelineEvaluationView> CancelEvaluationAsync(string evaluationId) => throw new NotSupportedException();

            public Task DeleteEvaluationAsync(string evaluationId) => throw new NotSupportedException();

            public Task<PipelineTableImportResult> ImportTableCsvAsync(string tableId, string csv) => throw new NotSupportedException();
            /// <summary>The site's pipeline folder, by path: a folder holds null, a file its bytes.</summary>
            public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

            public Task<PipelineFolderView> GetPipelineFilesAsync(string folder)
            {
                folder = PipelineFilePaths.Normalize(folder);
                if (folder.Length > 0 && !(Files.TryGetValue(folder, out var held) && held is null))
                {
                    return Task.FromResult<PipelineFolderView>(null);
                }

                var inside = Files.Where(pair => PipelineFilePaths.Parent(pair.Key) == folder).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
                var view = new PipelineFolderView { Folder = folder, MaxFileBytes = 1024 };
                view.Entries.AddRange(inside.Where(pair => pair.Value is null).Select(pair => new PipelineFileEntryView { Name = PipelineFilePaths.Name(pair.Key), Path = pair.Key, IsFolder = true }));
                view.Entries.AddRange(inside.Where(pair => pair.Value is not null).Select(pair => new PipelineFileEntryView { Name = PipelineFilePaths.Name(pair.Key), Path = pair.Key, Length = pair.Value.Length }));
                return Task.FromResult(view);
            }
            public Task<PipelineFileEntryView> UploadPipelineFileAsync(string folder, string name, System.IO.Stream content) => throw new NotSupportedException();
            public Task<PipelineFileEntryView> CreatePipelineFolderAsync(string path) => throw new NotSupportedException();
            public Task<bool> DeletePipelineFileAsync(string path) => throw new NotSupportedException();

            /// <summary>The site's AI agent tools, as the editor is told.</summary>
            public List<AgentToolView> AgentTools { get; } = new();
            public Task<PipelineRunStarted> StartPipelineAsync(string definitionId, StartPipelineRequest request) => throw new NotSupportedException();
            public Task<List<PipelineRunView>> GetRunsAsync(string definitionId = null, int take = 100, string state = null, string search = null, DateOnly? from = null, DateOnly? to = null) => Task.FromResult(new List<PipelineRunView>());
            public Task<PipelineRunView> GetRunAsync(string runId) => Task.FromResult<PipelineRunView>(null!);
            public Task<List<PipelineRunLogLine>> GetRunLogAsync(string runId) => Task.FromResult(new List<PipelineRunLogLine>());
            public Task<List<PipelineStepRunView>> GetRunStepsAsync(string runId) => Task.FromResult(new List<PipelineStepRunView>());

            /// <summary>The versions the fake pipeline has, and each one's graph.</summary>
            public List<PipelineVersionView> Versions { get; } = new();

            public Dictionary<int, PipelineGraph> VersionGraphs { get; } = new();

            public Task<List<PipelineVersionView>> GetVersionsAsync(string definitionId) => Task.FromResult(Versions.ToList());

            public Task<PipelineGraph> GetVersionGraphAsync(string definitionId, int version) => Task.FromResult(VersionGraphs[version]);

            public StepTestRequest? Tested { get; private set; }

            /// <summary>Steps a whole test does not reach - a branch the run did not take.</summary>
            public HashSet<string> WholeTestSkips { get; } = new();

            public Task<StepTestResult> TestStepAsync(string definitionId, StepTestRequest request)
            {
                Tested = request;
                if (request.Whole)
                {
                    // The pipeline as it stands: every step sent ran (this fake's graphs are a chain) but the ones a test leaves out.
                    var all = new StepTestResult { Status = "Finished", Log = { "the whole pipeline ran" } };
                    all.Steps.AddRange(request.Graph.Steps.Where(each => !WholeTestSkips.Contains(each.Id)).Select(each => new PipelineStepRunView
                    {
                        StepId = each.Id, StepType = each.Type, Status = "Completed", StartedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow,
                        OutputsJson = "{ \"Result\": \"" + each.Id + " done\" }",
                        ResultCount = each.Id == "a" ? 3 : null, // step "a" passes three records on
                    }));
                    return Task.FromResult(all);
                }

                var step = request.Graph.Steps.Single(candidate => candidate.Id == request.StepId);
                var result = new StepTestResult { Status = "Finished", Log = { "wrote a line" } };
                // Up to here: every other step in the graph sent ran first (this fake's graphs are a chain).
                if (request.UpToHere)
                {
                    foreach (var before in request.Graph.Steps.Where(candidate => candidate.Id != step.Id))
                    {
                        // A step that takes its Failed path is reported as the engine's record does: completed, the failure handled.
                        result.Steps.Add(new PipelineStepRunView
                        {
                            StepId = before.Id, StepType = before.Type, Status = "Completed", StartedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow,
                            HandledFailure = before.FailedPath ? PipelineStepRunView.TookFailedPath : null, Error = before.FailedPath ? "boom" : null,
                        });
                    }
                }

                result.Steps.Add(new PipelineStepRunView
                {
                    StepId = step.Id, StepType = step.Type, Status = "Completed", StartedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow,
                    InputsJson = "{ \"Text\": \"Hello " + request.Values.GetValueOrDefault("who") + "\" }",
                    OutputsJson = "{ \"Result\": [ { \"name\": \"Ada\" } ] }", // what a pin is taken from
                });
                return Task.FromResult(result);
            }
            public Task CancelRunAsync(string runId) => throw new NotSupportedException();
            public Task<PipelineRunStarted> RetryRunAsync(string runId, bool onItsVersion = false) => throw new NotSupportedException();
            public Task<List<PipelineCallerView>> GetCallersAsync(string definitionId) => Task.FromResult((CallersOf.TryGetValue(definitionId, out var theirs) ? theirs : Callers).ToList());
            public List<PipelineCallerView> Callers { get; } = new();

            /// <summary>The callers of a pipeline by its definition id, where a test tells them apart; <see cref="Callers"/> otherwise.</summary>
            public Dictionary<string, List<PipelineCallerView>> CallersOf { get; } = new();
            public Task<PipelineRunStarted> RetryFromFailedStepAsync(string runId) => throw new NotSupportedException();
            public Task DeleteRunAsync(string runId) => throw new NotSupportedException();
            public Task<List<PipelineApprovalView>> GetApprovalsAsync(string runId = null) => Task.FromResult(new List<PipelineApprovalView>());
            public Task<PipelineApprovalDecided> DecideApprovalAsync(string approvalId, ApprovalDecisionRequest request) => throw new NotSupportedException();
            public Task<PipelineInsightsView> GetInsightsAsync(int days) => Task.FromResult(new PipelineInsightsView { Days = days });

            public Task<PipelineStepInsightsView> GetStepInsightsAsync(string definitionId, int days) => Task.FromResult(new PipelineStepInsightsView { Days = days });

            public Task<PipelineUpcomingView> GetUpcomingRunsAsync(int hours) => Task.FromResult(new PipelineUpcomingView { Hours = hours, Enabled = true });
        }

        // Injected by the collaboration panel; with the switch off it is never called.
        private sealed class UnusedCollaborationService : ICollaborationService
        {
            public Task<CollaborationOutcome<CollaborationOverviewView>> OverviewAsync() => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> TakeOverAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> CloseSessionAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<EditSessionView>> JoinAsync(string resourceType, string resourceId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<EditSessionView>> SessionAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> PresenceAsync(Guid sessionId, string activeRegion) => throw new NotSupportedException();
            public Task<CollaborationOutcome<EditSessionView>> AnnounceAsync(Guid sessionId, string descriptor) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> LeaveAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> HeartbeatAsync(Guid sessionId) => throw new NotSupportedException();
            public Task<CollaborationOutcome<ResourceLockResult>> AcquireAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<ResourceLockResult>> RenewAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<bool>> ReleaseAsync(LockRequest request) => throw new NotSupportedException();
            public Task<CollaborationOutcome<ResourceLockResult>> LockAsync(string resourceType, string resourceId) => throw new NotSupportedException();
        }

        private sealed class NoOpLogService : ILogService
        {
            public Task<List<Log>> GetLogsAsync(int siteId, string level, string function, int rows) => Task.FromResult(new List<Log>());
            public Task<Log> GetLogAsync(int logId) => Task.FromResult<Log>(null!);
            public Task DeleteLogsAsync(int siteId) => Task.CompletedTask;
            public Task Log(int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
            public Task Log(Alias alias, int? pageId, int? moduleId, int? userId, string category, string feature, string remoteIPAddress, Enums.LogFunction function, LogLevel level, Exception exception, string message, params object[] args) => Task.CompletedTask;
        }
    }
}
