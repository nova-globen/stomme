#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Metering;
using Stomme.Modules.AiAssistant.Application;
using Xunit;
using static Stomme.Tests.AiAssistant.TurnScriptChatClient;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// The chat agent, over a scripted model behind the REAL metered client. Metering is per MODEL CALL through
    /// <see cref="MeteredChatClient"/> - a tool round and the answer are two rows, each attributed to the agent, rather
    /// than one aggregate row per turn; a disabled assistant surfaces <see cref="AssistantUnavailableException"/> with
    /// its reason.
    /// </summary>
    public sealed class ChatAssistantAgentTests
    {
        private static TurnScriptChatClient ToolThenAnswer() =>
            new(Call("call-1", "ListDatasets", new Dictionary<string, object?>()), Answer("You have 1 dataset."));

        private static (ChatAssistantAgent Agent, ScriptedModelAccess Models, RecordingQueryGateway Workspace) Build(
            TurnScriptChatClient model, IModelTierRouter? router = null, IModelResponseCache? cache = null, float? temperature = null)
        {
            var models = new ScriptedModelAccess(model, temperature);
            var workspace = new RecordingQueryGateway();
            workspace.AddDataset("Sales", "Month", "Revenue");
            return (new ChatAssistantAgent(models, workspace, router, cache), models, workspace);
        }

        [Fact]
        public async Task InvokeAsync_runs_the_tool_call_loop_and_meters_token_usage()
        {
            var model = ToolThenAnswer();
            var (agent, models, workspace) = Build(model);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("list my datasets"));

            Assert.Equal("You have 1 dataset.", reply.Message.Content);
            Assert.True(workspace.ListDatasetsCalled);
            Assert.Equal(2, model.Turns);
            Assert.Contains("Sales", Assert.Single(model.ToolResults()));

            // Every model call is gated first and metered after, attributed to this agent.
            Assert.Equal(2, models.Usage.Events.Count);
            Assert.All(models.Usage.Events, usage => Assert.Equal(("test", "test-model", "assistant", 10L, 5L, AiCallStatus.Success), (usage.Provider, usage.Model, usage.Agent, usage.InputTokens, usage.OutputTokens, usage.Status)));
            Assert.Equal(new[] { "ai.calls", "ai.tokens", "ai.calls", "ai.tokens" }, models.Quotas.Asked);
        }

        [Fact]
        public async Task InvokeAsync_throws_when_the_assistant_is_disabled()
        {
            var model = ToolThenAnswer();
            var (agent, models, _) = Build(model);
            models.Enabled = false;

            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => agent.InvokeAsync(AssistantRequests.Of("hi")));

            Assert.Equal(AssistantUnavailableReason.Disabled, refused.Reason);
            Assert.Equal(0, model.Turns);
        }

        [Fact]
        public async Task InvokeAsync_uses_the_model_tier_routers_model_when_it_returns_one()
        {
            var model = ToolThenAnswer();
            var router = new FixedModelTierRouter(new ModelTierDecision(ModelTier.Simple, "cheap-model"));
            var (agent, models, _) = Build(model, router);

            await agent.InvokeAsync(AssistantRequests.Of("list my datasets"));

            Assert.Equal("cheap-model", model.ModelIds[0]);
            Assert.NotNull(router.LastRequest);
            Assert.True(router.LastRequest!.ToolCount > 0);
            Assert.True(router.LastRequest.TokenEstimate > 0);

            // The meter records the model the call was MADE with, not the site's default.
            Assert.All(models.Usage.Events, usage => Assert.Equal("cheap-model", usage.Model));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task InvokeAsync_keeps_the_default_model_when_no_router_or_no_mapped_model(bool withRouter)
        {
            var model = ToolThenAnswer();
            var router = withRouter ? new FixedModelTierRouter(new ModelTierDecision(ModelTier.Simple, Model: null)) : null;
            var (agent, _, _) = Build(model, router);

            await agent.InvokeAsync(AssistantRequests.Of("list my datasets"));

            Assert.Equal("test-model", model.ModelIds[0]);
        }

        [Fact]
        public async Task InvokeAsync_returns_the_cached_reply_without_calling_the_provider_on_a_cache_hit()
        {
            var model = ToolThenAnswer();
            var cache = new FakeResponseCache();
            cache.Prime(new CachedModelResponse("cached answer", "test-model", 10, 5));
            var (agent, models, _) = Build(model, cache: cache, temperature: 0f);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("list my datasets"));

            Assert.Equal("cached answer", reply.Message.Content);
            Assert.Equal(0, model.Turns);

            // The hit is metered as cached, with the original counts (a cached call is billed at nothing).
            var usage = Assert.Single(models.Usage.Events);
            Assert.Equal(("test", "test-model", "assistant", 10L, 5L, AiCallStatus.Cached), (usage.Provider, usage.Model, usage.Agent, usage.InputTokens, usage.OutputTokens, usage.Status));
            Assert.Empty(models.Quotas.Asked);
        }

        [Fact]
        public async Task InvokeAsync_dispatches_and_stores_on_a_cache_miss_then_hits_on_the_identical_turn()
        {
            var model = ToolThenAnswer();
            var cache = new FakeResponseCache();
            var (agent, models, _) = Build(model, cache: cache, temperature: 0f);
            var request = AssistantRequests.Of("list my datasets");

            var first = await agent.InvokeAsync(request);
            var turnsAfterMiss = model.Turns;
            var second = await agent.InvokeAsync(request);

            Assert.Equal(2, turnsAfterMiss);
            Assert.Equal("You have 1 dataset.", Assert.Single(cache.Entries.Values).Text);
            Assert.Equal(turnsAfterMiss, model.Turns);
            Assert.Equal(2, cache.ObservedKeys.Count);
            Assert.Equal(cache.ObservedKeys[0], cache.ObservedKeys[1]);
            Assert.Equal(first.Message.Content, second.Message.Content);

            // The miss is two metered calls (the tool round and the answer); the hit is one cached row.
            Assert.Equal(2, models.Usage.Events.Count(usage => usage.Status == AiCallStatus.Success));
            Assert.Single(models.Usage.Events, usage => usage.Status == AiCallStatus.Cached);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0.7f)]
        public async Task InvokeAsync_never_consults_the_cache_for_a_non_deterministic_turn(float? temperature)
        {
            var model = ToolThenAnswer();
            var cache = new FakeResponseCache();
            var (agent, _, _) = Build(model, cache: cache, temperature: temperature);

            var reply = await agent.InvokeAsync(AssistantRequests.Of("list my datasets"));

            Assert.Empty(cache.ObservedKeys);
            Assert.Equal(2, model.Turns);
            Assert.Equal("You have 1 dataset.", reply.Message.Content);
        }

        /// <summary>
        /// A model that never stops calling a tool is cut off at
        /// <see cref="AssistantTurn.MaxToolRounds"/>. The library's default is 40 rounds, each a metered, billed call.
        /// </summary>
        [Fact]
        public async Task A_model_that_never_stops_calling_tools_is_cut_off_at_the_bound()
        {
            // A new call every round, as a model makes it: a call the loop has handled is not invoked again.
            var model = new TurnScriptChatClient(Enumerable.Range(0, 50).Select(round => Call($"again-{round}", "ListDatasets", new Dictionary<string, object?>())).ToArray());
            var (agent, models, _) = Build(model);

            await agent.InvokeAsync(AssistantRequests.Of("list my datasets"));

            Assert.Equal(AssistantTurn.MaxToolRounds + 1, model.Turns);
            Assert.Equal(model.Turns, models.Usage.Events.Count);
        }

        [Fact]
        public async Task A_spent_quota_refuses_the_turn_before_the_model_is_called()
        {
            var model = ToolThenAnswer();
            var (agent, models, workspace) = Build(model);
            models.Quotas.Decisions["ai.calls"] = new QuotaDecision(false, QuotaDecisionReason.Exceeded, 100, 100, 0);

            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => agent.InvokeAsync(AssistantRequests.Of("list my datasets")));

            Assert.Equal(AssistantUnavailableReason.QuotaExceeded, refused.Reason);
            Assert.Equal(0, model.Turns);
            Assert.False(workspace.ListDatasetsCalled);
            Assert.Empty(models.Usage.Events);
        }
    }
}
