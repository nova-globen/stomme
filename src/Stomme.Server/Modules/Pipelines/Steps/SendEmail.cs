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
    /// An email sent from a mailbox through one of the site's connections (n8n's Gmail and Microsoft Outlook nodes, their
    /// Send and Reply): the
    /// mailbox's account kept in the site's secret store, the message going through the extraction port as the connector's
    /// <c>send</c> operation - the connection's endpoint, its account and the HOST's egress, exactly as an extraction is. Its body
    /// is an input of its own, as many lines as it takes (a connection's parameters are one line each). The message's id is the
    /// result and an output, its thread's another.
    /// </summary>
    /// <remarks>
    /// Why a message was not sent - a recipient that is not an address, the mail service's own refusal - comes back as the
    /// connector's record and is the step's failure in those words, <c>MESSAGE_REFUSED</c>; anything else that goes wrong is the
    /// port's category, <c>EXTRACTION_&lt;OUTCOME&gt;</c>, honouring <c>On failure</c> like every step. This is not Send
    /// notification, which sends through the site's own mail relay to its users.
    /// </remarks>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Notifications", "Send an email from a mailbox - a Gmail or a Microsoft Outlook one - through one of the site's connections; the mailbox's account stays in the site's secret store.", DisplayName = "Send email")]
    public class SendEmail : DataStep
    {
        /// <summary>The operation a mail connector answers a message with.</summary>
        public const string SendOperation = "send";

        [Input(DisplayName = "Connection", UIHint = PipelineInputHints.ConnectionPicker, Description = "One of the site's Gmail or Microsoft Outlook connections (Admin > Connections) - the mailbox it is sent from.", Order = 1)]
        [PipelineConnectorTypes("gmail", "outlook")]
        public Input<string>? Connection { get; set; }

        [Input(DisplayName = "To", Description = "Addresses, comma-separated. Empty on a reply: the sender of the message answered.", Order = 2)]
        public Input<string?>? To { get; set; }

        [Input(DisplayName = "Cc", Description = "Addresses, comma-separated.", Order = 3)]
        public Input<string?>? Cc { get; set; }

        [Input(DisplayName = "Bcc", Description = "Addresses, comma-separated.", Order = 4)]
        public Input<string?>? Bcc { get; set; }

        [Input(Description = "One line. Empty on a reply: Re: the original's.", Order = 5)]
        public Input<string?>? Subject { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "The plain-text body. Empty, with no HTML either: the previous step's result.", Order = 6)]
        public Input<string?>? Text { get; set; }

        [Input(DisplayName = "HTML", UIHint = InputUIHints.MultiLine, Description = "An HTML body. With a text one too, both are sent and the reader's mail program picks.", Order = 7)]
        public Input<string?>? Html { get; set; }

        [Input(DisplayName = "In reply to", Description = "Optional: a message's id - sent as a reply in its thread.", Order = 8)]
        public Input<string?>? ReplyTo { get; set; }

        [Output(DisplayName = "Message id")]
        public Output<string>? MessageId { get; set; }

        [Output(DisplayName = "Thread id")]
        public Output<string>? ThreadId { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var connection = Required(context, Connection, "Connection");
            if (!Guid.TryParse(connection.Trim(), out var connectionId))
            {
                await FailAsync(context, "CONNECTION_INVALID", $"'{connection}' is not a connection id.").ConfigureAwait(false);
                return;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Put(string name, Input<string?>? input)
            {
                if (input?.GetOrDefault(context) is { } value && !string.IsNullOrWhiteSpace(value))
                {
                    parameters[name] = value.Trim();
                }
            }

            Put("to", To);
            Put("cc", Cc);
            Put("bcc", Bcc);
            Put("subject", Subject);
            Put("reply_to", ReplyTo);
            if (Html?.GetOrDefault(context) is { } html && !string.IsNullOrWhiteSpace(html))
            {
                parameters["html"] = html;
            }

            var text = parameters.ContainsKey("html") ? Text?.GetOrDefault(context) : TextOrLastResult(context, Text);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parameters["text"] = text;
            }

            if (!parameters.ContainsKey("to") && !parameters.ContainsKey("reply_to"))
            {
                await FailAsync(context, "TO_MISSING", "Say whom the email goes to (To).").ConfigureAwait(false);
                return;
            }

            if (!parameters.ContainsKey("text") && !parameters.ContainsKey("html"))
            {
                await FailAsync(context, "TEXT_MISSING", "The email has no body.").ConfigureAwait(false);
                return;
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

            var result = await extraction.ExtractAsync(connectionId, SendOperation, parameters, maxRecords: 1, context.CancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                await FailAsync(context, "EXTRACTION_" + result.Outcome.ToString().ToUpperInvariant(), result.Outcome is ConnectorOutcome.OperationUndefined or ConnectorOutcome.ExtractionUnsupported
                    ? "The connection's connector does not send email - choose a Gmail or a Microsoft Outlook one."
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
                await FailAsync(context, "MESSAGE_UNSUPPORTED", "The connection's connector does not send email - choose a Gmail or a Microsoft Outlook one.").ConfigureAwait(false);
                return;
            }

            context.Set(MessageId, messageId);
            if (fields.TryGetValue("thread_id", out var thread) && thread is string threadId)
            {
                context.Set(ThreadId, threadId);
            }

            await SucceedAsync(context, messageId).ConfigureAwait(false);
        }
    }
}
