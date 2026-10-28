#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The handler the OAuth connector tests hand their <see cref="StubEgress"/> (BL-96 increment 3): every request is
    /// recorded with its body, and a responder answers by request and body - the shape the predecessor platform's per-file RoutingHandler
    /// classes had, made one. A token endpoint is answered by <see cref="TokenRoute"/> when set (by host or path), and the
    /// forms it received are kept apart from the API calls.
    /// </summary>
    internal sealed class RecordingHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : this((request, _, _) => responder(request))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : this((request, _, call) => responder(request, call))
        {
        }

        /// <summary>Whether a request is a token call; such a request is answered with <see cref="TokenResponse"/> and its body kept in <see cref="TokenForms"/>.</summary>
        public Func<HttpRequestMessage, bool>? TokenRoute { get; init; }

        public Func<HttpResponseMessage> TokenResponse { get; init; } = () => Json("""{"token_type":"Bearer","expires_in":3599,"access_token":"tok-1"}""");

        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        public List<string> TokenForms { get; } = new();

        public IEnumerable<HttpRequestMessage> Requests => Calls.Select(call => call.Request);

        public static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (TokenRoute is not null && TokenRoute(request))
            {
                TokenForms.Add(body);
                return TokenResponse();
            }

            var call = Calls.Count;
            Calls.Add((request, body));
            return responder(request, body, call);
        }
    }
}
