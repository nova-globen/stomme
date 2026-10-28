#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The stimulus an "Email received" trigger is indexed by: the connection, the mailbox and the two choices - so pipelines
    /// watching a mailbox the same way share one look at it.
    /// </summary>
    public sealed record EmailReceivedStimulus(string Connection, string Mailbox, bool MarkRead, bool KeepAttachments);

    /// <summary>
    /// n8n's Email Trigger (IMAP), as an engine TRIGGER (BL-229): a published pipeline that starts with it is indexed under one of
    /// the site's IMAP connections and a mailbox, and <c>Triggers/WatchedMailboxes.cs</c> looks at that mailbox through the
    /// connection - the credentials in the site's secret store, the host's egress, never the pipeline's - starting a run for
    /// each message that arrived since it last looked, the message its result (<see cref="PipelineEmailEvent"/>). Messages are
    /// followed by their UID, not by being unread, so a message a person read first still starts its run; messages already
    /// there when the pipeline is published start nothing. A connection that is no id is refused at the save. A Gmail connection
    /// is watched the same way (n8n's Gmail Trigger): the connector speaks the same operations, a label its mailbox and the
    /// points in the label's history its messages were added at its UIDs.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when an e-mail arrives in a mailbox of one of the site's IMAP or Gmail connections - n8n's Email Trigger (IMAP) and Gmail Trigger.", DisplayName = "Email received")]
    public class EmailReceived : Trigger<string>
    {
        public const string MessageInputKey = "__EmailReceived";

        [Input(DisplayName = "Connection", UIHint = PipelineInputHints.ConnectionPicker, Description = "One of the site's IMAP or Gmail connections (Admin > Connections) - the account whose mailbox is watched.", Order = 1)]
        [PipelineConnectorTypes("imap", "gmail")]
        public Input<string> Connection { get; set; } = null!;

        [Input(Description = "The mailbox (folder) to watch - INBOX, or another by its full name; on Gmail, INBOX or a label's id.", DefaultValue = PipelineEmailEvent.Inbox, Order = 2)]
        public Input<string?> Mailbox { get; set; } = null!;

        [Input(DisplayName = "Mark as read", Description = "Each message a run is started for is flagged read on the server, as n8n's trigger does by default.", Order = 3)]
        public Input<bool> MarkRead { get; set; } = new(new Literal<bool>(false));

        [Input(DisplayName = "Keep attachments", Description = "Each attachment is kept in the site's pipeline folder (under mail/), its path in the message for a file step to read. Off: named only.", Order = 4)]
        public Input<bool> KeepAttachments { get; set; } = new(new Literal<bool>(false));

        /// <summary>Why a connection cannot be the one a trigger watches - null when it can.</summary>
        public static string? ConnectionProblem(string? connection) =>
            Guid.TryParse(connection?.Trim(), out _) ? null : $"'{connection}' is not a connection id - choose one of the site's IMAP or Gmail connections.";

        /// <summary>The stimulus for a connection, a mailbox and the two choices - as it is indexed at publish - or the refusal, thrown.</summary>
        public static EmailReceivedStimulus Stimulus(string? connection, string? mailbox, bool markRead, bool keepAttachments)
        {
            if (ConnectionProblem(connection) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            return new EmailReceivedStimulus(Guid.Parse(connection!.Trim()).ToString("D"), string.IsNullOrWhiteSpace(mailbox) ? PipelineEmailEvent.Inbox : mailbox.Trim(), markRead, keepAttachments);
        }

        protected override object GetTriggerPayload(TriggerIndexingContext context) =>
            Stimulus(context.Get(Connection), context.Get(Mailbox), context.Get(MarkRead), context.Get(KeepAttachments));

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself (measured - the run sat Suspended).
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.SetResult(context.TryGetPipelineInput<string>(MessageInputKey, out var message) ? message : "");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
