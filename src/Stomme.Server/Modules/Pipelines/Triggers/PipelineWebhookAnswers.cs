#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>What a pipeline answers a webhook call with (Respond to webhook).</summary>
    public sealed record PipelineWebhookAnswer(int StatusCode, string ContentType, string Body)
    {
        private static readonly IReadOnlyList<(string Name, string Value)> NoHeaders = Array.Empty<(string Name, string Value)>();

        /// <summary>BL-229: the headers the caller gets besides the content type - none by default.</summary>
        public IReadOnlyList<(string Name, string Value)> Headers { get; init; } = NoHeaders;

        /// <summary>BL-229: a file's bytes, answered instead of the body; null for a body.</summary>
        public byte[]? File { get; init; }

        /// <summary>BL-229: with a file, the name the caller saves it under (an attachment); null shows it inline.</summary>
        public string? DownloadName { get; init; }
    }

    /// <summary>
    /// The webhook calls waiting for their pipeline's answer (n8n's Respond to Webhook): one entry per call, in this
    /// process - the dispatcher runs the pipeline here, so its Respond to webhook step answers here too. A call is waited for
    /// only while the receiver waits; an answer to a call no longer waited for is dropped, and says so.
    /// </summary>
    public sealed class PipelineWebhookAnswers
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<PipelineWebhookAnswer>> _waiting = new(StringComparer.Ordinal);

        /// <summary>A new call waiting: its id, which rides with the run, and what it is answered with.</summary>
        public (string Id, Task<PipelineWebhookAnswer> Answer) Register()
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var answer = new TaskCompletionSource<PipelineWebhookAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = answer;
            return (id, answer.Task);
        }

        /// <summary>Answers the call; false when nothing waits for it any more (it timed out, or was answered).</summary>
        public bool TryAnswer(string? id, PipelineWebhookAnswer answer) =>
            id is not null && _waiting.TryRemove(id, out var waiting) && waiting.TrySetResult(answer);

        /// <summary>Stops waiting for the call.</summary>
        public void Forget(string id) => _waiting.TryRemove(id, out _);
    }

    /// <summary>
    /// A webhook call handed to its pipelines (the receiver's half, after the guards): every pipeline listening on the name is
    /// started; when one of them answers from the pipeline (its Webhook set so - <see cref="AnsweredWebhookStimulus"/>), the
    /// call waits for that answer up to <c>Pipelines:Webhooks:AnswerWithinSeconds</c>. Null: answer 202 at once, or nothing
    /// answered in time.
    /// </summary>
    public sealed class PipelineWebhookReceiver(
        IStimulusDispatcher dispatcher,
        ITriggerStore triggers,
        IStimulusHasher hasher,
        PipelineWebhookAnswers answers,
        IOptions<PipelineTriggerOptions> options)
    {
        public async Task<PipelineWebhookAnswer?> ReceiveAsync(string name, string body, string? contentType, string? correlationId, CancellationToken cancellationToken)
        {
            var input = new Dictionary<string, object>
            {
                [Webhook.PayloadInputKey] = body,
                [Webhook.ContentTypeInputKey] = contentType ?? "",
                [Webhook.NameInputKey] = name,
            };

            // The pipelines that answer at once.
            await SendAsync(new WebhookStimulus(name), input, correlationId, cancellationToken).ConfigureAwait(false);

            var answered = new AnsweredWebhookStimulus(name);
            var listening = await triggers.FindAsync(new TriggerFilter { Name = Webhook.StepTypeName, Hash = hasher.Hash(Webhook.StepTypeName, answered) }, cancellationToken).ConfigureAwait(false);
            if (listening is null)
            {
                return null;
            }

            var (id, answer) = answers.Register();
            try
            {
                await SendAsync(answered, new Dictionary<string, object>(input) { [Webhook.AnswerToInputKey] = id }, correlationId, cancellationToken).ConfigureAwait(false);
                var within = TimeSpan.FromSeconds(Math.Clamp(options.Value.Webhooks.AnswerWithinSeconds, 1, 120));
                return await answer.WaitAsync(within, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null; // the run goes on; the caller is told it was accepted
            }
            finally
            {
                answers.Forget(id);
            }
        }

        private Task SendAsync(object stimulus, IDictionary<string, object> input, string? correlationId, CancellationToken cancellationToken) =>
            dispatcher.SendAsync(new DispatchStimulusRequest
            {
                StepTypeName = Webhook.StepTypeName,
                Stimulus = stimulus,
                Metadata = new StimulusMetadata { CorrelationId = correlationId, Input = input },
            }, cancellationToken);
    }
}
