#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Notifications;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Notifications;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// A notification as an engine step over Stomme's Notifications module: a template key,
    /// recipients (<c>user:&lt;id&gt;</c>, <c>email:&lt;address&gt;</c>, <c>endpoint:&lt;url&gt;</c>), channels,
    /// a severity, template parameters and a topic; the created request's id is the result. The dispatcher needs
    /// Stomme's site context, which a run on the engine's worker does not have, so the step enters the site of its
    /// engine tenant first (<see cref="StommeSiteScope"/>). The dispatcher's refusals (no recipients, an unknown
    /// channel, a site that cannot be entered) are failures under the step's policy.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Notifications", "Queue a notification through the site's Notifications module.", DisplayName = "Send notification")]
    public class SendNotification : DataStep
    {
        [Input(DisplayName = "Template key", Description = "The notification template to render.", Order = 1)]
        public Input<string> TemplateKey { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: user:<id> | email:<address> | endpoint:<url>.", Order = 2)]
        public Input<string?>? Recipients { get; set; }

        [Input(Description = "Comma-separated: Email, InApp, Webhook, Sms.", DefaultValue = "InApp", Order = 3)]
        public Input<string> Channels { get; set; } = new(new Literal<string>("InApp"));

        [Input(DefaultValue = NotificationSeverity.Information, Order = 4)]
        public Input<NotificationSeverity> Severity { get; set; } = new(new Literal<NotificationSeverity>(NotificationSeverity.Information));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Template parameters, one 'name = value' per line. Empty values may come from the previous step's result as 'result'.", Order = 5)]
        public Input<string?>? Parameters { get; set; }

        [Input(Description = "The topic recipients can subscribe to or mute.", Order = 6)]
        public Input<string?>? Topic { get; set; }

        [Output(DisplayName = "Request id")]
        public Output<string>? RequestId { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var templateKey = Required(context, TemplateKey, "TemplateKey");
            var recipients = new List<DispatchRecipient>();
            // Each recipient's value rendered on its own (TemplatedLines): an address from the run is one recipient, never more.
            foreach (var recipient in await TemplatedLines.ReadAsync(Recipients, context, ':').ConfigureAwait(false))
            {
                var line = recipient.Line;
                var kind = recipient.Name?.ToLowerInvariant() ?? "";
                var value = recipient.Name is null ? "" : recipient.Value;
                NotificationRecipientKind? recipientKind = kind switch
                {
                    "user" => NotificationRecipientKind.User,
                    "email" => NotificationRecipientKind.Email,
                    "endpoint" => NotificationRecipientKind.Endpoint,
                    _ => null,
                };
                if (recipientKind is null || value.Length == 0)
                {
                    await FailAsync(context, "RECIPIENT_INVALID", $"'{line}' is not 'user:<id>', 'email:<address>' or 'endpoint:<url>'.").ConfigureAwait(false);
                    return;
                }

                recipients.Add(new DispatchRecipient(recipientKind.Value, value));
            }

            var channels = new List<NotificationChannelKind>();
            foreach (var name in (Channels.GetOrDefault(context) ?? "").Split(',').Select(c => c.Trim()).Where(c => c.Length > 0))
            {
                if (!Enum.TryParse<NotificationChannelKind>(name, ignoreCase: true, out var channel))
                {
                    await FailAsync(context, "CHANNEL_INVALID", $"'{name}' is not a channel (Email, InApp, Webhook, Sms).").ConfigureAwait(false);
                    return;
                }

                channels.Add(channel);
            }

            var parameters = new List<DispatchParameter>();
            foreach (var line in await TemplatedLines.ReadAsync(Parameters, context, '=').ConfigureAwait(false))
            {
                if (line.Name is null)
                {
                    await FailAsync(context, "PARAMETER_INVALID", $"'{line.Line}' is not 'name = value'.").ConfigureAwait(false);
                    return;
                }

                parameters.Add(new DispatchParameter(line.Name, line.Value));
            }

            if (context.GetLastResult() is { } last && parameters.All(p => p.Name != "result"))
            {
                parameters.Add(new DispatchParameter("result", last.ToString() ?? ""));
            }

            if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false))
            {
                await FailAsync(context, "SITE_UNAVAILABLE", "The run has no site to notify from.").ConfigureAwait(false);
                return;
            }

            var dispatcher = context.GetService<INotificationDispatcher>();
            if (dispatcher is null)
            {
                await FailAsync(context, "NOTIFICATIONS_UNAVAILABLE", "The Notifications module is not composed in this host.").ConfigureAwait(false);
                return;
            }

            try
            {
                var requestId = await dispatcher.DispatchAsync(
                    new NotificationDispatchRequest(templateKey, recipients, channels, parameters, Severity.GetOrDefault(context), Topic?.GetOrDefault(context)),
                    context.CancellationToken).ConfigureAwait(false);
                context.Set(RequestId, requestId.ToString());
                await SucceedAsync(context, requestId.ToString()).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await FailAsync(context, "DISPATCH_REFUSED", ex.Message).ConfigureAwait(false);
            }
        }
    }
}
