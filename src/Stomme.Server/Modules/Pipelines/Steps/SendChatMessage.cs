#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Tenancy;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// A message to a chat through one of the site's connections (BL-229, n8n's Slack, Telegram, Discord, Mattermost and Microsoft
    /// Teams nodes, their send message, its Twilio node's SMS and its WhatsApp Business Cloud node's text): a Slack channel, a
    /// Telegram chat, a Discord, a Mattermost or a Teams channel or a Teams chat, a phone by text message (or WhatsApp) through
    /// Twilio, or a phone on WhatsApp through Meta's Cloud API, the token kept in the site's secret store. The
    /// message goes through the extraction port as the connector's <c>message</c> operation, so it is the connection's
    /// endpoint, its token and the HOST's egress, exactly as an extraction is. The message's id is the result and an output.
    /// </summary>
    /// <remarks>
    /// The service's own refusal - a channel not found, a bot not in the channel - comes back as the connector's record and is
    /// the step's failure in the service's words, <c>MESSAGE_REFUSED</c>; anything else that goes wrong is the port's category,
    /// <c>EXTRACTION_&lt;OUTCOME&gt;</c>, honouring <c>On failure</c> like every step.
    /// </remarks>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Notifications", "Send a message to a chat - a Slack, Discord, Mattermost or Microsoft Teams channel, a Telegram or Teams chat, a phone by SMS through Twilio or on WhatsApp - through one of the site's connections; the token stays in the site's secret store.", DisplayName = "Send chat message")]
    public class SendChatMessage : DataStep
    {
        /// <summary>The operation a chat connector answers a message with.</summary>
        public const string MessageOperation = "message";

        /// <summary>The connections this step can send through, by connector type.</summary>
        public static readonly string[] ChatConnectorTypes = { "slack", "telegram", "discord", "mattermost", "twilio", "teams", "whatsapp" };

        [Input(DisplayName = "Connection", UIHint = PipelineInputHints.ConnectionPicker, Description = "One of the site's Slack, Telegram, Discord, Mattermost, Twilio, Microsoft Teams or WhatsApp Business connections (Admin > Connections) - its token stays there.", Order = 1)]
        [PipelineConnectorTypes("slack", "telegram", "discord", "mattermost", "twilio", "teams", "whatsapp")]
        public Input<string>? Connection { get; set; }

        [Input(DisplayName = "To", Description = "Where it goes: a Slack channel (#general, or its id) or person's id; a Telegram chat's id or a channel's @name; a Discord or a Mattermost channel's id; for Teams a channel as its team's id/the channel's id, or a chat's id; for Twilio a phone number (+46701740605) or whatsapp:+46...; for WhatsApp Business a phone number with its country code (+46701740605)", Order = 2)]
        public Input<string>? To { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "The message. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Text { get; set; }

        [Input(DisplayName = "In reply to", Description = "Optional: a message to answer, by its id - on Slack its ts, which answers it in its thread; on Mattermost the post its thread starts with; on Teams a channel message's id; on WhatsApp a message's id (wamid....), quoted in the reply. Not for an SMS or a Teams chat.", Order = 4)]
        public Input<string?>? ReplyTo { get; set; }

        [Output(DisplayName = "Message id")]
        public Output<string>? MessageId { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var connection = Required(context, Connection, "Connection");
            if (!Guid.TryParse(connection.Trim(), out var connectionId))
            {
                await FailAsync(context, "CONNECTION_INVALID", $"'{connection}' is not a connection id.").ConfigureAwait(false);
                return;
            }

            var to = To?.GetOrDefault(context)?.Trim();
            if (string.IsNullOrEmpty(to))
            {
                await FailAsync(context, "TO_MISSING", "Say where the message goes (To).").ConfigureAwait(false);
                return;
            }

            if (TextOrLastResult(context, Text) is not { Length: > 0 } text || string.IsNullOrWhiteSpace(text))
            {
                await FailAsync(context, "TEXT_MISSING", "The message has no text.").ConfigureAwait(false);
                return;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["to"] = to, ["text"] = text };
            if (ReplyTo?.GetOrDefault(context) is { Length: > 0 } reply && !string.IsNullOrWhiteSpace(reply))
            {
                parameters["reply_to"] = reply.Trim();
            }

            if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false))
            {
                await FailAsync(context, "SITE_UNAVAILABLE", "The run has no site whose connections it could use.").ConfigureAwait(false);
                return;
            }

            var extraction = context.GetService<IConnectorExtractionService>();
            if (extraction is null)
            {
                await FailAsync(context, "CONNECTIVITY_UNAVAILABLE", "The Connectivity module is not composed in this host.").ConfigureAwait(false);
                return;
            }

            var result = await extraction.ExtractAsync(connectionId, MessageOperation, parameters, maxRecords: 1, context.CancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                await FailAsync(context, "EXTRACTION_" + result.Outcome.ToString().ToUpperInvariant(), result.Outcome is ConnectorOutcome.OperationUndefined or ConnectorOutcome.ExtractionUnsupported
                    ? "The connection's connector does not send chat messages - choose a Slack, Telegram, Discord, Mattermost, Twilio, Microsoft Teams or WhatsApp Business one."
                    : result.Message).ConfigureAwait(false);
                return;
            }

            var fields = result.Records.Count == 1 ? result.Records[0].Fields : null;
            if (fields?.TryGetValue("refused", out var refused) == true && refused is string reason)
            {
                await FailAsync(context, "MESSAGE_REFUSED", reason).ConfigureAwait(false);
                return;
            }

            if (fields?.TryGetValue("id", out var id) != true || id is not string messageId)
            {
                await FailAsync(context, "MESSAGE_UNSUPPORTED", "The connection's connector does not send chat messages - choose a Slack, Telegram, Discord, Mattermost, Twilio, Microsoft Teams or WhatsApp Business one.").ConfigureAwait(false);
                return;
            }

            context.Set(MessageId, messageId);
            await SucceedAsync(context, messageId).ConfigureAwait(false);
        }
    }
}
