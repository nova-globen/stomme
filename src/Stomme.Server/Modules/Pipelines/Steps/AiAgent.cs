#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Tenancy;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// BL-229 - n8n's AI Agent: the site's language model answers the prompt with the site's AI agent TOOLS at hand - the published
    /// pipelines that start with <see cref="AgentTool"/> (all of them, or the ones named). When the model calls a tool, its pipeline
    /// RUNS - the model's argument its trigger's result, the agent's run its parent - and the result of the last step the tool's run
    /// completes goes back to the model; then the model answers. The model is the one Ask AI uses: the site's, through
    /// <see cref="IAssistantModelAccess"/>, every request to it quota-gated and metered as the "pipelines" agent. Bounded: at most
    /// <see cref="MaxToolCalls"/> tool runs per answer (the model is told when it may call no more), and an agent reached through
    /// <see cref="MaxDepth"/> tools in a row may call none - so a tool that runs an agent that calls the tool ends. With a
    /// <see cref="MemoryKey"/> (n8n's Simple Memory) it remembers the conversation under that key - a customer, a ticket - and a
    /// later run with the same key continues it (<see cref="PipelineAgentMemory"/>).
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "AI", "Let the site's language model answer with tools: the site's 'AI agent tool' pipelines, which it may run. Quota-gated and metered like the assistant.", DisplayName = "AI agent")]
    public class AiAgent : DataStep
    {
        public const int MaxDepth = 3;

        public const int MostToolCalls = 20;

        [Input(UIHint = InputUIHints.MultiLine, Description = "What to ask or do. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Prompt { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "How the agent should work - its role, its rules, when to use which tool, the form of the answer (the system message).", Order = 2)]
        public Input<string?>? Instructions { get; set; }

        [Input(UIHint = PipelineInputHints.AgentTools, Description = "The tools it may call, one name per line - the 'AI agent tool' pipelines published on the site. Empty: every one.", Order = 3)]
        public Input<string?>? Tools { get; set; }

        [Input(DisplayName = "Most tool calls", Description = "The most tool runs for one answer (at most 20); past it the model is told to answer with what it has.", DefaultValue = 5, Order = 4)]
        public Input<int> MaxToolCalls { get; set; } = new(new Literal<int>(5));

        [Input(DisplayName = "Answer as", DefaultValue = AiAnswerFormat.Text, Description = "Text, or JSON - the step fails when the answer is not JSON.", Order = 5)]
        public Input<AiAnswerFormat> AnswerAs { get; set; } = new(new Literal<AiAnswerFormat>(AiAnswerFormat.Text));

        [Input(DisplayName = "Memory key", Description = "Remember the conversation under this key - e.g. {{ customer.id }} or a chat's id - so a later run with the same key continues it. Empty: no memory.", Order = 6)]
        public Input<string?>? MemoryKey { get; set; }

        [Input(DisplayName = "Messages remembered", Description = "How many earlier messages under the key the model sees (at most 50).", DefaultValue = 10, Order = 7)]
        public Input<int> MemoryMessages { get; set; } = new(new Literal<int>(10));

        [Output(DisplayName = "Tool calls", Description = "How many tool runs the answer took.")]
        public Output<int>? ToolCalls { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var prompt = TextOrLastResult(context, Prompt);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", "The step needs its 'Prompt' input, or a previous step's result to work on.");
            }

            var depth = context.TryGetPipelineInput<object>(AgentTool.DepthInputKey, out var given)
                && int.TryParse(Convert.ToString(given, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : 0;
            if (depth >= MaxDepth)
            {
                await FailAsync(context, "AI_TOO_DEEP", $"This agent was reached through {depth} tools in a row; an agent that deep may not run (at most {MaxDepth}).").ConfigureAwait(false);
                return;
            }

            if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false))
            {
                await FailAsync(context, "SITE_UNAVAILABLE", "The run has no site whose model it could ask.").ConfigureAwait(false);
                return;
            }

            var access = context.GetService<IAssistantModelAccess>();
            if (access is null)
            {
                await FailAsync(context, "AI_UNAVAILABLE", "The AI assistant module is not composed in this host.").ConfigureAwait(false);
                return;
            }

            // The site's tools, by name; the ones named, when some are.
            var stored = await context.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = AgentTool.StepTypeName }, context.CancellationToken).ConfigureAwait(false);
            var available = stored.Select(trigger => (Pipeline: trigger.PipelineDefinitionId, Tool: AgentTool.Read(trigger))).Where(pair => pair.Tool is not null)
                .GroupBy(pair => pair.Tool!.Tool, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            var named = Records.Lines(Tools?.GetOrDefault(context)).Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unknown = named.Where(name => available.All(pair => !string.Equals(pair.Tool!.Tool, name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                await FailAsync(context, "AI_TOOL_UNKNOWN", $"No AI agent tool named {string.Join(", ", unknown.Select(name => $"'{name}'"))} is published on the site.").ConfigureAwait(false);
                return;
            }

            // BL-229: a tool's run is a call from this pipeline, so a tool's pipeline that does not let this one call it is held to
            // that (Reliability/CallerPolicy.cs) - a tool named is refused, and of every tool it is left out, never offered.
            var definitions = context.GetRequiredService<Stomme.Engine.Pipelines.Management.IPipelineDefinitionStore>();
            var caller = Reliability.PipelineCallerPolicy.CallerOf(context.PipelineExecutionContext);
            var tools = new List<AgentToolStimulus>();
            var leftOut = new List<string>();
            foreach (var (pipeline, tool) in available.Where(pair => named.Count == 0 || named.Contains(pair.Tool!.Tool, StringComparer.OrdinalIgnoreCase)))
            {
                if (await Reliability.PipelineCallerPolicy.RefusalAsync(definitions, pipeline, caller, context.CancellationToken).ConfigureAwait(false) is not { } refusal)
                {
                    tools.Add(tool!);
                }
                else if (named.Count > 0)
                {
                    await FailAsync(context, "CALL_REFUSED", $"The AI agent tool '{tool!.Tool}' may not be used here: {refusal}").ConfigureAwait(false);
                    return;
                }
                else
                {
                    leftOut.Add(tool!.Tool);
                }
            }

            if (leftOut.Count > 0)
            {
                context.AddExecutionLogEntry("ToolsLeftOut", $"Not offered - their pipelines may not be called by this one: {string.Join(", ", leftOut.Select(name => $"'{name}'"))}.");
            }

            var most = Math.Clamp(MaxToolCalls.GetOrDefault(context), 0, MostToolCalls);
            var calls = 0;
            var functions = tools.Select(tool => (AITool)AIFunctionFactory.Create(
                async ([Description("What to give the tool - text, or JSON when its description asks for it.")] string request, CancellationToken cancellationToken) =>
                {
                    if (++calls > most)
                    {
                        return "No more tool calls are allowed for this answer - answer with what you have.";
                    }

                    return await CallAsync(context, tool, request, depth, cancellationToken).ConfigureAwait(false);
                },
                new AIFunctionFactoryOptions { Name = tool.Tool, Description = tool.Description })).ToList();

            var json = AnswerAs.GetOrDefault(context) == AiAnswerFormat.Json;
            var messages = new List<ChatMessage>();
            if (Instructions?.GetOrDefault(context) is { Length: > 0 } instructions)
            {
                messages.Add(new ChatMessage(ChatRole.System, instructions));
            }

            // The conversation so far, when it has a memory: the newest messages under the key, between the instructions and the prompt.
            var memoryKey = MemoryKey?.GetOrDefault(context)?.Trim();
            if (memoryKey is { Length: > PipelineAgentMemory.MaxKeyLength })
            {
                await FailAsync(context, "AI_MEMORY_KEY", $"A memory key is at most {PipelineAgentMemory.MaxKeyLength} characters.").ConfigureAwait(false);
                return;
            }

            var memory = string.IsNullOrEmpty(memoryKey) ? null : context.GetRequiredService<IKeyValueStore>();
            var tenant = context.GetService<ITenantAccessor>()?.TenantId;
            var pipelineId = context.PipelineExecutionContext.Pipeline.Identity.DefinitionId;
            if (memory is not null)
            {
                var remembered = await PipelineAgentMemory.ReadAsync(memory, tenant, pipelineId, memoryKey!, Math.Clamp(MemoryMessages.GetOrDefault(context), 0, PipelineAgentMemory.MaxKept), context.CancellationToken).ConfigureAwait(false);
                messages.AddRange(remembered.Select(message => new ChatMessage(message.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, message.Text)));
                context.AddExecutionLogEntry("Remembered", $"{remembered.Count} earlier message(s) under '{memoryKey}'.");
            }

            messages.Add(new ChatMessage(ChatRole.User, prompt));
            string answer;
            try
            {
                using var model = await access.OpenAsync(AskAi.Agent, context.CancellationToken).ConfigureAwait(false);
                // Every request the loop makes goes through the site's client - quota-gated and metered, each one.
                using var agent = new FunctionInvokingChatClient(model.Client) { MaximumIterationsPerRequest = most + 2 };
                var options = new ChatOptions
                {
                    Temperature = model.Temperature,
                    ResponseFormat = json ? ChatResponseFormat.Json : null,
                    Tools = functions.Count == 0 ? null : functions,
                };
                var response = await agent.GetResponseAsync(messages, options, context.CancellationToken).ConfigureAwait(false);
                answer = response.Text ?? string.Empty;
            }
            catch (AssistantUnavailableException ex)
            {
                await FailAsync(context, "AI_" + AskAi.Code(ex.Reason), ex.Message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync(context, "AI_FAILED", ex.Message).ConfigureAwait(false);
                return;
            }

            context.Set(ToolCalls, Math.Min(calls, most));
            if (memory is not null)
            {
                await PipelineAgentMemory.AppendAsync(memory, tenant, pipelineId, memoryKey!, [new("user", prompt), new("assistant", answer)], context.CancellationToken).ConfigureAwait(false);
            }

            if (json)
            {
                answer = AskAi.Unfenced(answer);
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

        // One tool call: the tool's pipeline started with the model's argument (this run its parent, the depth one more), run to its
        // end, and the result of the last step it completed - or, when it did not finish, what happened - for the model.
        private static async Task<string> CallAsync(StepExecutionContext context, AgentToolStimulus tool, string request, int depth, CancellationToken cancellationToken)
        {
            var sent = await context.GetRequiredService<IStimulusSender>().SendAsync(AgentTool.StepTypeName, tool, new StimulusMetadata
            {
                ParentPipelineInstanceId = context.PipelineExecutionContext.Id,
                CorrelationId = context.PipelineExecutionContext.CorrelationId,
                Input = new Dictionary<string, object>
                {
                    [AgentTool.RequestInputKey] = request ?? "",
                    [AgentTool.DepthInputKey] = (depth + 1).ToString(CultureInfo.InvariantCulture),
                },
            }, cancellationToken).ConfigureAwait(false);
            var run = sent.PipelineInstanceResponses.FirstOrDefault();
            if (run is null)
            {
                context.AddExecutionLogEntry("ToolCalled", $"Called '{tool.Tool}': it is not published any more.");
                return $"The tool '{tool.Tool}' is not available any more.";
            }

            context.AddExecutionLogEntry("ToolCalled", $"Called '{tool.Tool}' (run {run.PipelineInstanceId}): {run.SubStatus}.");
            if (run.Incidents.FirstOrDefault() is { } incident)
            {
                return $"The tool '{tool.Tool}' failed: {incident.Message}";
            }

            if (run.SubStatus != PipelineSubStatus.Finished)
            {
                return $"The tool '{tool.Tool}' did not finish (its run is {run.SubStatus}); it has no answer yet.";
            }

            return ToolAnswer(await PipelineRunResults.LastResultAsync(context.GetRequiredService<IStepExecutionStore>(), run.PipelineInstanceId, cancellationToken).ConfigureAwait(false));
        }

        // What goes back to the model: the answer as text (PipelineRunResults), or that there was none.
        internal static string ToolAnswer(object? value) => PipelineRunResults.Text(value) ?? "(the tool gave no result)";
    }
}
