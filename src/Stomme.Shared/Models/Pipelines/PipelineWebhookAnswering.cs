#nullable enable

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// A webhook answered from the pipeline (BL-229, n8n's Respond to Webhook): the names the editor and the problems panel need
    /// - the Webhook step's Answer input and the value that makes its caller wait, and the step that gives the answer.
    /// </summary>
    public static class PipelineWebhookAnswering
    {
        public const string WebhookStepType = "Stomme.Webhook";
        public const string RespondStepType = "Stomme.RespondToWebhook";
        public const string AnswerInput = "Answer";
        public const string FromThePipeline = "FromThePipeline";

        /// <summary>Whether the step is a Webhook, switched on, whose caller waits for an answer from the pipeline.</summary>
        public static bool Answers(PipelineGraphStep? step) =>
            step is { Type: WebhookStepType, Disabled: false } && step.Inputs is not null
            && step.Inputs.TryGetValue(AnswerInput, out var answer) && string.Equals(answer?.Trim(), FromThePipeline, System.StringComparison.OrdinalIgnoreCase);
    }
}
