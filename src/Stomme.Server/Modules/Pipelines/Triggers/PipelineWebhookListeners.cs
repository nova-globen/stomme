#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// The editor LISTENING for one call to a webhook (BL-229 increment 10, n8n's "listen for test event"): an author waits, the
    /// next AUTHENTIC call the receiver accepts for that site and name - signed, admitted, not a repeat - is handed to every
    /// listener of it as well as dispatched as always, and the author sees what it sent. In-process, like the collaboration gate:
    /// on several nodes a call reaches the listeners of the node that received it. A few listeners per name at most, for at most
    /// <see cref="MaxWait"/> each.
    /// </summary>
    public sealed class PipelineWebhookListeners
    {
        public const int MaxPerName = 8;

        public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

        private readonly object _gate = new();
        private readonly Dictionary<string, List<TaskCompletionSource<WebhookCallView>>> _waiting = new(StringComparer.Ordinal);

        /// <summary>The key of a site's webhook name: its tenant and site, then the normalised name.</summary>
        public static string Key(int tenantId, int siteId, string name) => $"{tenantId}:{siteId}:{name}";

        /// <summary>The next call to <paramref name="key"/> within <paramref name="wait"/> (capped), or one saying none came.</summary>
        /// <exception cref="InvalidOperationException">Already <see cref="MaxPerName"/> listeners on that name.</exception>
        public async Task<WebhookCallView> ListenAsync(string key, TimeSpan wait, CancellationToken ct)
        {
            var call = new TaskCompletionSource<WebhookCallView>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (!_waiting.TryGetValue(key, out var listeners))
                {
                    _waiting[key] = listeners = new List<TaskCompletionSource<WebhookCallView>>();
                }

                if (listeners.Count >= MaxPerName)
                {
                    throw new InvalidOperationException($"{MaxPerName} editors are already listening to this webhook; try again when one is done.");
                }

                listeners.Add(call);
            }

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(wait <= TimeSpan.Zero || wait > MaxWait ? MaxWait : wait);
                using var registration = limit.Token.Register(() => call.TrySetResult(new WebhookCallView { Received = false }));
                return await call.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_waiting.TryGetValue(key, out var listeners) && listeners.Remove(call) && listeners.Count == 0)
                    {
                        _waiting.Remove(key);
                    }
                }
            }
        }

        /// <summary>Hands a received call to everyone listening to <paramref name="key"/>; how many were.</summary>
        public int Offer(string key, DateTime receivedAtUtc, string? contentType, string body)
        {
            List<TaskCompletionSource<WebhookCallView>> listeners;
            lock (_gate)
            {
                if (!_waiting.Remove(key, out var waiting))
                {
                    return 0;
                }

                listeners = waiting;
            }

            var truncated = body.Length > WebhookCallView.MaxBodyChars;
            foreach (var listener in listeners)
            {
                listener.TrySetResult(new WebhookCallView
                {
                    Received = true,
                    ReceivedAtUtc = receivedAtUtc,
                    ContentType = contentType ?? "",
                    Body = truncated ? body[..WebhookCallView.MaxBodyChars] : body,
                    Truncated = truncated,
                });
            }

            return listeners.Count;
        }
    }
}
