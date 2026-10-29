#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Notifications;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// The "Ask for approval" step (n8n's human in the loop) as the step, the server, the run page and the editor name
    /// it: the run pauses on a question until a person with <c>pipelines:approve</c> on the site decides, and goes on down the
    /// outcome they chose - or down Expired when a time to decide was given and passed.
    /// </summary>
    public static class PipelineApprovals
    {
        /// <summary>The step's type name, which is also the name its bookmark is kept under.</summary>
        public const string StepType = "Stomme.AskForApproval";

        public const string QuestionInput = "Question";

        public const string DecideWithinInput = "DecideWithin";

        public const string Approved = "Approved";

        public const string Rejected = "Rejected";

        public const string Expired = "Expired";

        /// <summary>What a decision is resumed with - the run's input for that one resumption, never stored with the run.</summary>
        public const string DecisionKey = "stomme:approval:decision";

        public const string DecidedByKey = "stomme:approval:by";

        public const string CommentKey = "stomme:approval:comment";

        /// <summary>The most a decision's comment may hold.</summary>
        public const int MaxCommentLength = 2000;

        /// <summary>The most of a question, and of its details, an approval keeps - the rest is cut, saying so.</summary>
        public const int MaxTextLength = 4000;

        /// <summary>The text, cut to <see cref="MaxTextLength"/>.</summary>
        public static string Cut(string? text)
        {
            var value = text?.Trim() ?? "";
            return value.Length > MaxTextLength ? value[..MaxTextLength] + "… (cut)" : value;
        }

        public const string NotifyInput = "Notify";

        public const string NotifyByInput = "NotifyBy";

        /// <summary>A recipient of the notice that a decision waits: everyone in a role on the site, a user by id, or an address.</summary>
        public const string RoleRecipient = "role";

        public const string UserRecipient = "user";

        public const string EmailRecipient = "email";

        /// <summary>The ways a notice that a decision waits can go.</summary>
        public static readonly IReadOnlyList<NotificationChannelKind> NotifyChannels = new[] { NotificationChannelKind.InApp, NotificationChannelKind.Email };

        /// <summary>
        /// Who is told a decision waits, one per line - <c>role:&lt;name&gt;</c>, <c>user:&lt;id&gt;</c> or <c>email:&lt;address&gt;</c>;
        /// empty is no one. ONE reading for the step and the editor's problems; the error says how to write a line.
        /// </summary>
        public static bool TryParseRecipients(string? text, out List<(string Kind, string Value)> recipients, out string? error)
        {
            recipients = new List<(string Kind, string Value)>();
            error = null;
            foreach (var line in (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                var colon = line.IndexOf(':');
                var kind = colon > 0 ? line[..colon].Trim().ToLowerInvariant() : "";
                var value = colon > 0 ? line[(colon + 1)..].Trim() : "";
                var known = kind is RoleRecipient or UserRecipient or EmailRecipient
                    && value.Length > 0
                    && (kind != UserRecipient || int.TryParse(value, out _))
                    && (kind != EmailRecipient || value.Contains('@'));
                if (!known)
                {
                    error = $"'{line}' is not role:<name>, user:<id> or email:<address>.";
                    recipients.Clear();
                    return false;
                }

                recipients.Add((kind, value));
            }

            return true;
        }

        /// <summary>How a notice goes - comma-separated InApp and Email; empty is InApp.</summary>
        public static bool TryParseChannels(string? text, out List<NotificationChannelKind> channels, out string? error)
        {
            channels = new List<NotificationChannelKind>();
            error = null;
            foreach (var name in (text ?? "").Split(',').Select(name => name.Trim()).Where(name => name.Length > 0))
            {
                if (!Enum.TryParse<NotificationChannelKind>(name, ignoreCase: true, out var channel) || !NotifyChannels.Contains(channel))
                {
                    error = $"'{name}' is not a way to notify - write InApp, Email, or both.";
                    channels.Clear();
                    return false;
                }

                if (!channels.Contains(channel))
                {
                    channels.Add(channel);
                }
            }

            if (channels.Count == 0)
            {
                channels.Add(NotificationChannelKind.InApp);
            }

            return true;
        }
    }

    /// <summary>
    /// One run waiting for a decision: the run, its pipeline, the step, what is asked and since when. The details are shown to
    /// those who may decide (or author) only - a viewer sees the question, which the run's log already names.
    /// </summary>
    public class PipelineApprovalView
    {
        /// <summary>The approval's id (the engine's bookmark) - what a decision names.</summary>
        public string Id { get; set; } = "";

        public string RunId { get; set; } = "";

        public string? DefinitionId { get; set; }

        public string? PipelineName { get; set; }

        public string? StepId { get; set; }

        public string? StepName { get; set; }

        public string Question { get; set; } = "";

        public string? Details { get; set; }

        public DateTime RequestedOnUtc { get; set; }

        /// <summary>When the time to decide ends - null when it does not.</summary>
        public DateTime? ExpiresOnUtc { get; set; }
    }

    /// <summary>A decision: approve or reject, with a comment the run can read.</summary>
    public class ApprovalDecisionRequest
    {
        public bool Approve { get; set; }

        public string? Comment { get; set; }
    }

    /// <summary>What a decision did: the run it resumed and the outcome it goes on down.</summary>
    public class PipelineApprovalDecided
    {
        public string RunId { get; set; } = "";

        public string Outcome { get; set; } = "";
    }
}
