#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// D111a: a new secret of a webhook's own on the request's site, replacing any it had - its calls are verified under it from
    /// now on, the installation's no longer opens it. Given out in the answer this once. Authoring rights, as the trigger is
    /// the author's.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record RenewWebhookSecretCommand(string Name) : ICommand<PipelineWebhookSecretView>;

    public sealed class RenewWebhookSecretCommandHandler(PipelineWebhookSecrets secrets, SiteState siteState) : ICommandHandler<RenewWebhookSecretCommand, PipelineWebhookSecretView>
    {
        public async Task<PipelineWebhookSecretView> Handle(RenewWebhookSecretCommand request, CancellationToken ct)
        {
            var name = WebhookSecretMessages.RequireName(request.Name);
            var site = WebhookSecretMessages.RequireSite(siteState);
            return new PipelineWebhookSecretView { Name = name, Secret = await secrets.RenewAsync(site, name, ct) };
        }
    }

    /// <summary>D111a: removes a webhook's own secret on the request's site, so it takes the installation's again; false when it had none.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record RemoveWebhookSecretCommand(string Name) : ICommand<bool>;

    public sealed class RemoveWebhookSecretCommandHandler(PipelineWebhookSecrets secrets, SiteState siteState) : ICommandHandler<RemoveWebhookSecretCommand, bool>
    {
        public Task<bool> Handle(RemoveWebhookSecretCommand request, CancellationToken ct) =>
            secrets.RemoveAsync(WebhookSecretMessages.RequireSite(siteState), WebhookSecretMessages.RequireName(request.Name), ct);
    }

    internal static class WebhookSecretMessages
    {
        public static string RequireName(string? name) =>
            Webhook.IsValidName(name)
                ? Webhook.Normalize(name!)
                : throw new ArgumentException($"'{name}' is not a webhook name (letters, digits, '-', '_', '.', at most 100 characters).", nameof(name));

        public static int RequireSite(SiteState siteState) =>
            siteState.Alias?.SiteId ?? throw new InvalidOperationException("A webhook's secret needs the request's site.");
    }
}
