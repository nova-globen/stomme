#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// Retries an HTTP 429 (and 503) answer, honouring Retry-After when present and falling back to a short exponential
    /// backoff when absent; retries are bounded and delays capped; any other status passes through untouched (BL-96
    /// increment 3, lifted from the predecessor platform's <c>RetryAfterHandler</c>). The port is a policy AROUND a send rather than a
    /// handler INSIDE a client, because the client is the host's: a connector cannot wrap the egress's handler
    /// chain, so it asks the policy to send for it, handing a factory - a request message cannot be sent twice, and a
    /// throttled attempt is disposed before the next one is built. Safe for a request whose content can be rebuilt:
    /// the Graph and ARM GETs, the token, GraphQL and SOAP POSTs all qualify.
    /// </summary>
    public sealed class RetryAfterPolicy
    {
        private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

        private readonly int maxRetries;
        private readonly TimeSpan maxDelay;
        private readonly TimeProvider timeProvider;

        public RetryAfterPolicy(int maxRetries = 3, TimeSpan? maxDelay = null, TimeProvider? timeProvider = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
            this.maxRetries = maxRetries;
            this.maxDelay = maxDelay ?? DefaultMaxDelay;
            this.timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>The policy every connector of the family uses: three retries, delays capped at thirty seconds.</summary>
        public static RetryAfterPolicy Default { get; } = new();

        /// <summary>Test seam: replaces the real wait so retry timing is assertable without sleeping.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        /// <summary>Sends a fresh request from <paramref name="request"/> until the answer is not throttled or the retries are spent; the caller owns the returned response.</summary>
        /// <param name="completion">When the answer is handed back: its whole body read (the default), or its headers only, for a body
        /// streamed somewhere as it comes - a download (S3) - which the caller reads and disposes.</param>
        public async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> request, CancellationToken cancellationToken = default,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(request);
            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage response;
                using (var message = request())
                {
                    response = await client.SendAsync(message, completion, cancellationToken);
                }

                if (!IsThrottled(response.StatusCode) || attempt >= maxRetries)
                {
                    return response;
                }

                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Delay(delay, cancellationToken);
            }
        }

        private static bool IsThrottled(HttpStatusCode status) =>
            status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

        private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            var header = response.Headers.RetryAfter;
            var delay = header?.Delta
                ?? (header?.Date is { } date ? date - timeProvider.GetUtcNow() : (TimeSpan?)null)
                ?? AzureRetryAfter(response) // Azure Cosmos DB's own, in milliseconds, where it gives no Retry-After
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            return delay > maxDelay ? maxDelay : delay;
        }

        private static TimeSpan? AzureRetryAfter(HttpResponseMessage response) =>
            response.Headers.TryGetValues("x-ms-retry-after-ms", out var values)
            && double.TryParse(System.Linq.Enumerable.FirstOrDefault(values), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds >= 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : null;
    }
}
