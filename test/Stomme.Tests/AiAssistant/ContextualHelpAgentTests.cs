#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;
using Xunit;
using static Stomme.Tests.AiAssistant.TurnScriptChatClient;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// The predecessor platform's two <c>AiAssistantCompositionTests</c> cases for the contextual-help agent, by name: an
    /// answer phrased from the PUBLISHED topics and metered to the agent; no model call at all when no topic matches. Plus the
    /// gateway's term search (the Help module matches one phrase, so a question is searched word by word) and the topic's
    /// sanitised HTML as the bounded text a prompt carries.
    /// </summary>
    public sealed class ContextualHelpAgentTests
    {
        [Fact]
        public async Task ContextualHelpAssistantAgent_AnswersFromPublishedTopicsAndMetersUsage()
        {
            var model = new TurnScriptChatClient(Answer("Open Pipelines and choose New pipeline."));
            var models = new ScriptedModelAccess(model);
            var workspace = new RecordingQueryGateway();
            workspace.Help.Add(new AssistantHelpTopic("pipelines.index", "Create a pipeline", "Use New pipeline from the toolbar."));
            var agent = new ContextualHelpAgent(models, workspace);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("How do I create a pipeline?"));

            Assert.Equal("contextual-help", agent.Name);
            Assert.Equal("Open Pipelines and choose New pipeline.", reply.Message.Content);
            Assert.Empty(reply.Artifacts);
            Assert.Equal("How do I create a pipeline?", Assert.Single(workspace.HelpQueries));
            var prompt = model.Received.Single().Single(message => message.Role == ChatRole.User).Text;
            Assert.Contains("Title: Create a pipeline", prompt);
            Assert.Contains("Use New pipeline from the toolbar.", prompt);

            var usage = Assert.Single(models.Usage.Events);
            Assert.Equal(("contextual-help", 10L, 5L, AiCallStatus.Success), (usage.Agent, usage.InputTokens, usage.OutputTokens, usage.Status));
        }

        [Fact]
        public async Task ContextualHelpAssistantAgent_DoesNotCallProviderWhenNoTopicMatches()
        {
            var model = new TurnScriptChatClient(Answer("should not be used"));
            var models = new ScriptedModelAccess(model);

            var reply = await new ContextualHelpAgent(models, new RecordingQueryGateway()).InvokeAsync(AssistantRequests.Of("unknown thing"));

            Assert.Contains("does not cover", reply.Message.Content);
            Assert.Equal(0, model.Turns);
            Assert.Empty(models.Opened);
            Assert.Empty(models.Usage.Events);
        }

        [Fact]
        public void A_question_is_searched_by_its_words_and_a_topic_is_carried_as_bounded_text()
        {
            Assert.Equal(new[] { "create", "pipeline", "schedule", "nightly" }, MediatorAssistantQueryGateway.Terms("How do I create a pipeline, and schedule it nightly? Create!"));
            Assert.Empty(MediatorAssistantQueryGateway.Terms("   "));
            Assert.Equal(MediatorAssistantQueryGateway.MaxSearchTerms, MediatorAssistantQueryGateway.Terms(string.Join(' ', Enumerable.Range(0, 20).Select(i => "word" + i))).Count);

            Assert.Equal("Steps Open Pipelines & choose New.", MediatorAssistantQueryGateway.PlainText("<h2>Steps</h2>\n<p>Open <b>Pipelines</b> &amp; choose New.</p>"));
            Assert.Equal(MediatorAssistantQueryGateway.MaxTopicTextLength, MediatorAssistantQueryGateway.PlainText("<p>" + new string('x', 10_000) + "</p>").Length);
        }
    }
}
