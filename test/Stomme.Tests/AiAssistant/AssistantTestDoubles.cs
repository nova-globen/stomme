#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Metering;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.AI;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>A model that answers from a script and counts its calls - no test reaches a live model (D113).</summary>
    internal sealed class ScriptedChatClient : IChatClient
    {
        public string Reply { get; set; } = "scripted reply";

        public long InputTokens { get; set; } = 120;

        public long OutputTokens { get; set; } = 45;

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public bool Disposed { get; private set; }

        public List<IReadOnlyList<ChatMessage>> Received { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Received.Add(new List<ChatMessage>(messages));
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply))
            {
                Usage = new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = OutputTokens },
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            if (Throw is not null)
            {
                throw Throw; // BL-251: a stream that fails at its first step, as a refused or failed send does
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, Reply);
            yield return new ChatResponseUpdate { Contents = { new UsageContent(new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = OutputTokens }) } };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() => Disposed = true;
    }

    internal sealed class RecordingAiUsage : IAiUsageRecorder
    {
        public List<AiUsageEvent> Events { get; } = new();

        public Exception? Throw { get; set; }

        public Task<AiUsageOutcome> RecordAsync(AiUsageEvent usage, CancellationToken ct = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Events.Add(usage);
            return Task.FromResult(new AiUsageOutcome(true, true, QuotaDecisionReason.Within, false, 0m, "USD"));
        }
    }

    internal sealed class ScriptedQuotas : IQuotaInspector
    {
        public Dictionary<string, QuotaDecision> Decisions { get; } = new(StringComparer.Ordinal);

        public Exception? Throw { get; set; }

        public List<string> Asked { get; } = new();

        public Task<QuotaDecision> EvaluateAsync(string metric, long quantity, CancellationToken ct = default)
        {
            Asked.Add(metric);
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Decisions.TryGetValue(metric, out var decision) ? decision : new QuotaDecision(true, QuotaDecisionReason.Undefined, 0, 0, 0));
        }
    }

    internal sealed class FixedCaller(int siteId, int userId) : IAssistantCaller
    {
        public Task<(int SiteId, TenantId? TenantId, int UserId)> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((siteId, (TenantId?)TenantId.Default, userId));
    }

    internal sealed class FixedSiteSettings(AssistantSiteConfiguration configuration) : IAssistantSiteSettings
    {
        public AssistantSiteConfiguration Configuration { get; set; } = configuration;

        public Task<AssistantSiteConfiguration> ReadAsync(int siteId, CancellationToken cancellationToken = default) => Task.FromResult(Configuration);
    }

    internal sealed class FixedSecrets(SiteSecretResult result) : ISiteSecretStore
    {
        public List<(int SiteId, string Key)> Read { get; } = new();

        public Task SetAsync(int siteId, string referenceKey, string secret, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SiteSecretResult> GetAsync(int siteId, string referenceKey, CancellationToken ct = default)
        {
            Read.Add((siteId, referenceKey));
            return Task.FromResult(result);
        }

        public Task DeleteAsync(int siteId, string referenceKey, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The real egress over the guard with a fixed allowlist - what the host hands a site.</summary>
    internal sealed class FixedEgress(params string[] allowedHosts) : IConnectorEgressFactory
    {
        public List<int> Sites { get; } = new();

        public Task<IConnectorEgress> ForSiteAsync(int siteId, CancellationToken cancellationToken = default)
        {
            Sites.Add(siteId);
            return Task.FromResult<IConnectorEgress>(new HostConnectorEgress(allowedHosts));
        }
    }

    /// <summary>A provider that hands out one scripted client and remembers what it was asked for.</summary>
    internal sealed class ScriptedProvider(string name, IChatClient client) : ILlmProvider
    {
        public string Name { get; } = name;

        public List<(string Endpoint, string Model, string? ApiKey)> Created { get; } = new();

        public IChatClient CreateChatClient(string endpoint, string model, string? apiKey, HttpClient transport)
        {
            Created.Add((endpoint, model, apiKey));
            return client;
        }
    }
}
