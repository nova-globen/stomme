#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.Pipelines.Tenancy;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// One call to the site's language model for a pipeline step: the site resolved for the run, the model opened as
    /// the "pipelines" agent through <see cref="IAssistantModelAccess"/> - quota-gated before, metered after, as Ask AI's - and the
    /// answer, or the failure's code and words. Ask AI, Extract information and Classify text share it.
    /// </summary>
    internal static class AiModelCall
    {
        /// <summary>The answer; or, when there is none, the failure as a step reports it (<c>AI_&lt;REASON&gt;</c>, <c>AI_FAILED</c>).</summary>
        public static async Task<(string? Answer, string? Code, string? Error)> AskAsync(StepExecutionContext context, IList<ChatMessage> messages, bool json)
        {
            if (!await StommeSiteScope.EnsureSiteAsync(context).ConfigureAwait(false))
            {
                return (null, "SITE_UNAVAILABLE", "The run has no site whose model it could ask.");
            }

            var access = context.GetService<IAssistantModelAccess>();
            if (access is null)
            {
                return (null, "AI_UNAVAILABLE", "The AI assistant module is not composed in this host.");
            }

            try
            {
                using var model = await access.OpenAsync(AskAi.Agent, context.CancellationToken).ConfigureAwait(false);
                var options = new ChatOptions { Temperature = model.Temperature, ResponseFormat = json ? ChatResponseFormat.Json : null };
                var response = await model.Client.GetResponseAsync(messages, options, context.CancellationToken).ConfigureAwait(false);
                return (response.Text ?? string.Empty, null, null);
            }
            catch (AssistantUnavailableException ex)
            {
                return (null, "AI_" + AskAi.Code(ex.Reason), ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, "AI_FAILED", ex.Message);
            }
        }

        /// <summary>A JSON object the model answered (a fenced block unwrapped); null, with the reason, when it is none.</summary>
        public static System.Text.Json.Nodes.JsonObject? Object(string answer, out string? error)
        {
            error = null;
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(AskAi.Unfenced(answer)) is System.Text.Json.Nodes.JsonObject found)
                {
                    return found;
                }

                error = "The model's answer is JSON but not an object.";
            }
            catch (JsonException ex)
            {
                error = "The model's answer is not JSON: " + ex.Message;
            }

            return null;
        }
    }
}
