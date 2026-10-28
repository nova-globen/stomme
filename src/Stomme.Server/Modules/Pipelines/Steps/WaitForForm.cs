#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What a run waiting for a form keeps on its bookmark: the SHA-256 of its call address's token, never the token.</summary>
    public sealed record FormWaitStimulus(string TokenHash);

    /// <summary>
    /// Wait for a form (BL-229, n8n's Wait node resumed "on form submitted", and its resume form URL): the run pauses on a form -
    /// its fields edited as the Form trigger's are - which a person fills in at the run's FORM ADDRESS, the run's
    /// <see cref="CallAddress"/> with <c>/form</c> after it (Call address's Form address output: send it to them, then wait). The
    /// answers are the result, down Submitted; down Timed out when "Time out after" was given and passed. The form travels on the
    /// run's bookmark (its metadata), so the page shows the fields this wait asks for; once answered - or timed out - the page says
    /// the form is closed. The page after a submission is a plain thank-you: the form has closed by then, and carrying the author's
    /// own words to it in the address would let anyone link to a page of the site saying whatever they wrote. Durable like <see cref="WaitForCall"/>: the run is stored and holds no worker. The page is public, as
    /// the Form trigger's is, behind <c>Pipelines:Forms:Enabled</c>; the address's random token is what admits a person.
    /// <para>
    /// With <see cref="NextPage"/> (n8n's Form node, a form's next page) no address is handed to anyone: the person who sent the form
    /// before it - the Form trigger's, or an earlier page's - is taken to this one at once, the run's form address made for it when
    /// no Call address did. The submission waits for the run to reach the page (<see cref="PipelineFormContinuations"/>), and is
    /// answered with it once the run - this bookmark with it - is stored, so the page is there when the browser asks for it.
    /// </para>
    /// </summary>
    [FlowNode(Submitted, PipelineCalls.TimedOut)]
    [Step("Stomme", "Control Flow", "Pause the run until a person fills in a form at the run's form address; the answers are the result. Durable: the run is stored and holds no worker.", DisplayName = "Wait for a form")]
    public class WaitForForm : Step<string>
    {
        public const string StepTypeName = "Stomme.WaitForForm";

        public const string Submitted = "Submitted";

        /// <summary>Where the bookmark keeps the form, as JSON.</summary>
        public const string FormKey = "stomme:form:spec";

        /// <summary>What a submission is resumed with - for that one resumption.</summary>
        public const string AnswersKey = "stomme:form:answers";

        /// <summary>On the bookmark when the run's pipeline has a next page: a submission of this form waits to be taken on to it.</summary>
        public const string LeadsOnKey = "stomme:form:leadsOn";

        /// <summary>On a next page's bookmark: the name of the Form trigger's form the pages follow - its thank-you ends them.</summary>
        public const string StartedByKey = "stomme:form:startedBy";

        /// <summary>Where the run keeps that name for its later pages (the trigger's input is the first burst's only).</summary>
        public const string StartedByProperty = "stomme:form:startedBy";

        [Input(Description = "The form's heading.", Order = 1)]
        public Input<string?>? Title { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "What the form says under its heading - what to fill in, and why.", Order = 2)]
        public Input<string?>? Description { get; set; }

        [Input(UIHint = PipelineInputHints.FormFields, Description = "The form's fields: each one's label, kind, whether it is required and its choices.", Order = 3)]
        public Input<string?>? Fields { get; set; }

        [Input(DisplayName = "Submit label", Description = "The button's words.", DefaultValue = "Submit", Order = 4)]
        public Input<string?>? SubmitLabel { get; set; }

        [Input(DisplayName = "Time out after", Description = "How long to wait for the answers: 10 minutes, 2 hours, 3 days. When it passes, the run goes on down Timed out. Empty: it waits until answered.", Order = 5)]
        public Input<string?>? TimeOutAfter { get; set; }

        [Input(DisplayName = "Next page", Description = "Take the person who sent the form before it - the Form trigger's, or an earlier page's - straight on to this form, as n8n's next form page; no Call address needed. Off: someone is sent the run's form address.", Order = 6)]
        public Input<bool> NextPage { get; set; } = new(new Literal<bool>(false));

        protected override ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var nextPage = NextPage.GetOrDefault(context);
            var token = CallAddress.TokenOf(context);
            if (token is null)
            {
                if (!nextPage)
                {
                    throw new FaultException("FORM_NO_ADDRESS", "Pipelines", "Configuration", "Place a 'Call address' step before 'Wait for a form' - its Form address is where the form is filled in - or tick Next page.");
                }

                // A next page needs no address handed on, but the run's form address is still where it is filled in: made here, as Call address makes it.
                token = PipelineCalls.NewToken();
                context.PipelineExecutionContext.Properties[CallAddress.TokenProperty] = token;
            }

            var fields = PipelineForms.Parse(Fields?.GetOrDefault(context), out var fieldsError);
            if (fields is null || PipelineForms.Problems(fields) is { Count: > 0 })
            {
                throw new FaultException("FORM_INVALID", "Pipelines", "Configuration", "The form's fields: " + (fieldsError ?? string.Join(" ", PipelineForms.Problems(fields!))));
            }

            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
            var metadata = new Dictionary<string, string>
            {
                [FormKey] = JsonSerializer.Serialize(new StoredForm(
                    Text(Title?.GetOrDefault(context)) ?? "A form", Text(Description?.GetOrDefault(context)), PipelineForms.Serialize(fields),
                    Text(SubmitLabel?.GetOrDefault(context)) ?? "Submit")),
            };
            if (TimeOutAfter?.GetOrDefault(context) is { } within && !string.IsNullOrWhiteSpace(within))
            {
                if (!PipelineWaits.TryParseDuration(within, out var span, out var error))
                {
                    throw new FaultException("FORM_INVALID", "Pipelines", "Configuration", "Time out after: " + error);
                }

                var maxDays = Math.Max(1, context.GetService<IOptions<PipelineTriggerOptions>>()?.Value.Waits.MaxDays ?? new WaitOptions().MaxDays);
                if (span > TimeSpan.FromDays(maxDays))
                {
                    throw new FaultException("FORM_TOO_LONG", "Pipelines", "Configuration", $"A wait for a form can be at most Pipelines:Waits:MaxDays ({maxDays}) days.");
                }

                metadata[WaitForCall.ExpiresAtKey] = (now + span).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            if (LeadsOn(context.PipelineExecutionContext))
            {
                metadata[LeadsOnKey] = "true";
            }

            // A next page of the Form trigger's form: the form's name, so its last page ends on that form's own thank-you (n8n's ending).
            if (nextPage && StartedBy(context) is { } startedBy)
            {
                metadata[StartedByKey] = startedBy;
            }

            context.CreateBookmark(new FormWaitStimulus(PipelineCalls.Hash(token)), ResumeAsync, includeStepInstanceId: false, metadata);
            context.AddExecutionLogEntry("Waiting for a form", WaitForCall.ExpiresOf(metadata) is { } expires
                ? $"Waits until the form at the run's form address is filled in, or {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC."
                : "Waits until the form at the run's form address is filled in.");

            // The person who sent the form before it, taken on to this one - once the run is stored with this bookmark, so the page
            // is there when their browser asks for it (the deferred tasks run after the bookmarks are persisted).
            if (nextPage && PipelineFormContinuations.IdOf(context.PipelineInput) is { } submission && context.GetService<PipelineFormContinuations>() is { } continuations)
            {
                var next = token;
                context.DeferTask(() => continuations.TryContinue(submission, next));
                context.AddExecutionLogEntry("Next page", "The person who sent the form before it is taken on to this one.");
            }

            return ValueTask.CompletedTask;
        }

        // The Form trigger's form the run started on: in the first burst's input, kept on the run for the pages after.
        private static string? StartedBy(StepExecutionContext context)
        {
            if (context.TryGetPipelineInput<string>(Form.NameInputKey, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                context.PipelineExecutionContext.Properties[StartedByProperty] = name;
                return name;
            }

            return context.PipelineExecutionContext.Properties.TryGetValue(StartedByProperty, out var kept) ? Plain(kept) is { Length: > 0 } text ? text : null : null;
        }

        /// <summary>Whether the run's pipeline has a next page - a Wait for a form with Next page ticked - so a form submission in it may be taken on.</summary>
        public static bool LeadsOn(PipelineExecutionContext run) =>
            run.Nodes.Any(node => node.Step is WaitForForm wait && Ticked(wait.NextPage));

        // A literal tick as the engine holds it - a bool, or the text or JSON the definition was read from.
        private static bool Ticked(Input<bool>? input) => input?.Expression is { Type: "Literal" } expression && expression.Value switch
        {
            bool flag => flag,
            string text => bool.TryParse(text.Trim(), out var parsed) && parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } element => bool.TryParse(element.GetString(), out var parsed) && parsed,
            _ => false,
        };

        // Called by name when the bookmark is resumed - by a submission, or by the task that watches the time.
        private async ValueTask ResumeAsync(StepExecutionContext context)
        {
            if (context.PipelineInput.TryGetValue(PipelineCalls.TimedOutKey, out var timedOut) && Plain(timedOut) == "true")
            {
                context.Set(Result, null);
                context.AddExecutionLogEntry(PipelineCalls.TimedOut, "Nobody filled in the form in time.");
                await context.CompleteStepWithOutcomesAsync(PipelineCalls.TimedOut).ConfigureAwait(false);
                return;
            }

            var answers = context.PipelineInput.TryGetValue(AnswersKey, out var given) ? Plain(given) : "{}";
            context.Set(Result, answers);
            context.AddExecutionLogEntry(Submitted, "The form was filled in.");
            await context.CompleteStepWithOutcomesAsync(Submitted).ConfigureAwait(false);
        }

        /// <summary>The form a waiting run's bookmark carries; null when it carries none this step wrote.</summary>
        public static PipelineFormSpec? FormOf(IDictionary<string, string>? metadata)
        {
            if (metadata is null || !metadata.TryGetValue(FormKey, out var json))
            {
                return null;
            }

            try
            {
                var stored = JsonSerializer.Deserialize<StoredForm>(json);
                return stored is null ? null : new PipelineFormSpec("form", stored.Title, stored.Description, PipelineForms.Parse(stored.Fields, out _) ?? new List<PipelineFormField>(), stored.SubmitLabel, null)
                {
                    LeadsOn = metadata.TryGetValue(LeadsOnKey, out var leadsOn) && leadsOn == "true",
                    StartedBy = metadata.TryGetValue(StartedByKey, out var startedBy) && startedBy.Length > 0 ? startedBy : null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>What a submission is resumed with - and the id it waits under to be taken on to a next page, when it does.</summary>
        public static IDictionary<string, object> SubmissionInput(string answers, string? continueTo = null)
        {
            var input = new Dictionary<string, object> { [AnswersKey] = answers };
            if (continueTo is not null)
            {
                input[PipelineFormContinuations.InputKey] = continueTo;
            }

            return input;
        }

        private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Plain(object? value) => value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            bool flag => flag ? "true" : "false",
            var other => other.ToString() ?? string.Empty,
        };

        private sealed record StoredForm(string Title, string? Description, string Fields, string SubmitLabel);
    }
}
