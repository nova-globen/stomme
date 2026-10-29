#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Modules.Pipelines.Steps;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The form submissions waiting to be taken on to their next page (n8n's multi-page forms - its Form node's next page):
    /// one entry per submission, in this process - the dispatcher runs the pipeline here, so the next page is known here, as
    /// Respond to webhook's answer is (<see cref="PipelineWebhookAnswers"/>). The id rides with the run's input for that one burst
    /// (<see cref="InputKey"/>); a <see cref="WaitForForm"/> with Next page ticked answers it with the run's form token once the run
    /// - its bookmark with it - is stored, and a burst that ends without one answers it with none
    /// (<see cref="FormContinuationOnCommit"/>), so the person is thanked at once, not after the wait.
    /// </summary>
    public sealed class PipelineFormContinuations
    {
        /// <summary>Where a submission's id rides in the run's input - the start's, or the resumption's.</summary>
        public const string InputKey = "__FormContinue";

        private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _waiting = new(StringComparer.Ordinal);

        /// <summary>A new submission waiting: its id, which rides with the run, and the token of the next page's form - null for none.</summary>
        public (string Id, Task<string?> Next) Register()
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var next = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = next;
            return (id, next.Task);
        }

        /// <summary>Takes the submission on: to the form of the run with the token, or - null - to the thank-you. False when nothing waits for it any more.</summary>
        public bool TryContinue(string? id, string? token) =>
            id is not null && _waiting.TryRemove(id, out var waiting) && waiting.TrySetResult(token);

        /// <summary>Stops waiting for the submission.</summary>
        public void Forget(string id) => _waiting.TryRemove(id, out _);

        /// <summary>The submission's id a run's input carries; null when it carries none.</summary>
        public static string? IdOf(IDictionary<string, object>? input) =>
            input is not null && input.TryGetValue(InputKey, out var value) ? value switch
            {
                string text when text.Length > 0 => text,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
                _ => null,
            } : null;

        /// <summary>
        /// Sends a submission on and, when a next page can follow (<paramref name="leadsOn"/>), waits up to <paramref name="within"/>
        /// for the run to reach it: the next page's token, or null - none came, or not in time (the person is thanked, and the run
        /// goes on). A form no next page can follow is sent on and not waited for.
        /// </summary>
        public async Task<string?> SendAsync(bool leadsOn, TimeSpan within, Func<string?, Task> send, CancellationToken cancellationToken)
        {
            if (!leadsOn)
            {
                await send(null).ConfigureAwait(false);
                return null;
            }

            var (id, next) = Register();
            try
            {
                await send(id).ConfigureAwait(false);
                return await next.WaitAsync(within, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                Forget(id);
            }
        }
    }

    /// <summary>
    /// A burst that carried a form submission and ended without reaching a next page answers it with none: the person is
    /// thanked as soon as the run has nothing more to show them. A next page reached in the burst answered first, from the deferred
    /// task that runs before this notification; only a run whose pipeline has a next page answers - several pipelines may share a
    /// form's name, and one with no next page must not thank the person before the one that has one shows it.
    /// </summary>
    public sealed class FormContinuationOnCommit(PipelineFormContinuations continuations) : INotificationHandler<PipelineStateCommitted>
    {
        public Task HandleAsync(PipelineStateCommitted notification, CancellationToken cancellationToken)
        {
            if (notification.PipelineState.SubStatus is not (PipelineSubStatus.Executing or PipelineSubStatus.Pending)
                && PipelineFormContinuations.IdOf(notification.PipelineExecutionContext.Input) is { } id
                && WaitForForm.LeadsOn(notification.PipelineExecutionContext))
            {
                continuations.TryContinue(id, null);
            }

            return Task.CompletedTask;
        }
    }
}
