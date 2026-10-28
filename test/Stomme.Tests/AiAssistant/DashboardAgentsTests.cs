#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.AiAssistant.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static Stomme.Tests.AiAssistant.TurnScriptChatClient;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// BL-97 increment 10 - the predecessor platform's <c>DashboardDesignerAgentTests</c> and <c>WidgetGeneratorAgentTests</c>, by name. The
    /// Designer asks the Widget Generator for a chart spec as ONE tool and proposes the board; applying runs the create command
    /// and only then marks it applied. The Generator accepts a spec only when theme-free with every column resolved, within
    /// three submissions. <b>ADDED:</b> a widget bound to a dataset the user cannot see, or of a kind the Dashboards module does
    /// not have, goes back to the model; the proposal names the datasets it binds; the attempt cap is ENFORCED (the predecessor platform's tool
    /// still validated - and could accept - a fourth submission).
    /// </summary>
    public sealed class DashboardAgentsTests
    {
        private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

        private const string ValidSpec = """{"dataset":{"dimensions":["$Month","$Sales"]},"xAxis":{"type":"category"},"yAxis":{"type":"value"},"series":[{"type":"bar"}]}""";

        private static AssistantDashboardDraft DashboardWithChart(Guid datasetId) =>
            new("Sales Overview", 12, 40, [new AssistantWidgetDraft("Sales", "Chart", datasetId, ["Sales", "Month"], 0, 0, 6, 4, ValidSpec)]);

        private static ChatMessage CallGenerateWidgetSpec(string callId, Guid datasetId, string description) =>
            Call(callId, "GenerateWidgetSpec", new Dictionary<string, object?> { ["datasetId"] = JsonSerializer.SerializeToElement(datasetId), ["description"] = description });

        private sealed class FixedWidgetGenerator(WidgetSpecResult result) : IWidgetSpecGenerator
        {
            public List<(AssistantDataSummary Summary, string Request)> Asked { get; } = new();

            public Task<WidgetSpecResult> GenerateAsync(AssistantDataSummary summary, string request, CancellationToken cancellationToken = default)
            {
                Asked.Add((summary, request));
                return Task.FromResult(result);
            }
        }

        [Fact]
        public async Task InvokeAsync_designs_a_dashboard_and_proposes_it_as_an_artifact()
        {
            var workspace = new RecordingQueryGateway();
            var sales = workspace.AddDataset("Sales", "Month", "Sales");
            var draft = DashboardWithChart(sales);
            var model = new TurnScriptChatClient(CallGenerateWidgetSpec("call-1", sales, "bar chart of sales by month"), Call("call-2", "ProposeDashboard", "draft", draft), Answer("Proposed your dashboard."));
            var generator = new FixedWidgetGenerator(WidgetSpecResult.Ok(ValidSpec));
            var agent = new DashboardDesignerAgent(new ScriptedModelAccess(model), workspace, generator);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("build a sales dashboard"));

            var artifact = Assert.Single(reply.Artifacts);
            Assert.Equal((ArtifactKind.Dashboard, draft.Name), (artifact.Kind, artifact.Title));
            Assert.Equal(ValidSpec, Assert.Single(JsonSerializer.Deserialize<AssistantDashboardDraft>(artifact.SpecJson, DraftJson)!.Widgets).ChartSpecJson);
            Assert.Equal(new[] { sales }, artifact.ReferencedDatasetIds);

            // The generator saw the dataset's bounded summary, and its spec went back to the model as the tool's result.
            Assert.Equal(sales, Assert.Single(generator.Asked).Summary.DatasetId);
            Assert.Equal(ValidSpec, model.ToolResults().First());
        }

        [Fact]
        public async Task Applying_a_proposed_dashboard_invokes_the_create_command_and_marks_it_applied()
        {
            var dataset = Guid.NewGuid();
            var draft = DashboardWithChart(dataset);
            var commands = new RecordingCommandGateway();
            var applier = new DashboardArtifactApplier(commands, TimeProvider.System);
            var conversation = AssistantConversationAggregate.Start(Guid.NewGuid(), AssistantRequests.Tenant, 1, 7, DateTime.UtcNow);
            conversation.AttachDataContext(DataContextRef.Create(dataset), DateTime.UtcNow);
            var artifactId = Guid.NewGuid();
            conversation.ProposeArtifact(artifactId, ArtifactKind.Dashboard, draft.Name, JsonSerializer.Serialize(draft, DraftJson), AgentTrace.Create("dashboard-designer", "designed on request"), [dataset], DateTime.UtcNow);

            var dashboardId = await applier.ApplyAsync(conversation, artifactId);

            Assert.Equal(commands.CreatedDashboardId, dashboardId);
            var created = Assert.Single(commands.CreatedDashboards);
            Assert.Equal(draft.Name, created.Name);
            Assert.Single(created.Widgets);
            Assert.Equal(ArtifactState.Applied, conversation.Artifacts.Single().State);
        }

        [Fact]
        public async Task A_failed_apply_leaves_the_artifact_proposed()
        {
            var draft = DashboardWithChart(Guid.NewGuid());
            var commands = new RecordingCommandGateway { FailCreate = new InvalidOperationException("the Dashboards module refused it") };
            var conversation = AssistantConversationAggregate.Start(Guid.NewGuid(), AssistantRequests.Tenant, 1, 7, DateTime.UtcNow);
            var artifactId = Guid.NewGuid();
            conversation.ProposeArtifact(artifactId, ArtifactKind.Dashboard, draft.Name, JsonSerializer.Serialize(draft, DraftJson), AgentTrace.Create("dashboard-designer", null), null, DateTime.UtcNow);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new DashboardArtifactApplier(commands, TimeProvider.System).ApplyAsync(conversation, artifactId));

            Assert.Equal(ArtifactState.Proposed, conversation.Artifacts.Single().State);
            await Assert.ThrowsAsync<AssistantConflictException>(() => new PipelineArtifactApplier(commands, TimeProvider.System).ApplyAsync(conversation, artifactId));
        }

        [Fact]
        public async Task A_widget_on_an_unseen_dataset_or_of_an_unknown_kind_goes_back_to_the_model()
        {
            var workspace = new RecordingQueryGateway();
            var sales = workspace.AddDataset("Sales", "Month", "Sales");
            var hidden = Guid.NewGuid();
            var wrong = new AssistantDashboardDraft("Board", 12, 40,
                [new AssistantWidgetDraft("Hidden", "Grid", hidden, ["A"], 0, 0, 6, 4), new AssistantWidgetDraft("Pie", "Pie", sales, ["Sales"], 6, 0, 6, 4)]);
            var model = new TurnScriptChatClient(Call("call-1", "ProposeDashboard", "draft", wrong), Answer("I could not."));
            var agent = new DashboardDesignerAgent(new ScriptedModelAccess(model), workspace, new FixedWidgetGenerator(WidgetSpecResult.Ok(ValidSpec)));

            var reply = await agent.InvokeAsync(AssistantRequests.Of("a board"));

            Assert.Empty(reply.Artifacts);
            var result = Assert.Single(model.ToolResults());
            Assert.Contains(hidden.ToString(), result);
            Assert.Contains("kind 'Pie'", result);
        }

        private static AssistantDataSummary Summary() =>
            new(Guid.NewGuid(), [new AssistantColumnSummary("Month", ["Jan", "Feb"], "Jan", "Dec", 12, 0), new AssistantColumnSummary("Sales", ["10", "20"], "0", "999", 500, 0)], 1000);

        private static ChatMessage SubmitSpec(string callId, string spec) => Call(callId, "SubmitChartSpec", new Dictionary<string, object?> { ["echartsJson"] = spec });

        [Fact]
        public async Task GenerateAsync_returns_the_accepted_spec_when_it_is_valid()
        {
            var model = new TurnScriptChatClient(SubmitSpec("call-1", ValidSpec), Answer("Here is your chart."));
            var models = new ScriptedModelAccess(model);

            var result = await new WidgetGeneratorAgent(models).GenerateAsync(Summary(), "bar chart of sales by month");

            Assert.True(result.Succeeded);
            Assert.Equal(ValidSpec, result.EchartsJson);
            Assert.Equal(new[] { "widget-generator" }, models.Opened);

            // Only the bounded summary reached the model - the columns and their statistics, never a row.
            var prompt = model.Received[0][1].Text;
            Assert.Contains("- Month (unique=12, nulls=0, range=Jan..Dec)", prompt);
            Assert.DoesNotContain("Feb", prompt);
        }

        [Fact]
        public async Task GenerateAsync_repairs_a_theme_violation_and_proposes_the_corrected_spec()
        {
            const string themed = """{"color":["#ff0000"],"series":[{"type":"bar","encode":{"y":"$Sales"}}]}""";
            var model = new TurnScriptChatClient(SubmitSpec("call-1", themed), SubmitSpec("call-2", ValidSpec), Answer("Fixed the theme and proposed it."));

            var result = await new WidgetGeneratorAgent(new ScriptedModelAccess(model)).GenerateAsync(Summary(), "bar chart");

            Assert.True(result.Succeeded);
            Assert.Equal(ValidSpec, result.EchartsJson);
        }

        [Fact]
        public async Task GenerateAsync_fails_when_column_references_never_resolve_within_the_attempt_budget()
        {
            const string badColumn = """{"series":[{"type":"bar","encode":{"y":"$Revenue"}}]}""";
            var model = new TurnScriptChatClient(SubmitSpec("call-1", badColumn), SubmitSpec("call-2", badColumn), SubmitSpec("call-3", badColumn), Answer("I could not produce a valid spec."));

            var result = await new WidgetGeneratorAgent(new ScriptedModelAccess(model)).GenerateAsync(Summary(), "chart of revenue");

            Assert.False(result.Succeeded);
            Assert.Null(result.EchartsJson);
            Assert.Contains(result.Errors, error => error.Contains("$Revenue") && error.Contains("not in the dataset schema"));
        }

        [Fact]
        public async Task A_fourth_submission_is_not_accepted_even_when_valid()
        {
            const string badColumn = """{"series":[{"type":"bar","encode":{"y":"$Revenue"}}]}""";
            var model = new TurnScriptChatClient(SubmitSpec("call-1", badColumn), SubmitSpec("call-2", badColumn), SubmitSpec("call-3", badColumn), SubmitSpec("call-4", ValidSpec), Answer("Done."));

            var result = await new WidgetGeneratorAgent(new ScriptedModelAccess(model)).GenerateAsync(Summary(), "chart of revenue");

            Assert.False(result.Succeeded);
            Assert.Contains(model.ToolResults(), text => text.Contains("maximum number of attempts is spent"));
        }
    }
}
