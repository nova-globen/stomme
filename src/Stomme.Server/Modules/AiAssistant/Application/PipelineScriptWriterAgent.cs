#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Pipelines.Application;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The Script Writer (BL-229, n8n's "Ask AI" in its Code node): a Run script step's script written as its author asks. The
    /// guardrail first - the per-person rate and the instruction screen - so a refused request reaches no model; then ONE call to
    /// the SITE's model (quota-gated and metered like every agent), no tools: it is told the script's world and what the script can
    /// see (<see cref="PipelineScriptPrompt"/>), and answers with a script. It proposes: nothing is stored and nothing runs.
    /// </summary>
    public sealed class PipelineScriptWriterAgent(IAssistantModelAccess models, IAssistantGuardrail guardrail, IAssistantCaller caller) : IPipelineScriptWriter
    {
        public const string AgentName = "pipeline-script-writer";

        /// <summary>A request ends here, whatever the model is doing: a page waits for it, and the edge of a site gives up near 100 seconds.</summary>
        public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(60);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => models.IsAvailableAsync(cancellationToken);

        public async Task<string> WriteAsync(PipelineScriptAsk ask, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(ask);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeLimit);
            try
            {
                // What the author wrote is screened; the script and what it can see go to the model as data, said so.
                var (siteId, _, userId) = await caller.ResolveAsync(limit.Token);
                await guardrail.EnsureAllowedAsync(new AssistantRequest(Guid.NewGuid(), [new AssistantMessage(ChatRole.User, ask.Instruction)]), siteId, userId, limit.Token);

                using var model = await models.OpenAsync(AgentName, limit.Token);
                var options = new ChatOptions { ModelId = model.Model, Temperature = model.Temperature };
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, PipelineScriptPrompt.Instructions(ask.Style)),
                    new(ChatRole.User, PipelineScriptPrompt.Message(ask)),
                };
                var response = await model.Client.GetResponseAsync(messages, options, limit.Token);
                return response.Text ?? string.Empty;
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
                throw new InvalidOperationException($"The AI took longer than {TimeLimit.TotalSeconds:0} seconds - ask for a smaller script.", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                // A model call that failed - the provider's error, a connection gone - is said to the author, never a 500. The editor
                // says "The AI did not write it:" before it, so this names the cause (found driving it: the same words twice).
                throw new InvalidOperationException("The site's model could not be asked - " + ex.Message, ex);
            }
        }
    }
}
