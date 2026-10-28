#nullable enable
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Metering;
using Stomme.Repository;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// A language-model backend adapted to <see cref="IChatClient"/> (D113). A provider is configuration, not code, for every
    /// backend speaking the OpenAI-compatible API; this port exists so a second protocol could be added without touching an
    /// agent. It is given the site's egress client - it never builds its own transport.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>Matched case-insensitively against the site's <c>AiAssistantProvider</c> setting.</summary>
        string Name { get; }

        IChatClient CreateChatClient(string endpoint, string model, string? apiKey, HttpClient transport);
    }

    /// <summary>
    /// THE shipped provider (D113): the OpenAI-compatible chat API through <c>Microsoft.Extensions.AI.OpenAI</c> over the
    /// official OpenAI SDK (both MIT) - OpenAI, Azure OpenAI's compatible endpoint, and local servers speaking the same API
    /// (Ollama, LM Studio). Every request goes through the HTTP client it is handed, which is the site's egress (the one
    /// outbound guard); a local server is reachable only when the site allowlists its host.
    /// </summary>
    public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
    {
        public const string ProviderName = "openai";

        public string Name => ProviderName;

        public IChatClient CreateChatClient(string endpoint, string model, string? apiKey, HttpClient transport)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            ArgumentNullException.ThrowIfNull(transport);
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint.Trim().TrimEnd('/') + "/"),
                Transport = new HttpClientPipelineTransport(transport),
            };

            // A local server needs no key but the SDK requires one; it is sent as a Bearer the server ignores.
            var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "no-key" : apiKey);
            return new OpenAI.Chat.ChatClient(model.Trim(), credential, options).AsIChatClient();
        }
    }

    /// <summary>A model ready to call for one agent on one site: the client (metered and quota-gated) and what it is.</summary>
    public sealed record AssistantModel(IChatClient Client, string Provider, string Model, float? Temperature) : IDisposable
    {
        /// <summary>Releases the client and the site egress connection it owns.</summary>
        public void Dispose() => Client.Dispose();

        /// <summary>Records a response the cache answered - no model call was made, so the metered client never saw it.</summary>
        public Task RecordCachedAsync(Stomme.CostOptimization.CachedModelResponse response, CancellationToken cancellationToken = default) =>
            Client.GetService<MeteredChatClient>() is { } meter
                ? meter.RecordCachedAsync(response, cancellationToken)
                : throw new InvalidOperationException("The assistant's model client is not metered.");
    }

    /// <summary>
    /// Opens the CURRENT site's model for an agent (D113), or says why it cannot: the installation switch, the site's own
    /// switch, a model named, a readable key when one is stored, a known provider - and every call through the returned client
    /// is quota-gated before it is made and metered after it (<see cref="MeteredChatClient"/>, D102).
    /// </summary>
    public interface IAssistantModelAccess
    {
        /// <summary>Whether the assistant is on for the installation and this site - cheap, no model call.</summary>
        Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

        /// <exception cref="AssistantUnavailableException">The model cannot be used here; the reason says why.</exception>
        Task<AssistantModel> OpenAsync(string agent, CancellationToken cancellationToken = default);
    }

    public sealed class AssistantModelAccess(
        AssistantOptions options,
        IAssistantCaller caller,
        IAssistantSiteSettings settings,
        ISiteSecretStore secrets,
        IConnectorEgressFactory egress,
        IEnumerable<ILlmProvider> providers,
        IQuotaInspector quotas,
        IAiUsageRecorder recorder,
        ILogger<AssistantModelAccess> logger) : IAssistantModelAccess
    {
        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            if (!options.Enabled)
            {
                return false;
            }

            var (siteId, _, _) = await caller.ResolveAsync(cancellationToken);
            return siteId > 0 && (await settings.ReadAsync(siteId, cancellationToken)).Enabled;
        }

        public async Task<AssistantModel> OpenAsync(string agent, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agent);
            if (!options.Enabled)
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.Disabled, "The assistant is switched off for this installation (AiAssistant:Enabled).");
            }

            var (siteId, _, _) = await caller.ResolveAsync(cancellationToken);
            if (siteId <= 0)
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.Disabled, "The assistant needs a site.");
            }

            var site = await settings.ReadAsync(siteId, cancellationToken);
            if (!site.Enabled)
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.Disabled, $"The assistant is switched off for this site ({AssistantSiteConfiguration.EnabledSetting}).");
            }

            if (site.Model is null)
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.NotConfigured, $"No model is configured for this site ({AssistantSiteConfiguration.ModelSetting}).");
            }

            var provider = providers.FirstOrDefault(candidate => string.Equals(candidate.Name, site.Provider, StringComparison.OrdinalIgnoreCase))
                ?? throw new AssistantUnavailableException(AssistantUnavailableReason.UnknownProvider, $"No provider named '{site.Provider}' is available ({AssistantSiteConfiguration.ProviderSetting}).");

            var key = await secrets.GetAsync(siteId, AssistantSiteConfiguration.ApiKeyReference, cancellationToken);
            if (key.Status == SiteSecretStatus.Unreadable)
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.KeyUnreadable, "The site's assistant API key cannot be read; enter it again.");
            }

            var transport = (await egress.ForSiteAsync(siteId, cancellationToken)).CreateHttpClient(TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds)));
            var client = provider.CreateChatClient(site.Endpoint, site.Model, key.IsFound ? key.Secret : null, transport);
            return new AssistantModel(new MeteredChatClient(client, provider.Name, site.Model, agent, quotas, recorder, logger, transport), provider.Name, site.Model, site.Temperature);
        }
    }
}
