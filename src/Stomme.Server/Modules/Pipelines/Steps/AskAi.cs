#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What an <see cref="AskAi"/> answer is: text as it comes, or JSON the step checks.</summary>
    public enum AiAnswerFormat
    {
        Text,
        Json,
    }

    /// <summary>
    /// Ask the site's language model (BL-229 - n8n's AI node, on Stomme's own model seam): the prompt - with the
    /// instructions as the system message - goes to the model the site configured for its assistant, through
    /// <see cref="IAssistantModelAccess"/>: the site's provider and endpoint, its key in the site's secret store, the site's egress,
    /// and every call quota-gated before it is made and metered after it, as the <see cref="Agent"/> agent beside the assistant's
    /// own. The answer is the result - text, or JSON the step checks parses (a fenced block is unwrapped). BL-229, n8n's
    /// Structured Output Parser and its Auto-fixing one: an Answer shape - an example of the answer, or a JSON Schema
    /// (<see cref="AiAnswerShape"/>) - is told the model, and the answer held to it; an answer that misses it (or is not JSON) is
    /// put to the model once more with what was wrong, and a second miss is <c>AI_WRONG_SHAPE</c>, naming where.
    /// </summary>
    /// <remarks>
    /// A refusal - the assistant switched off for the installation or the site, no model configured, the key unreadable, the
    /// site's AI quota spent, an endpoint the site's egress refuses - is <c>AI_&lt;REASON&gt;</c> with the seam's words (they name
    /// a setting, never a value); a model that fails is <c>AI_FAILED</c>; an answer that should be JSON and is not,
    /// <c>AI_NOT_JSON</c>. Each honours <c>On failure</c>, and a step's Failed path.
    /// </remarks>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "AI", "Ask the site's language model; the answer is the result. Quota-gated and metered like the assistant.", DisplayName = "Ask AI")]
    public class AskAi : DataStep
    {
        /// <summary>The name the site's AI usage records these calls under.</summary>
        public const string Agent = "pipelines";

        [Input(UIHint = InputUIHints.MultiLine, Description = "What to ask. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Prompt { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "How the model should answer - its role, its rules, the form of the answer (the system message).", Order = 2)]
        public Input<string?>? Instructions { get; set; }

        [Input(DisplayName = "Answer as", DefaultValue = AiAnswerFormat.Text, Description = "Text, or JSON - the step fails when the answer is not JSON.", Order = 3)]
        public Input<AiAnswerFormat> AnswerAs { get; set; } = new(new Literal<AiAnswerFormat>(AiAnswerFormat.Text));

        [Input(DisplayName = "Answer shape", UIHint = InputUIHints.MultiLine, Description = "An example of the answer - {\"name\": \"\", \"total\": 0, \"items\": [{\"sku\": \"\"}]} - or a JSON Schema. The model is told it and the answer held to it; a model that misses it is asked once more with what was wrong. Implies JSON.", Order = 4)]
        public Input<string?>? Shape { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var prompt = TextOrLastResult(context, Prompt);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", "The step needs its 'Prompt' input, or a previous step's result to ask about.");
            }

            System.Text.Json.Nodes.JsonNode? shape = null;
            if (Shape?.GetOrDefault(context) is { Length: > 0 } shapeText && !string.IsNullOrWhiteSpace(shapeText))
            {
                shape = AiAnswerShape.Read(shapeText, out var shapeProblem)
                    ?? throw new FaultException("INPUT_INVALID", "Pipelines", "Configuration", shapeProblem!);
            }

            var json = AnswerAs.GetOrDefault(context) == AiAnswerFormat.Json || shape is not null;
            var messages = new List<ChatMessage>();
            var system = Instructions?.GetOrDefault(context) is { Length: > 0 } instructions ? instructions : null;
            if (shape is not null)
            {
                system = (system is null ? "" : system + "\n\n") + AiAnswerShape.Instruction(shape);
            }

            if (system is not null)
            {
                messages.Add(new ChatMessage(ChatRole.System, system));
            }

            messages.Add(new ChatMessage(ChatRole.User, prompt));

            var (asked, code, error) = await AiModelCall.AskAsync(context, messages, json).ConfigureAwait(false);
            if (asked is null)
            {
                await FailAsync(context, code!, error!).ConfigureAwait(false);
                return;
            }

            var answer = asked;

            if (shape is not null)
            {
                var problems = Misfits(answer, shape);
                if (problems.Count > 0)
                {
                    // n8n's Auto-fixing Output Parser: the model asked once more, shown its answer and what was wrong with it.
                    context.AddExecutionLogEntry("Asked again", "The answer did not fit the shape: " + string.Join(" ", problems));
                    messages.Add(new ChatMessage(ChatRole.Assistant, answer));
                    messages.Add(new ChatMessage(ChatRole.User, "That answer does not fit the shape:\n- " + string.Join("\n- ", problems)
                        + "\nAnswer again with JSON of the shape alone."));
                    (asked, code, error) = await AiModelCall.AskAsync(context, messages, json).ConfigureAwait(false);
                    if (asked is null)
                    {
                        await FailAsync(context, code!, error!).ConfigureAwait(false);
                        return;
                    }

                    answer = asked;
                    problems = Misfits(answer, shape);
                    if (problems.Count > 0)
                    {
                        await FailAsync(context, "AI_WRONG_SHAPE", "The model's answer does not fit the shape, asked twice: " + string.Join(" ", problems)).ConfigureAwait(false);
                        return;
                    }
                }

                await SucceedAsync(context, Unfenced(answer)).ConfigureAwait(false);
                return;
            }

            if (json)
            {
                answer = Unfenced(answer);
                try
                {
                    using var _ = JsonDocument.Parse(answer);
                }
                catch (JsonException ex)
                {
                    await FailAsync(context, "AI_NOT_JSON", "The model's answer is not JSON: " + ex.Message).ConfigureAwait(false);
                    return;
                }
            }

            await SucceedAsync(context, answer).ConfigureAwait(false);
        }

        // Where an answer does not fit the shape - not JSON at all being the one problem then.
        private static List<string> Misfits(string answer, System.Text.Json.Nodes.JsonNode shape)
        {
            try
            {
                return AiAnswerShape.Problems(System.Text.Json.Nodes.JsonNode.Parse(Unfenced(answer)), shape);
            }
            catch (JsonException ex)
            {
                return new List<string> { "it is not JSON: " + ex.Message };
            }
        }

        // QuotaExceeded -> QUOTA_EXCEEDED: the reason as the step's failure code.
        internal static string Code(AssistantUnavailableReason reason) =>
            System.Text.RegularExpressions.Regex.Replace(reason.ToString(), "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant();

        // A model asked for JSON often wraps it in a fenced block anyway: ```json ... ```.
        internal static string Unfenced(string answer)
        {
            var text = answer.Trim();
            if (!text.StartsWith("```", StringComparison.Ordinal))
            {
                return text;
            }

            var firstLine = text.IndexOf('\n');
            var last = text.LastIndexOf("```", StringComparison.Ordinal);
            return firstLine > 0 && last > firstLine ? text[(firstLine + 1)..last].Trim() : text;
        }
    }
}
