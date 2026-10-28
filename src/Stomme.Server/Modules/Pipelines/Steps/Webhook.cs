#nullable enable
using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Models.Pipelines;
using Microsoft.AspNetCore.WebUtilities;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The stimulus a webhook trigger is indexed and fired by: the webhook's name on the site.</summary>
    public sealed record WebhookStimulus(string Name);

    /// <summary>
    /// The stimulus of a webhook whose caller waits for the pipeline's answer (BL-229, Respond to webhook) - a record of its own,
    /// because the engine hashes every property of a stimulus, a null one included: a flag on <see cref="WebhookStimulus"/>
    /// would have moved the hash of every webhook already published (the schedule's time zone met the same). And it has a
    /// property <see cref="WebhookStimulus"/> lacks, because the hash is of the properties ALONE, not the record's type: with
    /// <c>Name</c> only, both hashed alike and every call started each pipeline twice (found by the guards drive).
    /// </summary>
    public sealed record AnsweredWebhookStimulus(string Name, string AnsweredBy = "pipeline");

    /// <summary>When the caller of a webhook gets its answer.</summary>
    public enum WebhookAnswer
    {
        /// <summary>202 at once; the run goes on without the caller.</summary>
        Immediately,

        /// <summary>The caller waits for a Respond to webhook step, up to <c>Pipelines:Webhooks:AnswerWithinSeconds</c>, then 202.</summary>
        FromThePipeline,
    }

    /// <summary>
    /// An inbound webhook as an engine TRIGGER (BL-95 increment (e)): a published pipeline that starts with it is
    /// indexed by the engine under the webhook's name, and the receiver (<c>POST api/Pipelines/webhooks/{name}</c>,
    /// behind <c>Pipelines:Webhooks:Enabled</c>, HMAC-verified, rate-limited) fires it through the engine's stimulus
    /// dispatcher on the request's site. The result is the request body as text - a form-encoded one as a JSON object of
    /// its fields (<see cref="FormAsJson"/>); the content type and the name ride as pipeline input.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when the site's webhook receiver is called with this name.", DisplayName = "Webhook")]
    public class Webhook : Trigger<string>
    {
        public const string StepTypeName = PipelineWebhookAnswering.WebhookStepType;
        public const string PayloadInputKey = "__WebhookPayload";
        public const string ContentTypeInputKey = "__WebhookContentType";
        public const string NameInputKey = "__WebhookName";

        /// <summary>The waiting call's id, on a run whose caller waits for its answer (<see cref="RespondToWebhook"/>).</summary>
        public const string AnswerToInputKey = "__WebhookAnswerTo";

        [Input(DisplayName = "Name", Description = "The webhook's name: the last segment of its URL, api/Pipelines/webhooks/{name}. Letters, digits, '-', '_' and '.'.")]
        public Input<string> WebhookName { get; set; } = null!;

        [Input(Description = "Immediately: the caller gets 202 at once. FromThePipeline: it waits for a Respond to webhook step (up to Pipelines:Webhooks:AnswerWithinSeconds), then 202.", DefaultValue = WebhookAnswer.Immediately)]
        public Input<WebhookAnswer> Answer { get; set; } = new(new Literal<WebhookAnswer>(WebhookAnswer.Immediately));

        public static string Normalize(string name) => name.Trim().ToLowerInvariant();

        public static bool IsValidName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 100
            && Array.TrueForAll(name.Trim().ToCharArray(), c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        protected override object GetTriggerPayload(TriggerIndexingContext context)
        {
            var name = context.Get(WebhookName);
            if (!IsValidName(name))
            {
                throw new InvalidOperationException($"'{name}' is not a webhook name (letters, digits, '-', '_', '.', at most 100 characters).");
            }

            // A webhook that answers at once keeps the stimulus every webhook was published under.
            return context.Get(Answer) == WebhookAnswer.FromThePipeline ? new AnsweredWebhookStimulus(Normalize(name!)) : new WebhookStimulus(Normalize(name!));
        }

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself (measured - the run sat Suspended).
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            var payload = context.TryGetPipelineInput<string>(PayloadInputKey, out var body) ? body ?? "" : "";
            context.TryGetPipelineInput<string>(ContentTypeInputKey, out var contentType);
            context.SetResult(IsForm(contentType) ? FormAsJson(payload) : payload);
            await context.CompleteStepAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// BL-229 - a form-encoded body (<c>a=1&amp;b=2</c>) as n8n hands it on: a JSON object of its fields, each one's text, a
        /// field given more than once a list - so <c>{{ request.a }}</c> reads it as it reads a JSON body's field. (Found on UAT
        /// 2026-10-07: 10.18.0 accepted a signed form-encoded call, and the pipeline could read none of its fields.)
        /// </summary>
        public static string FormAsJson(string body)
        {
            var fields = new JsonObject();
            foreach (var (key, values) in QueryHelpers.ParseQuery(body))
            {
                fields[key] = values.Count == 1
                    ? JsonValue.Create(values[0] ?? "")
                    : new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value ?? "")).ToArray());
            }

            return fields.ToJsonString();
        }

        private static bool IsForm(string? contentType) =>
            MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && string.Equals(parsed.MediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }
}
