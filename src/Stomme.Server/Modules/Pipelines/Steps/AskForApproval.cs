#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Notifications;
using Stomme.Models.Pipelines;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Tenancy;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Notifications;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What a run waiting for approval keeps on its bookmark: the step, the question, and since and until when.</summary>
    public sealed record ApprovalStimulus(string StepId, string StepName, string Question, string? Details, string RequestedAt, string? ExpiresAt);

    /// <summary>
    /// Ask for approval (BL-229, n8n's "send and wait for approval", human in the loop): the run pauses on a question until a
    /// person with <c>pipelines:approve</c> on the site approves or rejects it - on the run's page, or from the list of runs
    /// waiting for a decision - and goes on down Approved or Rejected. Durable like <see cref="Wait"/>: the step leaves a
    /// bookmark that carries the question, the run is stored and holds no worker. With "Decide within" given, a run nobody
    /// decided in time goes on down Expired (<see cref="DueWaitsTask"/> watches the time).
    /// </summary>
    /// <remarks>
    /// The result is the decision as a record - <c>{ "decision", "approved", "by", "comment", "decidedAt" }</c> - so a later step
    /// reads <c>{{ approval.by }}</c> or <c>{{ approval.comment }}</c> from the variable it is saved to. Who decided, and what
    /// they said, is also a line of the run's log. A decision is resumed into the run as input for that one resumption: it is
    /// never stored as what the run was started with. "Notify" tells the people it names that a decision waits (n8n's node
    /// sends the request) - a role is everyone holding it on the site - with the question and the address of the run's page,
    /// through the site's Notifications module; a notice that cannot go is a line of the log, never a failure.
    /// </remarks>
    [FlowNode(PipelineApprovals.Approved, PipelineApprovals.Rejected, PipelineApprovals.Expired)]
    [Step("Stomme", "Control Flow", "Pause the run until a person approves or rejects - on the run's page, by anyone the site lets approve. Durable: the run is stored and holds no worker while it waits.", DisplayName = "Ask for approval")]
    public class AskForApproval : Step<string>
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        [Input(UIHint = InputUIHints.MultiLine, Description = "What the approver is asked - e.g. Pay invoice {{ invoice.number }}?", Order = 1)]
        public Input<string> Question { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "More for the approver to read before deciding - the record, the amount, a link. Shown to those who may decide.", Order = 2)]
        public Input<string?>? Details { get; set; }

        [Input(DisplayName = "Decide within", Description = "How long there is to decide: 4 hours, 2 days, 1 week. When it passes with no decision, the run goes on down Expired. Empty: the run waits until someone decides.", Order = 3)]
        public Input<string?>? DecideWithin { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Who is told a decision waits, one per line: role:<name> (everyone in the role), user:<id> or email:<address>. Each gets the question and a link to decide. Empty: no one is told; the runs list shows it.", Order = 4)]
        public Input<string?>? Notify { get; set; }

        [Input(DisplayName = "Notify by", Description = "InApp, Email, or both (comma-separated). Empty: InApp.", Order = 5)]
        public Input<string?>? NotifyBy { get; set; }

        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var question = Question.GetOrDefault(context);
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", "The step needs its 'Question' input.");
            }

            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            DateTimeOffset? expires = null;
            if (DecideWithin?.GetOrDefault(context) is { } within && !string.IsNullOrWhiteSpace(within))
            {
                if (!PipelineWaits.TryParseDuration(within, out var span, out var error))
                {
                    throw new FaultException("APPROVAL_INVALID", "Pipelines", "Configuration", "Decide within: " + error);
                }

                var maxDays = Math.Max(1, context.GetService<IOptions<PipelineTriggerOptions>>()?.Value.Waits.MaxDays ?? new WaitOptions().MaxDays);
                if (span > TimeSpan.FromDays(maxDays))
                {
                    throw new FaultException("APPROVAL_TOO_LONG", "Pipelines", "Configuration", $"The time to decide can be at most Pipelines:Waits:MaxDays ({maxDays}) days.");
                }

                expires = now + span;
            }

            var asked = PipelineApprovals.Cut(question);
            var details = Details?.GetOrDefault(context) is { } text && !string.IsNullOrWhiteSpace(text) ? PipelineApprovals.Cut(text) : null;
            context.CreateBookmark(new ApprovalStimulus(
                context.Step.Id,
                context.Step.Name ?? "",
                asked,
                details,
                Moment(now),
                expires is null ? null : Moment(expires.Value)), ResumeAsync);
            context.AddExecutionLogEntry("Awaiting approval", expires is null ? asked : $"{asked} (to be decided by {expires.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC)");
            await NotifyAsync(context, asked, expires, now).ConfigureAwait(false);
        }

        // The people the step names are told a decision waits - through the site's Notifications module, the generic.message
        // template. A notice that cannot go is a line of the run's log, never a failure: the run still waits, listed on the
        // runs page, and a decision there is all it needs.
        private async ValueTask NotifyAsync(StepExecutionContext context, string question, DateTimeOffset? expires, DateTimeOffset now)
        {
            var lines = Notify?.GetOrDefault(context);
            if (string.IsNullOrWhiteSpace(lines))
            {
                return;
            }

            if (!PipelineApprovals.TryParseRecipients(lines, out var named, out var error)
                || !PipelineApprovals.TryParseChannels(NotifyBy?.GetOrDefault(context), out var channels, out error))
            {
                context.AddExecutionLogEntry("Not notified", error!);
                return;
            }

            try
            {
                if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false) || context.GetService<SiteState>()?.Alias is not { } alias)
                {
                    context.AddExecutionLogEntry("Not notified", "The run has no site to notify from.");
                    return;
                }

                if (context.GetService<INotificationDispatcher>() is not { } dispatcher)
                {
                    context.AddExecutionLogEntry("Not notified", "The Notifications module is not composed in this host.");
                    return;
                }

                var recipients = await PipelineApprovalNotices.RecipientsAsync(named, alias.SiteId, context.GetService<IUserRoleRepository>(), now.UtcDateTime).ConfigureAwait(false);
                if (recipients.Count == 0)
                {
                    context.AddExecutionLogEntry("Not notified", "No one holds the roles it names on this site.");
                    return;
                }

                var runPage = await PipelineApprovalNotices.RunPageAsync(alias, context.GetService<IPageModuleRepository>(), context.PipelineExecutionContext.Id).ConfigureAwait(false);
                var (subject, body) = PipelineApprovalNotices.Message(context.PipelineExecutionContext.Pipeline.PipelineMetadata.Name, context.Step.Name, question, expires, runPage);
                await dispatcher.DispatchAsync(new NotificationDispatchRequest(
                    NotificationTemplateCatalogue.GenericMessageKey,
                    recipients,
                    channels,
                    new[] { new DispatchParameter("subject", subject), new DispatchParameter("body", body) },
                    NotificationSeverity.Information,
                    PipelineApprovalNotices.Topic), context.CancellationToken).ConfigureAwait(false);
                context.AddExecutionLogEntry("Notified", $"{recipients.Count} {(recipients.Count == 1 ? "recipient" : "recipients")} told by {string.Join(" and ", channels)}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.AddExecutionLogEntry("Not notified", ex.Message);
            }
        }

        // Called by name when the bookmark is resumed - with a person's decision, or with Expired by the task that watches the time.
        private async ValueTask ResumeAsync(StepExecutionContext context)
        {
            var decision = Read(context, PipelineApprovals.DecisionKey);
            var outcome = new[] { PipelineApprovals.Approved, PipelineApprovals.Rejected, PipelineApprovals.Expired }
                .FirstOrDefault(known => string.Equals(known, decision, StringComparison.OrdinalIgnoreCase))
                ?? throw new FaultException("APPROVAL_UNREADABLE", "Pipelines", "Runtime", "The approval was resumed without a decision.");
            var by = Read(context, PipelineApprovals.DecidedByKey);
            var comment = Read(context, PipelineApprovals.CommentKey);
            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;

            context.Set(Result, JsonSerializer.Serialize(new
            {
                decision = outcome,
                approved = outcome == PipelineApprovals.Approved,
                by = string.IsNullOrEmpty(by) ? null : by,
                comment = string.IsNullOrEmpty(comment) ? null : comment,
                decidedAt = Moment(now),
            }, Web));
            context.AddExecutionLogEntry(outcome, outcome == PipelineApprovals.Expired
                ? "No one decided in time."
                : $"{outcome} by {(string.IsNullOrEmpty(by) ? "someone" : by)}" + (string.IsNullOrEmpty(comment) ? "." : ": " + comment));
            await context.CompleteStepWithOutcomesAsync(outcome).ConfigureAwait(false);
        }

        private static string Read(StepExecutionContext context, string key) =>
            context.PipelineInput.TryGetValue(key, out var value) ? Text(value) : "";

        private static string Text(object? value) => value switch
        {
            null => "",
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
            var other => other.ToString() ?? "",
        };

        private static string Moment(DateTimeOffset moment) => moment.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        /// <summary>
        /// A stored bookmark's approval, whatever form the store kept the payload in - the record itself, JSON (either casing)
        /// or a dictionary; null when it is not one.
        /// </summary>
        public static ApprovalStimulus? Of(object? payload)
        {
            if (payload is ApprovalStimulus stimulus)
            {
                return stimulus;
            }

            JsonElement element;
            try
            {
                element = payload is JsonElement json ? json : JsonSerializer.SerializeToElement(payload);
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var fields = element.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            string? Field(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var question = Field(nameof(ApprovalStimulus.Question));
            return question is null
                ? null
                : new ApprovalStimulus(
                    Field(nameof(ApprovalStimulus.StepId)) ?? "",
                    Field(nameof(ApprovalStimulus.StepName)) ?? "",
                    question,
                    Field(nameof(ApprovalStimulus.Details)),
                    Field(nameof(ApprovalStimulus.RequestedAt)) ?? "",
                    Field(nameof(ApprovalStimulus.ExpiresAt)));
        }

        /// <summary>A moment an approval keeps, as UTC; null when there is none or it cannot be read.</summary>
        public static DateTimeOffset? MomentOf(string? text) =>
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment) ? moment : null;

        /// <summary>
        /// What a decision is resumed with: the outcome, who decided, and their comment. It is input for that one resumption - the
        /// engine stores only a pipeline's declared inputs - so a second approval in a run never reads the first one's.
        /// </summary>
        public static IDictionary<string, object> DecisionInput(string outcome, string? by, string? comment) => new Dictionary<string, object>
        {
            [PipelineApprovals.DecisionKey] = outcome,
            [PipelineApprovals.DecidedByKey] = by ?? "",
            [PipelineApprovals.CommentKey] = comment ?? "",
        };
    }
}
