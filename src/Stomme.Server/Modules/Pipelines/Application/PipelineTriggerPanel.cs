#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// The editor's trigger panel (BL-229 increment 10): for a webhook, the address under the site, the header a call is signed
    /// in, and whether the site receives webhooks and has a secret to verify them with (never the secret); for a schedule, its
    /// next runs or why its cron is not one. Read off the same rules the receiver and the schedules task apply.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record GetTriggerInfoQuery(string Type, string? Value, string? TimeZone = null) : IQuery<TriggerInfoView>;

    public sealed class GetTriggerInfoQueryHandler(IOptions<PipelineTriggerOptions> options, TimeProvider clock, PipelineWebhookSecrets secrets, SiteState siteState)
        : IQueryHandler<GetTriggerInfoQuery, TriggerInfoView>
    {
        public const int NextRuns = 5;

        public async Task<TriggerInfoView> Handle(GetTriggerInfoQuery request, CancellationToken ct)
        {
            var settings = options.Value;
            if (string.Equals(request.Type, WebhookType, StringComparison.Ordinal))
            {
                var valid = Webhook.IsValidName(request.Value);
                var name = valid ? Webhook.Normalize(request.Value!) : null;
                // The secret its calls are verified under, as the receiver chooses it (D111a): its own, else the installation's
                // - a SecretFile counts, a missing one does not. Whether there is one, never what it is.
                var (secret, _, own) = name is null ? (null, null, false) : await secrets.ForCallAsync(siteState.Alias?.SiteId, name, settings.Webhooks, ct);
                return new TriggerInfoView
                {
                    Kind = "Webhook",
                    Enabled = settings.Webhooks.Enabled,
                    Error = valid ? null : $"'{request.Value}' is not a webhook name (letters, digits, '-', '_', '.', at most 100 characters).",
                    Path = name is null ? null : "api/Pipelines/webhooks/" + name,
                    SignatureHeader = PipelineWebhooks.SignatureHeaderName,
                    Signed = secret is not null,
                    OwnSecret = own && secret is not null,
                    OwnSecretUnreadable = own && secret is null,
                    SecretFromFile = !string.IsNullOrWhiteSpace(settings.Webhooks.SecretFile),
                    Allowlisted = name is not null && settings.Webhooks.For(name).AllowedCallers.Count > 0,
                };
            }

            // BL-229, the Chat trigger: where its page is, and whether the site serves chats.
            if (string.Equals(request.Type, Steps.ChatTrigger.StepTypeName, StringComparison.Ordinal))
            {
                var valid = Webhook.IsValidName(request.Value);
                return new TriggerInfoView
                {
                    Kind = "Chat",
                    Enabled = settings.Chats.Enabled,
                    Error = valid ? null : $"'{request.Value}' is not a chat name (letters, digits, '-', '_', '.', at most 100 characters).",
                    Path = valid ? "api/Pipelines/chats/" + Webhook.Normalize(request.Value!) : null,
                };
            }

            // BL-229, the Form trigger: where its page is, and whether the site serves forms.
            if (string.Equals(request.Type, PipelineForms.StepType, StringComparison.Ordinal))
            {
                var valid = Webhook.IsValidName(request.Value);
                return new TriggerInfoView
                {
                    Kind = "Form",
                    Enabled = settings.Forms.Enabled,
                    Error = valid ? null : $"'{request.Value}' is not a form name (letters, digits, '-', '_', '.', at most 100 characters).",
                    Path = valid ? "api/Pipelines/forms/" + Webhook.Normalize(request.Value!) : null,
                };
            }

            if (string.Equals(request.Type, ScheduleType, StringComparison.Ordinal))
            {
                var info = new TriggerInfoView { Kind = "Schedule", Enabled = settings.Schedules.Enabled, CheckedEverySeconds = Math.Max(5, settings.Schedules.IntervalSeconds) };
                var crons = Schedule.TryParseAll(request.Value, out var error);
                if (crons is null)
                {
                    info.Error = error;
                    return info;
                }

                // BL-229: in the schedule's time zone, as the schedules task reads it - daylight saving followed.
                var zone = Schedule.FindZone(request.TimeZone, out var zoneError);
                if (zone is null)
                {
                    info.Error = zoneError;
                    return info;
                }

                info.TimeZone = Schedule.NormalizeZone(request.TimeZone);
                // BL-229: every cron line's next runs, merged in order (n8n's trigger rules).
                foreach (var next in Schedule.Next(crons, clock.GetUtcNow().UtcDateTime, zone, NextRuns))
                {
                    info.NextRunsUtc.Add(next);
                    if (info.TimeZone is not null)
                    {
                        info.NextRunsInZone.Add(TimeZoneInfo.ConvertTimeFromUtc(next, zone));
                    }
                }

                return info;
            }

            return new TriggerInfoView { Kind = "" };
        }

        public const string WebhookType = "Stomme.Webhook";

        public const string ScheduleType = "Stomme.Schedule";
    }

    /// <summary>
    /// Waits for the next call to one of the site's webhooks (BL-229 increment 10) - what it sent, or that none came within the
    /// wait. Authoring rights: what a call carries is the author's to see, as the runs it starts are.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ListenForWebhookCommand(string Name, int Seconds) : ICommand<WebhookCallView>;

    public sealed class ListenForWebhookCommandHandler(PipelineWebhookListeners listeners, IOptions<PipelineTriggerOptions> options, SiteState siteState)
        : ICommandHandler<ListenForWebhookCommand, WebhookCallView>
    {
        public async Task<WebhookCallView> Handle(ListenForWebhookCommand request, CancellationToken ct)
        {
            if (!options.Value.Webhooks.Enabled)
            {
                return new WebhookCallView { Error = "The site does not receive webhooks (Pipelines:Webhooks:Enabled is off)." };
            }

            if (!Webhook.IsValidName(request.Name))
            {
                return new WebhookCallView { Error = $"'{request.Name}' is not a webhook name." };
            }

            var alias = siteState.Alias ?? throw new InvalidOperationException("Listening needs the request's site.");
            var key = PipelineWebhookListeners.Key(alias.TenantId, alias.SiteId, Webhook.Normalize(request.Name));
            return await listeners.ListenAsync(key, TimeSpan.FromSeconds(Math.Clamp(request.Seconds, 5, (int)PipelineWebhookListeners.MaxWait.TotalSeconds)), ct);
        }
    }
}
