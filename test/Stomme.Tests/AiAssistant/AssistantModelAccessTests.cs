#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Metering;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// Opening a site's model, with the predecessor platform's <c>AssistantOptionsAndResolverTests</c> by name where
    /// they carry over. The installation switch and the SITE's switch both have to be on or nothing reaches a provider; a
    /// site names its provider case-insensitively; an unknown provider, a missing model and an unreadable key are refused
    /// with the setting named; the key, when stored, reaches the provider and nothing else; the client handed out is the
    /// metered one. <b>CHANGED:</b> the predecessor platform bound one provider for the whole deployment from <c>the predecessor platform:AI</c>; here the
    /// installation switch is <c>AiAssistant</c> and the provider, endpoint, model and key are PER SITE. the predecessor platform's
    /// default of a Claude model is gone - no model is assumed.
    /// </summary>
    public sealed class AssistantModelAccessTests
    {
        private const int Site = 5;

        private static AssistantSiteConfiguration Configured(string provider = "openai", string? model = "gpt-test", bool enabled = true) =>
            new(enabled, provider, "https://models.example.test/v1", model, 0.2f);

        private static (AssistantModelAccess Access, ScriptedProvider Provider, ScriptedChatClient Client, RecordingAiUsage Usage, FixedSecrets Secrets) Build(
            bool installation = true, AssistantSiteConfiguration? site = null, SiteSecretResult? key = null, string providerName = "openai")
        {
            var client = new ScriptedChatClient();
            var provider = new ScriptedProvider(providerName, client);
            var usage = new RecordingAiUsage();
            var secrets = new FixedSecrets(key ?? SiteSecretResult.NotFound);
            var access = new AssistantModelAccess(
                new AssistantOptions { Enabled = installation },
                new FixedCaller(Site, 7),
                new FixedSiteSettings(site ?? Configured()),
                secrets,
                new FixedEgress(),
                new ILlmProvider[] { provider },
                new ScriptedQuotas(),
                usage,
                NullLogger<AssistantModelAccess>.Instance);
            return (access, provider, client, usage, secrets);
        }

        [Fact]
        public void Options_bind_from_the_AiAssistant_section_and_are_off_by_default()
        {
            var defaults = new AssistantOptions();
            Assert.False(defaults.Enabled);
            Assert.Equal(30, defaults.RateLimitPerMinute);
            Assert.Equal(120, defaults.RequestTimeoutSeconds);

            var bound = new AssistantOptions();
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiAssistant:Enabled"] = "true",
                ["AiAssistant:RateLimitPerMinute"] = "75",
                ["AiAssistant:RequestTimeoutSeconds"] = "45",
            }).Build().GetSection(AssistantOptions.SectionName).Bind(bound);
            Assert.Equal((true, 75, 45), (bound.Enabled, bound.RateLimitPerMinute, bound.RequestTimeoutSeconds));
        }

        [Fact]
        public async Task Resolver_short_circuits_when_the_feature_flag_is_off()
        {
            // A provider IS registered and the site is configured, but the installation switch is off: nothing may open it.
            var (access, provider, _, _, _) = Build(installation: false);

            Assert.False(await access.IsAvailableAsync());
            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => access.OpenAsync("assistant"));
            Assert.Equal(AssistantUnavailableReason.Disabled, refused.Reason);
            Assert.Contains("AiAssistant:Enabled", refused.Message, StringComparison.Ordinal);
            Assert.Empty(provider.Created);
        }

        [Fact]
        public async Task The_site_switch_is_a_second_gate()
        {
            var (access, provider, _, _, _) = Build(site: Configured(enabled: false));

            Assert.False(await access.IsAvailableAsync());
            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => access.OpenAsync("assistant"));
            Assert.Equal(AssistantUnavailableReason.Disabled, refused.Reason);
            Assert.Contains(AssistantSiteConfiguration.EnabledSetting, refused.Message, StringComparison.Ordinal);
            Assert.Empty(provider.Created);
        }

        [Fact]
        public async Task Resolver_selects_the_configured_provider_case_insensitively()
        {
            var (access, provider, _, _, _) = Build(site: Configured(provider: "OpenAI"));

            Assert.True(await access.IsAvailableAsync());
            using var model = await access.OpenAsync("assistant");

            Assert.Equal(("openai", "gpt-test", (float?)0.2f), (model.Provider, model.Model, model.Temperature));
            Assert.Equal(("https://models.example.test/v1", "gpt-test"), (provider.Created.Single().Endpoint, provider.Created.Single().Model));
            Assert.IsType<MeteredChatClient>(model.Client);
        }

        [Fact]
        public async Task Resolver_throws_when_no_registered_provider_matches_configuration()
        {
            var (access, provider, _, _, _) = Build(site: Configured(provider: "anthropic"));
            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => access.OpenAsync("assistant"));
            Assert.Equal(AssistantUnavailableReason.UnknownProvider, refused.Reason);
            Assert.Contains(AssistantSiteConfiguration.ProviderSetting, refused.Message, StringComparison.Ordinal);
            Assert.Empty(provider.Created);
        }

        [Fact]
        public async Task No_model_is_assumed()
        {
            var (access, _, _, _, _) = Build(site: Configured(model: null));
            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => access.OpenAsync("assistant"));
            Assert.Equal(AssistantUnavailableReason.NotConfigured, refused.Reason);
        }

        [Fact]
        public async Task The_sites_key_reaches_the_provider_and_an_unreadable_one_is_refused()
        {
            var (withKey, provider, _, _, secrets) = Build(key: SiteSecretResult.Found("sk-test-value"));
            using (await withKey.OpenAsync("assistant"))
            {
            }

            Assert.Equal("sk-test-value", provider.Created.Single().ApiKey);
            Assert.Equal((Site, AssistantSiteConfiguration.ApiKeyReference), secrets.Read.Single());

            var (noKey, noKeyProvider, _, _, _) = Build(key: SiteSecretResult.NotFound);
            using (await noKey.OpenAsync("assistant"))
            {
            }

            Assert.Null(noKeyProvider.Created.Single().ApiKey);   // a local server needs none

            var (unreadable, unreadableProvider, _, _, _) = Build(key: SiteSecretResult.Unreadable);
            var refused = await Assert.ThrowsAsync<AssistantUnavailableException>(() => unreadable.OpenAsync("assistant"));
            Assert.Equal(AssistantUnavailableReason.KeyUnreadable, refused.Reason);
            Assert.DoesNotContain("sk-", refused.Message, StringComparison.Ordinal);
            Assert.Empty(unreadableProvider.Created);
        }

        [Fact]
        public async Task A_call_through_the_opened_model_is_metered_for_the_agent()
        {
            var (access, _, client, usage, _) = Build();
            using var model = await access.OpenAsync("pipeline-architect");

            var response = await model.Client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") });

            Assert.Equal("scripted reply", response.Text);
            Assert.Equal(1, client.Calls);
            var recorded = Assert.Single(usage.Events);
            Assert.Equal(("openai", "gpt-test", "pipeline-architect", 120L, 45L, AiCallStatus.Success), (recorded.Provider, recorded.Model, recorded.Agent, recorded.InputTokens, recorded.OutputTokens, recorded.Status));
        }
    }
}
