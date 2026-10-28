#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// BL-97 increment 9 - the assistant's meter bridge (D113 over D102), with the predecessor platform's <c>AiUsageAssistantMeterTests</c> (15
    /// cases, re-booked here by BL-79) reconciled by name:
    /// <list type="bullet">
    /// <item><c>RecordAsync_MapsUsageToLedgerEntry</c> - <see cref="A_call_is_recorded_with_its_provider_model_agent_tokens_and_status"/>.</item>
    /// <item><c>RecordAsync_MapsCachedTurnToCachedStatus</c> - LEAVES: a cached turn is the response cache's to report
    /// (CostOptimization's, BL-97 increment 8); the bridge records what the provider served.</item>
    /// <item><c>RecordAsync_ParsesProviderIdentityAndFallsBackToUnknown</c> (5) and <c>RecordAsync_ParsesAgentIdentityAndFallsBackToUnknown</c>
    /// (6) - CHANGED: D102's recorder takes provider and agent as strings and normalises them itself, so nothing is parsed
    /// here and nothing becomes "Unknown": <see cref="Provider_and_agent_pass_through_verbatim"/>.</item>
    /// <item><c>RecordAsync_SwallowsRecorderFailuresSoTheAssistantCallSucceeds</c> -
    /// <see cref="A_recorder_failure_after_the_call_is_logged_as_an_error_and_the_reply_stands"/> (logged at Error, not dropped quietly).</item>
    /// <item><c>Host_ResolvesTheRecordingBridge_NotTheNoOpMeter</c> - there is no no-op meter to confuse it with: every
    /// client <see cref="IAssistantModelAccess"/> opens is this one (<c>AssistantModelAccessTests</c>).</item>
    /// </list>
    /// NEW with D113: the quota is asked BEFORE the call, and a spent or unreachable quota refuses it with the model never
    /// called.
    /// </summary>
    public sealed class MeteredChatClientTests
    {
        private static MeteredChatClient Metered(ScriptedChatClient inner, ScriptedQuotas quotas, RecordingAiUsage usage, string provider = "openai", string agent = "assistant", ILogger? logger = null, IDisposable? owned = null) =>
            new(inner, provider, "gpt-test", agent, quotas, usage, logger ?? NullLogger.Instance, owned);

        private static readonly ChatMessage[] Hello = { new(ChatRole.User, "hello") };

        [Fact]
        public async Task A_call_is_recorded_with_its_provider_model_agent_tokens_and_status()
        {
            var usage = new RecordingAiUsage();
            var client = Metered(new ScriptedChatClient { InputTokens = 120, OutputTokens = 45 }, new ScriptedQuotas(), usage, agent: "pipeline-architect");

            await client.GetResponseAsync(Hello);

            var recorded = Assert.Single(usage.Events);
            Assert.Equal(("openai", "gpt-test", "pipeline-architect", 120L, 45L, AiCallStatus.Success), (recorded.Provider, recorded.Model, recorded.Agent, recorded.InputTokens, recorded.OutputTokens, recorded.Status));
            Assert.True(recorded.LatencyMs >= 0);
            Assert.NotEqual(Guid.Empty, recorded.OperationId);
        }

        [Theory]
        [InlineData("azure-openai", "orchestrator")]
        [InlineData("OLLAMA", "dashboard-designer")]
        [InlineData("x.ai", "contextual-help")]
        public async Task Provider_and_agent_pass_through_verbatim(string provider, string agent)
        {
            var usage = new RecordingAiUsage();
            await Metered(new ScriptedChatClient(), new ScriptedQuotas(), usage, provider, agent).GetResponseAsync(Hello);
            Assert.Equal((provider, agent), (usage.Events.Single().Provider, usage.Events.Single().Agent));
        }

        [Fact]
        public async Task A_failed_call_is_recorded_as_an_error_and_rethrown()
        {
            var usage = new RecordingAiUsage();
            var inner = new ScriptedChatClient { Throw = new InvalidOperationException("the provider refused") };

            await Assert.ThrowsAsync<InvalidOperationException>(() => Metered(inner, new ScriptedQuotas(), usage).GetResponseAsync(Hello));

            var recorded = Assert.Single(usage.Events);
            Assert.Equal((AiCallStatus.Error, 0L, 0L), (recorded.Status, recorded.InputTokens, recorded.OutputTokens));
        }

        /// <summary>
        /// ADDED (BL-180, found driving the assistant with the site's private hosts cleared): the site's outbound guard refusing
        /// the endpoint is a refusal the caller is told, metered as an error - not a fault. The OpenAI client throws its retried
        /// attempts together, so both shapes are covered.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task An_endpoint_the_site_may_not_reach_is_a_refusal_that_says_where_to_allow_it(bool retried)
        {
            var usage = new RecordingAiUsage();
            Exception denial = new System.Net.Http.HttpRequestException("send failed", new Stomme.Infrastructure.Security.OutboundDestinationDeniedException("The destination '127.0.0.1' is not permitted."));
            if (retried)
            {
                denial = new AggregateException(new InvalidOperationException("first attempt"), denial);
            }

            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => Metered(new ScriptedChatClient { Throw = denial }, new ScriptedQuotas(), usage).GetResponseAsync(Hello));

            Assert.Equal(AssistantUnavailableReason.EndpointNotPermitted, refused.Reason);
            Assert.Contains("Private hosts on the Connections page", refused.Message, StringComparison.Ordinal);
            Assert.Equal(AiCallStatus.Error, Assert.Single(usage.Events).Status);
            var described = AssistantRefusals.Describe(refused)!;
            Assert.Equal((409, "EndpointNotPermitted"), (described.Status, described.View.Code));
        }

        [Fact]
        public async Task A_streamed_call_is_recorded_with_the_usage_the_stream_reported()
        {
            var usage = new RecordingAiUsage();
            var client = Metered(new ScriptedChatClient { InputTokens = 30, OutputTokens = 12 }, new ScriptedQuotas(), usage);

            var text = string.Concat(await client.GetStreamingResponseAsync(Hello).Select(update => update.Text).ToListAsync());

            Assert.Equal("scripted reply", text);
            var recorded = Assert.Single(usage.Events);
            Assert.Equal((30L, 12L, AiCallStatus.Success), (recorded.InputTokens, recorded.OutputTokens, recorded.Status));
        }

        /// <summary>
        /// BL-251: a streamed call was recorded only after its stream ended, so one that failed - or that the egress guard
        /// refused - was neither metered nor in the usage log, and its refusal was not mapped. Now as the non-streamed call.
        /// </summary>
        [Fact]
        public async Task A_streamed_call_that_fails_is_recorded_as_an_error_and_rethrown()
        {
            var usage = new RecordingAiUsage();
            var inner = new ScriptedChatClient { Throw = new InvalidOperationException("the provider refused") };

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Metered(inner, new ScriptedQuotas(), usage).GetStreamingResponseAsync(Hello).ToListAsync());

            Assert.Equal(AiCallStatus.Error, Assert.Single(usage.Events).Status);
        }

        [Fact]
        public async Task A_streamed_call_to_an_endpoint_the_site_may_not_reach_is_the_same_refusal()
        {
            var usage = new RecordingAiUsage();
            var denial = new System.Net.Http.HttpRequestException("send failed", new Stomme.Infrastructure.Security.OutboundDestinationDeniedException("The destination '127.0.0.1' is not permitted."));

            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(async () => await Metered(new ScriptedChatClient { Throw = denial }, new ScriptedQuotas(), usage).GetStreamingResponseAsync(Hello).ToListAsync());

            Assert.Equal(AssistantUnavailableReason.EndpointNotPermitted, refused.Reason);
            Assert.Equal(AiCallStatus.Error, Assert.Single(usage.Events).Status);
        }

        [Fact]
        public async Task A_stream_the_caller_stops_reading_is_still_recorded()
        {
            var usage = new RecordingAiUsage();

            await foreach (var _ in Metered(new ScriptedChatClient(), new ScriptedQuotas(), usage).GetStreamingResponseAsync(Hello))
            {
                break;
            }

            Assert.Equal(AiCallStatus.Success, Assert.Single(usage.Events).Status);
        }

        [Theory]
        [InlineData("ai.calls")]
        [InlineData("ai.tokens")]
        public async Task A_spent_quota_refuses_the_call_before_the_model_is_asked(string spent)
        {
            var quotas = new ScriptedQuotas();
            quotas.Decisions[spent] = new QuotaDecision(false, QuotaDecisionReason.Exceeded, 100, 100, 0);
            var inner = new ScriptedChatClient();
            var usage = new RecordingAiUsage();

            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => Metered(inner, quotas, usage).GetResponseAsync(Hello));

            Assert.Equal(AssistantUnavailableReason.QuotaExceeded, refused.Reason);
            Assert.Contains(spent, refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, inner.Calls);
            Assert.Empty(usage.Events);
            await Assert.ThrowsAsync<AssistantUnavailableException>(async () => await Metered(inner, quotas, usage).GetStreamingResponseAsync(Hello).ToListAsync());
            Assert.Equal(0, inner.Calls);
        }

        [Fact]
        public async Task An_unreachable_meter_refuses_the_call_fail_closed()
        {
            var inner = new ScriptedChatClient();
            var thrown = new ScriptedQuotas { Throw = new MeteringUnavailableException("the metering store is down") };
            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => Metered(inner, thrown, new RecordingAiUsage()).GetResponseAsync(Hello));
            Assert.Equal(AssistantUnavailableReason.MeteringUnavailable, refused.Reason);

            var unavailable = new ScriptedQuotas();
            unavailable.Decisions["ai.calls"] = new QuotaDecision(false, QuotaDecisionReason.Unavailable, 0, 0, 0);
            refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => Metered(inner, unavailable, new RecordingAiUsage()).GetResponseAsync(Hello));
            Assert.Equal(AssistantUnavailableReason.MeteringUnavailable, refused.Reason);
            Assert.Equal(0, inner.Calls);
        }

        [Fact]
        public async Task An_undefined_or_unlimited_quota_lets_the_call_through()
        {
            var quotas = new ScriptedQuotas();
            quotas.Decisions["ai.calls"] = new QuotaDecision(true, QuotaDecisionReason.Unlimited, 0, 0, 0);
            var inner = new ScriptedChatClient();
            await Metered(inner, quotas, new RecordingAiUsage()).GetResponseAsync(Hello);
            Assert.Equal(1, inner.Calls);
            Assert.Equal(new[] { "ai.calls", "ai.tokens" }, quotas.Asked);
        }

        [Fact]
        public async Task A_recorder_failure_after_the_call_is_logged_as_an_error_and_the_reply_stands()
        {
            var logger = new CapturingLogger();
            var usage = new RecordingAiUsage { Throw = new MeteringUnavailableException("the ledger is down") };

            var response = await Metered(new ScriptedChatClient(), new ScriptedQuotas(), usage, logger: logger).GetResponseAsync(Hello);

            Assert.Equal("scripted reply", response.Text);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("could not be recorded", StringComparison.Ordinal));
        }

        [Fact]
        public void Disposing_the_client_releases_the_egress_connection_it_owns()
        {
            var inner = new ScriptedChatClient();
            var owned = new Owned();
            Metered(inner, new ScriptedQuotas(), new RecordingAiUsage(), owned: owned).Dispose();
            Assert.True(inner.Disposed);
            Assert.True(owned.Disposed);
        }

        private sealed class Owned : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
