#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using FluentValidation;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The site's assistant configuration (BL-97 increment 10b, D113: configured PER SITE) under <c>assistant:configure</c>:
    /// the switch, the provider, the endpoint, the model and temperature as Site settings, and whether an API key is stored -
    /// never the key itself.
    /// </summary>
    [Requires(AssistantPermissions.Configure)]
    public sealed record GetAssistantSettingsQuery : IQuery<AssistantSettingsView>;

    public sealed class GetAssistantSettingsQueryHandler(AssistantOptions options, IAssistantCaller caller, IAssistantSiteSettings settings, ISiteSecretStore secrets, IEnumerable<ILlmProvider> providers)
        : IQueryHandler<GetAssistantSettingsQuery, AssistantSettingsView>
    {
        public async Task<AssistantSettingsView> Handle(GetAssistantSettingsQuery request, CancellationToken ct) =>
            await AssistantSettingsViews.ReadAsync(options, await AssistantSettingsViews.RequireSiteAsync(caller, ct), settings, secrets, providers, ct);
    }

    /// <summary>
    /// Saves the site's assistant configuration. A non-empty key REPLACES the stored one in the per-site secret store (never a
    /// row, never read back); an empty one keeps it; <c>ClearApiKey</c> removes it. The endpoint is where the site's egress will
    /// dial - a LAN or loopback server is reachable only when the site allows its host (<c>ConnectivityAllowedHosts</c>).
    /// </summary>
    [Requires(AssistantPermissions.Configure)]
    public sealed record SaveAssistantSettingsCommand(bool Enabled, string Provider, string Endpoint, string? Model, float? Temperature, string? ApiKey, bool ClearApiKey) : ICommand<AssistantSettingsView>;

    public sealed class SaveAssistantSettingsCommandValidator : AbstractValidator<SaveAssistantSettingsCommand>
    {
        public SaveAssistantSettingsCommandValidator(IEnumerable<ILlmProvider> providers)
        {
            var names = providers.Select(provider => provider.Name).ToList();
            RuleFor(command => command.Provider).NotEmpty()
                .Must(provider => names.Contains(provider, StringComparer.OrdinalIgnoreCase))
                .WithMessage(command => $"'{command.Provider}' is not a provider this installation offers ({string.Join(", ", names)}).");
            RuleFor(command => command.Endpoint).NotEmpty().MaximumLength(500)
                .Must(endpoint => Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(uri.UserInfo))
                .WithMessage("The endpoint is an absolute http or https address, with no credentials in it.");
            RuleFor(command => command.Model).MaximumLength(200);
            RuleFor(command => command.Model).NotEmpty().When(command => command.Enabled).WithMessage("Name the model before switching the assistant on.");
            RuleFor(command => command.Temperature).InclusiveBetween(0f, 2f).When(command => command.Temperature is not null);
            RuleFor(command => command.ApiKey).MaximumLength(4096);
        }
    }

    public sealed class SaveAssistantSettingsCommandHandler(
        AssistantOptions options, IAssistantCaller caller, ISettingRepository settingRepository, IAssistantSiteSettings settings, ISiteSecretStore secrets, IEnumerable<ILlmProvider> providers)
        : ICommandHandler<SaveAssistantSettingsCommand, AssistantSettingsView>
    {
        public async Task<AssistantSettingsView> Handle(SaveAssistantSettingsCommand request, CancellationToken ct)
        {
            var siteId = await AssistantSettingsViews.RequireSiteAsync(caller, ct);
            await UpsertAsync(siteId, AssistantSiteConfiguration.EnabledSetting, request.Enabled ? "true" : "false");
            await UpsertAsync(siteId, AssistantSiteConfiguration.ProviderSetting, request.Provider.Trim().ToLowerInvariant());
            await UpsertAsync(siteId, AssistantSiteConfiguration.EndpointSetting, request.Endpoint.Trim());
            await UpsertAsync(siteId, AssistantSiteConfiguration.ModelSetting, request.Model?.Trim() ?? string.Empty);
            await UpsertAsync(siteId, AssistantSiteConfiguration.TemperatureSetting, request.Temperature?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);

            if (request.ClearApiKey)
            {
                await secrets.DeleteAsync(siteId, AssistantSiteConfiguration.ApiKeyReference, ct);
            }
            else if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                await secrets.SetAsync(siteId, AssistantSiteConfiguration.ApiKeyReference, request.ApiKey.Trim(), ct);
            }

            return await AssistantSettingsViews.ReadAsync(options, siteId, settings, secrets, providers, ct);
        }

        private async Task UpsertAsync(int siteId, string name, string value)
        {
            var existing = await settingRepository.GetSettingAsync(EntityNames.Site, siteId, name);
            if (existing is null)
            {
                await settingRepository.AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = siteId, SettingName = name, SettingValue = value, IsPrivate = true });
            }
            else if (existing.SettingValue != value)
            {
                existing.SettingValue = value;
                existing.IsPrivate = true;
                await settingRepository.UpdateSettingAsync(existing);
            }
        }
    }

    internal static class AssistantSettingsViews
    {
        public static async Task<int> RequireSiteAsync(IAssistantCaller caller, CancellationToken ct)
        {
            var (siteId, _, _) = await caller.ResolveAsync(ct);
            return siteId > 0 ? siteId : throw new AssistantConflictException("The assistant is configured per site; no site is resolved.");
        }

        public static async Task<AssistantSettingsView> ReadAsync(AssistantOptions options, int siteId, IAssistantSiteSettings settings, ISiteSecretStore secrets, IEnumerable<ILlmProvider> providers, CancellationToken ct)
        {
            var site = await settings.ReadAsync(siteId, ct);
            var key = await secrets.GetAsync(siteId, AssistantSiteConfiguration.ApiKeyReference, ct);
            return new AssistantSettingsView(
                options.Enabled,
                site.Enabled,
                site.Provider,
                site.Endpoint,
                site.Model,
                site.Temperature,
                key.Status switch { SiteSecretStatus.Found => "Stored", SiteSecretStatus.NotFound => "None", _ => "Unreadable" },
                providers.Select(provider => provider.Name).OrderBy(name => name, StringComparer.Ordinal).ToList());
        }
    }
}
