#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Classify text (BL-229, n8n's Text Classifier): the site's language model puts a text in ONE of the author's categories, and
    /// the run goes on through that category's outcome - the categories are the step's ports, as a Fork's branches are (the same
    /// dynamic-outcomes input, <see cref="Branches"/>). A category's description, when given, tells the model what belongs in it.
    /// The text passes through as the result, the category is an output; an answer that is none of the categories fails the step
    /// (<c>AI_NO_CATEGORY</c>) - add a category such as "Other" for what fits nowhere. The call is Ask AI's: the site's model,
    /// quota-gated and metered (<see cref="AiModelCall"/>).
    /// </summary>
    [FlowNode(FailedOutcome)]
    [Step("Stomme", "AI", "Have the site's language model put a text in one of your categories; the run goes on through that category's port.", DisplayName = "Classify text")]
    public class ClassifyText : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The text to classify. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Text { get; set; }

        /// <summary>The categories - each an outcome port; the input a Fork's branches use, so the editor and the codec treat them alike.</summary>
        [Input(DisplayName = "Categories", Description = "The categories, one per line - each is a port the run goes on through.", UIHint = InputUIHints.DynamicOutcomes, Order = 2)]
        public Input<ICollection<string>?> Branches { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "What belongs in each category, one 'category: what belongs in it' per line - optional, but it helps the model.", Order = 3)]
        public Input<string?>? Descriptions { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "Anything more the model should know.", Order = 4)]
        public Input<string?>? Instructions { get; set; }

        [Output(Description = "The category the text was put in.")]
        public Output<string>? Category { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var text = TextOrLastResult(context, Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", "The step needs its 'Text' input, or a previous step's result to classify.");
            }

            var categories = (Branches?.GetOrDefault(context) ?? Array.Empty<string>()).Select(category => category?.Trim() ?? "").Where(category => category.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (categories.Count == 0)
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", "The step needs its categories - one per line, each a port.");
            }

            var descriptions = Described(Descriptions?.GetOrDefault(context));
            var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt(categories, descriptions, Instructions?.GetOrDefault(context))), new(ChatRole.User, text) };
            var (answer, code, error) = await AiModelCall.AskAsync(context, messages, json: true).ConfigureAwait(false);
            if (answer is null)
            {
                await FailAsync(context, code!, error!).ConfigureAwait(false);
                return;
            }

            var chosen = Chosen(answer, categories);
            if (chosen is null)
            {
                await FailAsync(context, "AI_NO_CATEGORY", $"The model answered '{Truncate(answer)}', which is none of: {string.Join(", ", categories)}.").ConfigureAwait(false);
                return;
            }

            context.Set(Category, chosen);
            context.AddExecutionLogEntry("Classified", $"Put in '{chosen}'.");
            await SucceedAsync(context, text, chosen).ConfigureAwait(false);
        }

        /// <summary>The category the answer names - <c>{"category": "..."}</c>, or the bare name - matched whatever its case; null for none.</summary>
        public static string? Chosen(string answer, IReadOnlyList<string> categories)
        {
            var named = AiModelCall.Object(answer, out _) is { } given
                ? PipelineSiteTables.Text(given.FirstOrDefault(property => string.Equals(property.Key, "category", StringComparison.OrdinalIgnoreCase)).Value)
                : AskAi.Unfenced(answer).Trim().Trim('"', '\'', '.');
            return categories.FirstOrDefault(category => string.Equals(category, named.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, string> Described(string? text)
        {
            var described = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                var at = line.IndexOf(':');
                if (at > 0)
                {
                    described[line[..at].Trim()] = line[(at + 1)..].Trim();
                }
            }

            return described;
        }

        private static string SystemPrompt(IReadOnlyList<string> categories, IReadOnlyDictionary<string, string> descriptions, string? instructions)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine("You classify the text the user gives into exactly ONE of these categories:");
            foreach (var category in categories)
            {
                prompt.Append("- \"").Append(category).Append('"');
                if (descriptions.TryGetValue(category, out var description) && description.Length > 0)
                {
                    prompt.Append(": ").Append(description);
                }

                prompt.AppendLine();
            }

            prompt.AppendLine("Answer with ONE JSON object and nothing else: {\"category\": \"<the category's name, exactly as written above>\"}.");
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                prompt.AppendLine().AppendLine(instructions.Trim());
            }

            return prompt.ToString();
        }

        private static string Truncate(string text) => text.Length > 80 ? text[..80] + "..." : text;
    }
}
