#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// <c>AiAssistant</c> - the INSTALLATION switch (off by default) and the installation-wide limits. Read from this
    /// host's own configuration, the override file included. With it off nothing reaches a model, whatever a site
    /// says.
    /// </summary>
    public sealed class AssistantOptions
    {
        public const string SectionName = "AiAssistant";

        public bool Enabled { get; set; }

        /// <summary>Requests one user may make per minute - the guardrail's first check. At least 1.</summary>
        public int RateLimitPerMinute { get; set; } = 30;

        /// <summary>How long one model call may take.</summary>
        public int RequestTimeoutSeconds { get; set; } = 120;
    }

    /// <summary>
    /// A site's assistant configuration (configured PER SITE): whether the site uses it, which provider, the endpoint,
    /// the model and the sampling temperature, as Site settings; the API key in the per-site secret store under
    /// <see cref="ApiKeyReference"/>, never a row or a page.
    /// </summary>
    public sealed record AssistantSiteConfiguration(bool Enabled, string Provider, string Endpoint, string? Model, float? Temperature)
    {
        public const string EnabledSetting = "AiAssistantEnabled";
        public const string ProviderSetting = "AiAssistantProvider";
        public const string EndpointSetting = "AiAssistantEndpoint";
        public const string ModelSetting = "AiAssistantModel";
        public const string TemperatureSetting = "AiAssistantTemperature";

        /// <summary>The per-site secret store's reference key for the provider's API key.</summary>
        public const string ApiKeyReference = "ai-assistant.provider.api-key";

        public const string DefaultProvider = OpenAiCompatibleLlmProvider.ProviderName;
        public const string DefaultEndpoint = "https://api.openai.com/v1";

        public static async Task<AssistantSiteConfiguration> ReadAsync(ISettingRepository settings, int siteId)
        {
            async Task<string?> Setting(string name) => (await settings.GetSettingAsync(EntityNames.Site, siteId, name))?.SettingValue?.Trim();

            var enabled = bool.TryParse(await Setting(EnabledSetting), out var on) && on;
            var provider = await Setting(ProviderSetting);
            var endpoint = await Setting(EndpointSetting);
            var model = await Setting(ModelSetting);
            float? temperature = float.TryParse(await Setting(TemperatureSetting), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            return new AssistantSiteConfiguration(
                enabled,
                string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider,
                string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint,
                string.IsNullOrWhiteSpace(model) ? null : model,
                temperature);
        }
    }

    /// <summary>Reads a site's assistant configuration.</summary>
    public interface IAssistantSiteSettings
    {
        Task<AssistantSiteConfiguration> ReadAsync(int siteId, CancellationToken cancellationToken = default);
    }

    /// <summary>The Site-setting-backed <see cref="IAssistantSiteSettings"/>.</summary>
    public sealed class SettingAssistantSiteSettings(ISettingRepository settings) : IAssistantSiteSettings
    {
        public Task<AssistantSiteConfiguration> ReadAsync(int siteId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AssistantSiteConfiguration.ReadAsync(settings, siteId);
        }
    }

    /// <summary>WHO and WHERE for an assistant call, server-side.</summary>
    public interface IAssistantCaller
    {
        Task<(int SiteId, TenantId? TenantId, int UserId)> ResolveAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>The request's user and site (the site from the resolved alias outside a request); never a value a caller supplies.</summary>
    public sealed class AssistantCaller(ICurrentUser currentUser, ITenantContext tenantContext, ITenantManager tenantManager) : IAssistantCaller
    {
        public async Task<(int SiteId, TenantId? TenantId, int UserId)> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var siteId = currentUser.SiteId;
            if (siteId <= 0)
            {
                siteId = (await tenantManager.GetAliasAsync())?.SiteId ?? -1;
            }

            return (siteId, tenantContext.CurrentTenantId, currentUser.UserId ?? -1);
        }
    }
}
