#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.AiAssistant.Domain;
using Xunit;
using static Stomme.Tests.AiAssistant.TurnScriptChatClient;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// BL-97 increment 10 - the predecessor platform's <c>PipelineArchitectAgentTests</c>, by name: the model's ProposePipeline call becomes a
    /// PROPOSED artifact whose draft round-trips; an invalid draft goes back to the model and the corrected one is proposed;
    /// a connection the user cannot reach is refused before any validation or write; applying runs the create command and
    /// only then marks the artifact applied. <b>CHANGED:</b> a step's type is one from the engine's catalog and the
    /// pipeline's id is the engine's string definition id.
    /// </summary>
    public sealed class PipelineArchitectAgentTests
    {
        private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

        private static AssistantPipelineDraft ValidDraft() =>
            new("Daily Ingest", "Ingest sales nightly",
                [new AssistantPipelineStepDraft("Stomme.Timer", "Nightly"), new AssistantPipelineStepDraft("Stomme.WriteLine", "Ingest", Inputs: new Dictionary<string, string> { ["Text"] = "ingesting" })],
                [new AssistantPipelineRelationDraft(0, 1)]);

        private static (PipelineArchitectAgent Agent, ScriptedModelAccess Models) Build(TurnScriptChatClient model, RecordingQueryGateway workspace, RecordingCommandGateway commands)
        {
            var models = new ScriptedModelAccess(model);
            return (new PipelineArchitectAgent(models, workspace, commands), models);
        }

        [Fact]
        public async Task InvokeAsync_turns_a_request_into_a_proposed_pipeline_draft()
        {
            var draft = ValidDraft();
            var model = new TurnScriptChatClient(Call("call-1", "ProposePipeline", "draft", draft), Answer("Proposed your pipeline."));
            var commands = new RecordingCommandGateway();
            var (agent, models) = Build(model, new RecordingQueryGateway(), commands);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("build a daily ingest pipeline"));

            var artifact = Assert.Single(reply.Artifacts);
            Assert.Equal((ArtifactKind.Pipeline, draft.Name), (artifact.Kind, artifact.Title));
            var staged = JsonSerializer.Deserialize<AssistantPipelineDraft>(artifact.SpecJson, DraftJson)!;
            Assert.Equal(draft.Name, staged.Name);
            Assert.Equal(2, staged.Steps.Count);
            Assert.Equal("ingesting", staged.Steps[1].Inputs!["Text"]);

            // Validated, and nothing created: an artifact is never auto-applied.
            Assert.Equal(1, commands.ValidateCount);
            Assert.Empty(commands.CreatedPipelines);
            Assert.Equal("Proposed your pipeline.", reply.Message.Content);
            Assert.All(models.Usage.Events, usage => Assert.Equal("pipeline-architect", usage.Agent));
        }

        [Fact]
        public async Task Applying_a_proposed_pipeline_invokes_the_create_command_and_marks_it_applied()
        {
            var draft = ValidDraft();
            var commands = new RecordingCommandGateway();
            var applier = new PipelineArtifactApplier(commands, TimeProvider.System);
            var conversation = AssistantConversationAggregate.Start(Guid.NewGuid(), AssistantRequests.Tenant, 1, 7, DateTime.UtcNow);
            var artifactId = Guid.NewGuid();
            conversation.ProposeArtifact(artifactId, ArtifactKind.Pipeline, draft.Name, JsonSerializer.Serialize(draft, DraftJson), AgentTrace.Create("pipeline-architect", "drafted on request"), null, DateTime.UtcNow);

            var pipelineId = await applier.ApplyAsync(conversation, artifactId);

            Assert.Equal(commands.CreatedPipelineId, pipelineId);
            Assert.Equal(draft.Name, Assert.Single(commands.CreatedPipelines).Name);
            Assert.Equal(ArtifactState.Applied, conversation.Artifacts.Single().State);
        }

        [Fact]
        public async Task InvokeAsync_re_validates_after_an_invalid_draft_and_proposes_the_corrected_one()
        {
            var invalid = new AssistantPipelineDraft("No Trigger", null, [new AssistantPipelineStepDraft("Stomme.WriteLine", "Load")], []);
            var corrected = ValidDraft();
            var model = new TurnScriptChatClient(Call("call-1", "ProposePipeline", "draft", invalid), Call("call-2", "ProposePipeline", "draft", corrected), Answer("Fixed and proposed."));
            var commands = new RecordingCommandGateway
            {
                Validator = draft => draft.Steps.Any(step => step.Type == "Stomme.Timer")
                    ? AssistantPipelineValidationResult.Valid
                    : AssistantPipelineValidationResult.Invalid(["A pipeline must have at least one Trigger step."]),
            };
            var (agent, _) = Build(model, new RecordingQueryGateway(), commands);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("build a pipeline"));

            Assert.Equal(2, commands.ValidateCount);
            Assert.Equal(corrected.Name, Assert.Single(reply.Artifacts).Title);
            Assert.Contains(model.Received[1].SelectMany(message => message.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>()), result => result.Result!.ToString()!.Contains("at least one Trigger step"));
        }

        [Fact]
        public async Task InvokeAsync_blocks_a_draft_that_references_an_unauthorized_connection()
        {
            var workspace = new RecordingQueryGateway();
            workspace.Connections.Add(new AssistantConnectionInfo(Guid.NewGuid(), "Warehouse", "postgresql", "Connected"));
            var unauthorized = Guid.NewGuid();
            var draft = new AssistantPipelineDraft("Cross-tenant", null,
                [new AssistantPipelineStepDraft("Stomme.Timer", "Start"), new AssistantPipelineStepDraft("Stomme.ExtractFromConnection", "Read", ConnectionId: unauthorized)],
                [new AssistantPipelineRelationDraft(0, 1)]);
            var model = new TurnScriptChatClient(Call("call-1", "ProposePipeline", "draft", draft), Answer("I cannot use that connection."));
            var commands = new RecordingCommandGateway();
            var (agent, _) = Build(model, workspace, commands);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("read from a connection I don't own"));

            Assert.Empty(reply.Artifacts);
            Assert.Equal(0, commands.ValidateCount);
            Assert.Empty(commands.CreatedPipelines);
            Assert.Contains(model.ToolResults(), result => result.Contains(unauthorized.ToString()));
        }

        [Fact]
        public async Task A_connection_written_into_the_Connection_input_is_checked_like_a_connection_id()
        {
            var workspace = new RecordingQueryGateway();
            var own = new AssistantConnectionInfo(Guid.NewGuid(), "Warehouse", "postgresql", "Connected");
            workspace.Connections.Add(own);
            var smuggled = new AssistantPipelineDraft("Smuggled", null,
                [new AssistantPipelineStepDraft("Stomme.ExtractFromConnection", "Read", Inputs: new Dictionary<string, string> { ["Connection"] = Guid.NewGuid().ToString() })], []);
            var mine = new AssistantPipelineDraft("Mine", null,
                [new AssistantPipelineStepDraft("Stomme.ExtractFromConnection", "Read", Inputs: new Dictionary<string, string> { ["Connection"] = own.Id.ToString() })], []);
            var model = new TurnScriptChatClient(Call("call-1", "ProposePipeline", "draft", smuggled), Call("call-2", "ProposePipeline", "draft", mine), Answer("Done."));
            var commands = new RecordingCommandGateway();
            var (agent, _) = Build(model, workspace, commands);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("read my warehouse"));

            Assert.Equal(1, commands.ValidateCount);
            Assert.Equal("Mine", Assert.Single(reply.Artifacts).Title);
        }
    }
}
