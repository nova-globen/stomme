#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The egress a connector unit test hands in: an <see cref="IConnectorEgress"/> over a test handler, so every
    /// request a connector sends is captured without a socket - the host owns the client, so a connector takes no
    /// handler factory of its own. <see cref="Pinned"/> is what
    /// a driver connector gets back for any host; null refuses every host.
    /// </summary>
    internal sealed class StubEgress(HttpMessageHandler? handler = null) : IConnectorEgress
    {
        public IPAddress? Pinned { get; init; } = IPAddress.Loopback;

        public TimeSpan? LastTimeout { get; private set; }

        public int Clients { get; private set; }

        public List<(string Host, int Port)> Pins { get; } = new();

        /// <summary>What this egress calls a denial: the contract's own exception (the interface default), or everything when a test says so.</summary>
        public bool DeniesEverything { get; init; }

        public bool IsDenial(Exception exception) => DeniesEverything || exception is ConnectorEgressDeniedException || exception.InnerException is ConnectorEgressDeniedException;

        public HttpClient CreateHttpClient(TimeSpan timeout)
        {
            Clients++;
            LastTimeout = timeout;
            // A live loopback call must not ride whatever proxy this process inherited (WARP, a corporate setting): the socket goes where the URL says.
            return new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }, disposeHandler: handler is null) { Timeout = timeout };
        }

        public Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Pins.Add((host, port));
            return Task.FromResult(Pinned);
        }

        /// <summary>What a reach answers - nothing, as the contract's default, unless a test says.</summary>
        public Func<IPAddress, int, string, Task>? Reach { get; init; }

        public List<(IPAddress Address, int Port, string Host, TimeSpan Timeout, string Service)> Reaches { get; } = new();

        public Task ReachAsync(IPAddress address, int port, string host, TimeSpan timeout, string service, CancellationToken cancellationToken = default)
        {
            Reaches.Add((address, port, host, timeout, service));
            return Reach?.Invoke(address, port, host) ?? Task.CompletedTask;
        }
    }

    /// <summary>A handler answering every request with one status and one body, capturing what was sent.</summary>
    internal sealed class StubHandler(HttpStatusCode status, string body = "", Action<HttpRequestMessage, string?>? capture = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public string? BodyContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            BodyContentType = request.Content?.Headers.ContentType?.ToString();
            capture?.Invoke(request, Body);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    internal static class Contexts
    {
        public static ConnectorTestContext Test(IConnectorEgress egress, string endpoint, IReadOnlyDictionary<string, string>? settings = null, IReadOnlyDictionary<string, string>? credentials = null, int timeoutSeconds = 5) =>
            new(endpoint, settings ?? new Dictionary<string, string>(), TimeSpan.FromSeconds(timeoutSeconds), egress) { Credentials = credentials ?? new Dictionary<string, string>() };

        public static ConnectorExtractContext Extract(IConnectorEgress egress, string endpoint, string operation, IReadOnlyDictionary<string, string>? parameters = null, IReadOnlyDictionary<string, string>? settings = null, IReadOnlyDictionary<string, string>? credentials = null, int timeoutSeconds = 5) =>
            new(endpoint, settings ?? new Dictionary<string, string>(), TimeSpan.FromSeconds(timeoutSeconds), egress, operation, parameters ?? new Dictionary<string, string>()) { Credentials = credentials ?? new Dictionary<string, string>() };

        public static async Task<List<ConnectorRecord>> DrainAsync(IConnectorExtractor extractor, ConnectorExtractContext context)
        {
            var records = new List<ConnectorRecord>();
            await foreach (var record in extractor.ExtractAsync(context))
            {
                records.Add(record);
            }

            return records;
        }

        public static string? Header(HttpRequestMessage? request, string name) =>
            request is not null && request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }
}
