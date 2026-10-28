#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The Pipeline Designer (BL-229, n8n's AI workflow builder): the pipeline on the editor's canvas changed as its author asks.
    /// The guardrail first - the per-person rate and the instruction screen the assistant's every request passes - so a refused
    /// request reaches no model; then the SITE's model (quota-gated and metered like every agent) over the workbench's tools, until
    /// it finishes, its rounds run out, or its time does. It PROPOSES (D113): the workbench holds a copy, and nothing is stored.
    /// </summary>
    public sealed class PipelineGraphDesignerAgent(IAssistantModelAccess models, IAssistantGuardrail guardrail, IAssistantCaller caller) : IPipelineGraphDesigner
    {
        public const string AgentName = "pipeline-designer";

        /// <summary>
        /// The tool rounds one request may take - each a metered call. A request reads the pipeline, finds and describes a few step
        /// types, adds and connects them and checks: a dozen or so, the tools called side by side where the model can.
        /// </summary>
        public const int MaxRounds = 24;

        /// <summary>A request ends here, whatever the model is doing: a page waits for it, and the edge of a site gives up near 100 seconds.</summary>
        public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(90);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => models.IsAvailableAsync(cancellationToken);

        public async Task<string> DesignAsync(PipelineGraphWorkbench workbench, string instruction, IReadOnlyList<PipelineAiTurn>? earlier = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(workbench);
            ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeLimit);
            try
            {
                // The conversation so far, then what is asked now - every word the author wrote screened, the earlier too: they
                // come back from the page, so what was screened once is screened again.
                var turns = (earlier ?? Array.Empty<PipelineAiTurn>()).Where(turn => !string.IsNullOrWhiteSpace(turn?.Asked)).ToList();
                var conversation = turns
                    .SelectMany(turn => new[] { new AssistantMessage(ChatRole.User, turn.Asked), new AssistantMessage(ChatRole.Assistant, turn.Said ?? string.Empty) })
                    .Append(new AssistantMessage(ChatRole.User, instruction))
                    .ToList();
                var (siteId, _, userId) = await caller.ResolveAsync(limit.Token);
                await guardrail.EnsureAllowedAsync(new AssistantRequest(Guid.NewGuid(), conversation), siteId, userId, limit.Token);

                using var model = await models.OpenAsync(AgentName, limit.Token);
                var client = AssistantTurn.WithTools(model.Client, MaxRounds);
                var options = new ChatOptions { ModelId = model.Model, Temperature = model.Temperature, Tools = [.. workbench.AsTools()] };
                var messages = new List<ChatMessage> { new(ChatRole.System, PipelineGraphWorkbench.Instructions) };
                foreach (var turn in turns)
                {
                    messages.Add(new ChatMessage(ChatRole.User, "The author asked:\n" + turn.Asked.Trim()));
                    messages.Add(new ChatMessage(ChatRole.Assistant, string.IsNullOrWhiteSpace(turn.Said) ? "(no answer)" : turn.Said.Trim()));
                }

                messages.Add(new ChatMessage(ChatRole.User, "The author asks:\n" + instruction.Trim()));
                var response = await client.GetResponseAsync(messages, options, limit.Token);
                return workbench.Summary ?? response.Text?.Trim() ?? string.Empty;
            }
            catch (AssistantGuardrailException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (AssistantUnavailableException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The AI took longer than {TimeLimit.TotalSeconds:0} seconds - ask for a smaller change.", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                // A model call that failed - the provider's error, a connection gone - is said to the author, as Ask AI says it
                // (AI_FAILED), never a 500.
                throw new InvalidOperationException("The AI could not finish: " + ex.Message, ex);
            }
        }
    }
}
