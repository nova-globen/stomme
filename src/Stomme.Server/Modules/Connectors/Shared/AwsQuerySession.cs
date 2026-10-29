#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// One connection's calls to an AWS service over its Query protocol (the AWS IAM connector): a form of
    /// <c>Action</c>, <c>Version</c> and the call's parameters POSTed to the service's address, every request signed with SigV4;
    /// the answer's <c>{Action}Result</c> element, or a failure thrown with its status, AWS's error code and its message.
    /// </summary>
    public sealed class AwsQuerySession
    {
        private readonly string name;
        private readonly string service;
        private readonly string version;
        private readonly string api;
        private readonly string accessKey;
        private readonly string secretKey;
        private readonly TimeProvider time;
        private readonly HttpClient client;

        private AwsQuerySession(string name, string service, string version, string api, string region, string accessKey, string secretKey, TimeProvider time, HttpClient client)
        {
            this.name = name;
            this.service = service;
            this.version = version;
            this.api = api;
            Region = region;
            this.accessKey = accessKey;
            this.secretKey = secretKey;
            this.time = time;
            this.client = client;
        }

        /// <summary>The region the calls are signed for.</summary>
        public string Region { get; }

        /// <summary>A session at <paramref name="defaultEndpoint"/> - or the endpoint given, a stand-in's - signed for <paramref name="service"/> in <paramref name="region"/>.</summary>
        public static AwsQuerySession Of(string name, string service, string version, string defaultEndpoint, string region, string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
        {
            var api = Values.BaseUrl(endpoint, defaultEndpoint);
            var uri = new Uri(api);
            if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
            {
                throw new InvalidOperationException($"'{endpoint}' is not {name}'s address - its own, {defaultEndpoint}.");
            }

            var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException($"The {name} connection has no access key.");
            var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException($"The {name} connection has no secret key.");
            return new AwsQuerySession(name, service, version, api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
        }

        /// <summary><paramref name="action"/> with its parameters; its result element (an empty one when the call answers none) - an answer that is not <c>{Action}Response</c> a failure.</summary>
        public async Task<XElement> CallAsync(string action, IEnumerable<(string Name, string Value)> parameters, CancellationToken cancellationToken)
        {
            var form = string.Join("&", new[] { ("Action", action), ("Version", version) }.Concat(parameters)
                .Select(pair => Uri.EscapeDataString(pair.Item1) + "=" + Uri.EscapeDataString(pair.Item2)));
            var payload = Encoding.UTF8.GetBytes(form);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, api + "/") { Content = new ByteArrayContent(payload) };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded") { CharSet = "utf-8" };
                AwsSigV4.Sign(request, payload, Region, service, accessKey, secretKey, null, time.GetUtcNow());
                return request;
            }, cancellationToken);
            var answer = await response.Content.ReadAsStringAsync(cancellationToken);
            XElement? root = null;
            try
            {
                root = string.IsNullOrWhiteSpace(answer) ? null : XElement.Parse(answer);
            }
            catch (XmlException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{name} answered HTTP {(int)response.StatusCode} with what is not XML - check the address: a sign-in page or a proxy answers so.");
            }
            catch (XmlException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = root?.Descendants().FirstOrDefault(element => element.Name.LocalName == "Error");
                var code = Child(error, "Code");
                var said = Child(error, "Message");
                throw new InvalidOperationException($"{name} answered HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            // AWS answers a call with its {Action}Response; anything else - a sign-in page that happens to be well-formed - is not it.
            if (root is null || root.Name.LocalName != action + "Response")
            {
                throw new InvalidOperationException($"{name} answered HTTP {(int)response.StatusCode} with what is not its answer to {action} - check the address: a sign-in page or a proxy answers so.");
            }

            return root.Elements().FirstOrDefault(element => element.Name.LocalName == action + "Result") ?? new XElement(action + "Result");
        }

        /// <summary>A child's text by its local name - AWS's answers carry their service's namespace.</summary>
        public static string? Child(XElement? element, string localName) =>
            element?.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

        /// <summary>The members of a list child (<c>&lt;Users&gt;&lt;member&gt;...</c>).</summary>
        public static IEnumerable<XElement> Members(XElement? element, string localName) =>
            element?.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Elements().Where(member => member.Name.LocalName == "member") ?? Enumerable.Empty<XElement>();
    }
}
