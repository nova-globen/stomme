#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The stimulus an AI agent tool is indexed and called by: its name and what it does - both in the stored trigger, so an agent
    /// lists the site's tools from the trigger store alone and calls one with the very stimulus it was indexed under.
    /// </summary>
    public sealed record AgentToolStimulus(string Tool, string Description);

    /// <summary>
    /// n8n's "Call n8n Workflow Tool": a pipeline that starts with this step is a TOOL the site's <see cref="AiAgent"/> steps
    /// may call. The model sees its name and description; what the model gives it is this step's result, and the tool's answer is
    /// the result of the last step its run completes. Published like any trigger; a draft is no tool.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when an AI agent on the site calls it as a tool; what the agent gives it is the result.", DisplayName = "AI agent tool")]
    public class AgentTool : Trigger<string>
    {
        public const string StepTypeName = "Stomme.AgentTool";

        /// <summary>The pipeline input carrying what the model gave the tool.</summary>
        public const string RequestInputKey = "__AgentToolRequest";

        /// <summary>The pipeline input carrying how many agents deep the call is - so an agent calling a tool that calls an agent ends.</summary>
        public const string DepthInputKey = "__AgentDepth";

        [Input(DisplayName = "Name", Description = "The tool's name as the model sees it: letters, digits, '_' and '-', at most 64 - e.g. 'find_order'.", Order = 1)]
        public Input<string> ToolName { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "What the tool does, when to use it and what to give it - the model reads this to decide whether to call it.", Order = 2)]
        public Input<string> Description { get; set; } = null!;

        public static bool IsValidName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 64 && name.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

        protected override object GetTriggerPayload(TriggerIndexingContext context)
        {
            var name = context.Get(ToolName);
            if (!IsValidName(name))
            {
                throw new InvalidOperationException($"'{name}' is not a tool name (letters, digits, '_' and '-', at most 64).");
            }

            var description = context.Get(Description);
            if (string.IsNullOrWhiteSpace(description))
            {
                throw new InvalidOperationException($"The tool '{name!.Trim()}' needs a description - the model reads it to decide when to call it.");
            }

            return new AgentToolStimulus(name!.Trim(), description.Trim());
        }

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself.
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.SetResult(context.TryGetPipelineInput<string>(RequestInputKey, out var request) ? request : "");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }

        /// <summary>A stored tool trigger's name and description; null for one that holds neither.</summary>
        public static AgentToolStimulus? Read(StoredTrigger trigger) => trigger.Payload switch
        {
            AgentToolStimulus stimulus => stimulus,
            JsonElement { ValueKind: JsonValueKind.Object } element when Text(element, nameof(AgentToolStimulus.Tool)) is { Length: > 0 } tool =>
                new AgentToolStimulus(tool, Text(element, nameof(AgentToolStimulus.Description)) ?? ""),
            _ => null,
        };

        private static string? Text(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            return null;
        }
    }
}
