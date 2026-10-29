#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The assistant's meter bridge (D113 over D102), as a <see cref="DelegatingChatClient"/> around the
    /// provider's client, so no agent can make an unmetered call: BEFORE a call the site's AI quota is asked - the calls
    /// metric for one more call, the tokens metric for any budget left - and a spent quota or an unreachable meter REFUSES
    /// the call (fail-closed); AFTER it the call is recorded through <see cref="IAiUsageRecorder"/>, which prices it
    /// server-side and folds it into the same quotas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CHANGED from the predecessor platform's <c>AiUsageAssistantMeter</c>.</b> It mapped provider and agent names onto closed enums
    /// (unknown values became <c>Unknown</c>) and passed the caller's user id; D102's recorder takes both as strings it
    /// normalises itself, and derives the user from the request - so the mapping is gone, not ported. the predecessor platform's meter had no
    /// pre-call check at all; the quota gate is new with D113.
    /// </para>
    /// <para>
    /// <b>A recording failure after a completed call is logged at ERROR and does not fail the reply</b> - the tokens are
    /// already spent, and D102 leaves that choice to the producer; a lost row is reported, never silent. A failure BEFORE the
    /// call (the quota cannot be asked) refuses it.
    /// </para>
    /// </remarks>
    public sealed class MeteredChatClient : DelegatingChatClient
    {
        private readonly string _provider;
        private readonly string _model;
        private readonly string _agent;
        private readonly IQuotaInspector _quotas;
        private readonly IAiUsageRecorder _recorder;
        private readonly ILogger _logger;
        private readonly IDisposable? _owned;

        /// <param name="owned">What the client owns beyond the inner client - the site's egress HTTP client - disposed with it.</param>
        public MeteredChatClient(IChatClient inner, string provider, string model, string agent, IQuotaInspector quotas, IAiUsageRecorder recorder, ILogger logger, IDisposable? owned = null)
            : base(inner)
        {
            _owned = owned;
            _provider = provider;
            _model = model;
            _agent = agent;
            _quotas = quotas;
            _recorder = recorder;
            _logger = logger;
        }

        /// <summary>The metrics the gate asks before a call.</summary>
        public static readonly IReadOnlyList<string> GatedMetrics = new[] { "ai.calls", "ai.tokens" };

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await EnsureWithinQuotaAsync(cancellationToken);
            var timer = Stopwatch.StartNew();
            ChatResponse response;
            try
            {
                response = await base.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception ex)
            {
                await RecordAsync(ModelOf(options), 0, 0, timer.ElapsedMilliseconds, AiCallStatus.Error);
                if (IsEgressDenial(ex))
                {
                    // BL-180, found driving it: the site's outbound guard refused the endpoint's address. That is a refusal to
                    // show the user, not a fault - the chat panel crashed and the API answered 500 on it.
                    throw new AssistantUnavailableException(AssistantUnavailableReason.EndpointNotPermitted,
                        "The model endpoint is on a private, loopback or internal address this site may not reach. A Host user can allow its host under Private hosts on the Connections page.", ex);
                }

                throw;
            }

            await RecordAsync(ModelOf(options), response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0, timer.ElapsedMilliseconds, AiCallStatus.Success);
            return response;
        }

        // The OpenAI client retries a failed send and throws the attempts together, so an aggregate is searched attempt by attempt.
        private static bool IsEgressDenial(Exception exception) =>
            Stomme.Infrastructure.Security.OutboundConnectGuard.IsDenial(exception)
            || (exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(Stomme.Infrastructure.Security.OutboundConnectGuard.IsDenial));

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await EnsureWithinQuotaAsync(cancellationToken);
            var timer = Stopwatch.StartNew();
            long input = 0, output = 0;
            var recorded = false;
            // The stream used to be recorded only after it ended, so a streamed call that failed or that the egress
            // guard refused was never metered nor in the usage log, and its refusal was not mapped. A yield cannot sit in a
            // try with a catch, so the enumerator is driven by hand: each step's failure is recorded and mapped like the
            // non-streamed call's, and a stream the caller stops reading early is recorded with what it used so far.
            await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    try
                    {
                        if (!await updates.MoveNextAsync())
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        recorded = true;
                        await RecordAsync(ModelOf(options), input, output, timer.ElapsedMilliseconds, AiCallStatus.Error);
                        if (IsEgressDenial(ex))
                        {
                            throw new AssistantUnavailableException(AssistantUnavailableReason.EndpointNotPermitted,
                                "The model endpoint is on a private, loopback or internal address this site may not reach. A Host user can allow its host under Private hosts on the Connections page.", ex);
                        }

                        throw;
                    }

                    var update = updates.Current;
                    foreach (var usage in update.Contents)
                    {
                        if (usage is UsageContent content)
                        {
                            input += content.Details.InputTokenCount ?? 0;
                            output += content.Details.OutputTokenCount ?? 0;
                        }
                    }

                    yield return update;
                }

                recorded = true;
                await RecordAsync(ModelOf(options), input, output, timer.ElapsedMilliseconds, AiCallStatus.Success);
            }
            finally
            {
                if (!recorded)
                {
                    await RecordAsync(ModelOf(options), input, output, timer.ElapsedMilliseconds, AiCallStatus.Success);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _owned?.Dispose();
            }
        }

        private async Task EnsureWithinQuotaAsync(CancellationToken cancellationToken)
        {
            foreach (var metric in GatedMetrics)
            {
                QuotaDecision decision;
                try
                {
                    decision = await _quotas.EvaluateAsync(metric, 1, cancellationToken);
                }
                catch (MeteringUnavailableException ex)
                {
                    throw new AssistantUnavailableException(AssistantUnavailableReason.MeteringUnavailable, "The assistant cannot check the site's AI quota right now, so it makes no call.", ex);
                }

                if (decision.Reason == QuotaDecisionReason.Unavailable)
                {
                    throw new AssistantUnavailableException(AssistantUnavailableReason.MeteringUnavailable, "The assistant cannot check the site's AI quota right now, so it makes no call.");
                }

                if (!decision.Allowed)
                {
                    throw new AssistantUnavailableException(AssistantUnavailableReason.QuotaExceeded, $"The site's AI quota ({metric}) is spent: {decision.Consumed} of {decision.Limit}.");
                }
            }
        }

        /// <summary>
        /// A response the cache answered: no call reached the model, so it is recorded here as
        /// <see cref="AiCallStatus.Cached"/> with the original counts - D102 bills a cached call at nothing.
        /// </summary>
        public Task RecordCachedAsync(Stomme.CostOptimization.CachedModelResponse response, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            cancellationToken.ThrowIfCancellationRequested();
            return RecordAsync(string.IsNullOrWhiteSpace(response.Model) ? _model : response.Model, response.InputTokens, response.OutputTokens, 0, AiCallStatus.Cached);
        }

        /// <summary>The model a call was MADE with - a tier's (CostOptimization) when the agent chose one, else the site's.</summary>
        private string ModelOf(ChatOptions? options) => string.IsNullOrWhiteSpace(options?.ModelId) ? _model : options.ModelId;

        private async Task RecordAsync(string model, long inputTokens, long outputTokens, long latencyMs, AiCallStatus status)
        {
            try
            {
                await _recorder.RecordAsync(new AiUsageEvent(_provider, model, _agent, inputTokens, outputTokens, latencyMs, status, Guid.NewGuid()), CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The call happened; a lost ledger row is a wrong bill, so it is logged at Error rather than dropped quietly.
                _logger.LogError(ex, "An assistant model call (provider {Provider}, model {Model}, agent {Agent}) could not be recorded in AI usage.", _provider, model, _agent);
            }
        }
    }
}
