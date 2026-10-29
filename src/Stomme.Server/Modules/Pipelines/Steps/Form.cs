#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The stimulus a form is indexed and fired by: the form's name on the site. Its property is not a webhook's, so the two never hash alike.</summary>
    public sealed record FormStimulus(string Form);

    /// <summary>
    /// n8n's Form trigger: a page the site serves at <c>api/Pipelines/forms/{name}</c> (behind <c>Pipelines:Forms:Enabled</c>,
    /// rate-limited, anonymous like a webhook - a form is for people outside the site), with this step's title, description and
    /// fields; a submission that answers them starts the pipeline with the answers as its result, one JSON object with a key per
    /// field, and the person is shown <see cref="Answer"/>. The fields are judged where they are saved and published.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when a person submits the form the site shows for it.", DisplayName = "Form submitted")]
    public class Form : Trigger<string>
    {
        public const string StepTypeName = PipelineForms.StepType;
        public const string SubmissionInputKey = "__FormSubmission";
        public const string NameInputKey = "__FormName";

        [Input(DisplayName = "Name", Description = "The form's name: the last segment of its address, api/Pipelines/forms/{name}. Letters, digits, '-', '_' and '.'.", Order = 1)]
        public Input<string> FormName { get; set; } = null!;

        [Input(Description = "The form's heading.", Order = 2)]
        public Input<string> Title { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "Shown under the heading.", Order = 3)]
        public Input<string?>? Description { get; set; }

        [Input(UIHint = PipelineInputHints.FormFields, Description = "What the form asks: each field's label, kind, whether it is required and, for a dropdown, its choices.", Order = 4)]
        public Input<string> Fields { get; set; } = null!;

        [Input(DisplayName = "Button", Description = "The submit button's label.", DefaultValue = "Submit", Order = 5)]
        public Input<string> SubmitLabel { get; set; } = new(new Literal<string>("Submit"));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Shown after a submission - after its last page, when Wait for a form steps with Next page follow it. Empty: a plain thank-you.", Order = 6)]
        public Input<string?>? Answer { get; set; }

        [Input(DisplayName = "Then go to", Description = "An address to take the person to once the form is sent - your own site's page, say - instead of the Answer; https:// or http://. Empty: the Answer is shown.", Order = 7)]
        public Input<string?>? Redirect { get; set; }

        /// <summary>Whether an address is one a form may send a person on to: absolute, http or https.</summary>
        public static bool IsRedirect(string? address) =>
            Stomme.Shared.Utilities.TryCreateAbsoluteUri(address?.Trim(), out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

        protected override object GetTriggerPayload(TriggerIndexingContext context)
        {
            var name = context.Get(FormName);
            if (!Webhook.IsValidName(name))
            {
                throw new InvalidOperationException($"'{name}' is not a form name (letters, digits, '-', '_', '.', at most 100 characters).");
            }

            if (context.Get(Redirect) is { } redirect && !string.IsNullOrWhiteSpace(redirect) && !IsRedirect(redirect))
            {
                throw new InvalidOperationException($"The form '{name}' cannot be shown: Then go to '{redirect.Trim()}' is not an https:// or http:// address.");
            }

            var fields = PipelineForms.Parse(context.Get(Fields), out var error);
            var problems = fields is null ? [error!] : PipelineForms.Problems(fields);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"The form '{name}' cannot be shown: {string.Join(" ", problems)}");
            }

            return new FormStimulus(Webhook.Normalize(name!));
        }

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself.
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.SetResult(context.TryGetPipelineInput<string>(SubmissionInputKey, out var answers) ? answers : "{}");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
