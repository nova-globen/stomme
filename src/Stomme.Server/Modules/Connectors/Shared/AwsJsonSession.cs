#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// One connection's calls to an AWS service over its JSON protocol (BL-229, the AWS AI connectors): the service's address in the
    /// region, its target prefix and content type, every request signed with SigV4; a failure thrown with its status, AWS's error
    /// type and its message.
    /// </summary>
    public sealed class AwsJsonSession
    {
        private static readonly Regex RegionName = new("^[a-z]{2}(-gov)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly string name;
        private readonly string service;
        private readonly string target;
        private readonly string api;
        private readonly string accessKey;
        private readonly string secretKey;
        private readonly TimeProvider time;
        private readonly HttpClient client;

        private AwsJsonSession(string name, string service, string target, string api, string region, string accessKey, string secretKey, TimeProvider time, HttpClient client)
        {
            this.name = name;
            this.service = service;
            this.target = target;
            this.api = api;
            Region = region;
            this.accessKey = accessKey;
            this.secretKey = secretKey;
            this.time = time;
            this.client = client;
        }

        public string Region { get; }

        public static AwsJsonSession Of(string name, string service, string target, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
        {
            var region = Values.Get(settings, "region") ?? throw new InvalidOperationException("Give the AWS region - eu-west-1.");
            if (!RegionName.IsMatch(region))
            {
                throw new InvalidOperationException($"'{region}' is not an AWS region - eu-west-1.");
            }

            var api = Values.BaseUrl(endpoint, $"https://{service}.{region}.amazonaws.com");
            var uri = new Uri(api);
            if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
            {
                throw new InvalidOperationException($"'{endpoint}' is not {name}'s address - its own, https://{service}.{region}.amazonaws.com.");
            }

            var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException($"The {name} connection has no access key.");
            var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException($"The {name} connection has no secret key.");
            return new AwsJsonSession(name, service, target, api, region, accessKey, secretKey, time, egress.CreateHttpClient(timeout));
        }

        public async Task<JsonNode?> CallAsync(string action, JsonObject body, CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(body.ToJsonString(Wire));
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, api + "/") { Content = new ByteArrayContent(payload) };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1");
                request.Headers.TryAddWithoutValidation("X-Amz-Target", target + "." + action);
                AwsSigV4.Sign(request, payload, Region, service, accessKey, secretKey, null, time.GetUtcNow());
                return request;
            }, cancellationToken);
            var answer = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{name} answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var type = Text(json?["__type"]) is { } named ? named[(named.LastIndexOf('#') + 1)..] : null;
                var said = json is JsonObject error ? Text(error["message"]) ?? Text(error["Message"]) : null;
                throw new InvalidOperationException($"{name} answered HTTP {(int)response.StatusCode}{(type is null ? "" : " " + type)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }

        /// <summary>
        /// A JSON document at an address the service answered with - a presigned S3 address, which carries its own authorisation -
        /// fetched unsigned through the same egress; refused unless it is https or the service's own host (a stand-in's).
        /// </summary>
        public async Task<JsonNode?> FetchJsonAsync(string address, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !string.Equals(uri.Host, new Uri(api).Host, StringComparison.OrdinalIgnoreCase)) || uri.UserInfo.Length > 0)
            {
                throw new InvalidOperationException($"{name} answered with an address that is not one to fetch - an https address.");
            }

            using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
            var answer = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{name}'s file answered HTTP {(int)response.StatusCode}.");
            }

            try
            {
                return string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"{name}'s file is not JSON.");
            }
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>
    /// A document or an image an AWS AI service reads (BL-229, Textract and Rekognition, as n8n's nodes give it): a file of the
    /// pipeline run's folder, sent as its bytes, or an object in S3 - its bucket, its name and a version - which AWS reads itself.
    /// Held to the service's limit in bytes before anything is signed.
    /// </summary>
    public static class AwsMediaSource
    {
        private static readonly Regex Bucket = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);

        /// <summary>Whether the step names a file of the run's folder (rather than an S3 object) - checked before anything is read.</summary>
        public static bool FromFile(IReadOnlyDictionary<string, string> p)
        {
            var file = Values.Get(p, "file");
            var bucket = Values.Get(p, "bucket");
            if ((file is null) == (bucket is null))
            {
                throw new InvalidOperationException(file is null ? "Give the file in the site's pipeline folder, or an S3 bucket and object (bucket, object)." : "Give the file or an S3 object, not both.");
            }

            if (bucket is not null)
            {
                if (!Bucket.IsMatch(bucket))
                {
                    throw new InvalidOperationException($"'{bucket}' is not an S3 bucket's name (bucket).");
                }

                if (Values.Get(p, "object") is null)
                {
                    throw new InvalidOperationException("Give the S3 object's name - kvitton/1042.png (object).");
                }
            }

            return file is not null;
        }

        /// <summary>The source as AWS takes it: <c>{"Bytes": ...}</c> read from the run's folder, or <c>{"S3Object": {"Bucket", "Name", "Version"}}</c>.</summary>
        public static async Task<JsonObject> ReadAsync(IReadOnlyDictionary<string, string> p, IConnectorFiles? files, long maxBytes, string service, CancellationToken cancellationToken)
        {
            if (!FromFile(p))
            {
                var s3 = new JsonObject { ["Bucket"] = Values.Get(p, "bucket"), ["Name"] = Values.Get(p, "object") };
                if (Values.Get(p, "version") is { } version)
                {
                    s3["Version"] = version;
                }

                return new JsonObject { ["S3Object"] = s3 };
            }

            if (files is null)
            {
                throw new InvalidOperationException("A file is read from the run's pipeline folder - run it from a pipeline, or give an S3 object.");
            }

            await using var opened = await files.OpenReadAsync(Values.Get(p, "file")!, cancellationToken);
            if (opened.Length > maxBytes)
            {
                throw new InvalidOperationException($"'{opened.Name}' is {opened.Length} bytes; {service} reads at most {maxBytes} sent with the call - put it in S3 and give its bucket and object.");
            }

            using var buffer = new System.IO.MemoryStream();
            await opened.Content.CopyToAsync(buffer, cancellationToken);
            return new JsonObject { ["Bytes"] = Convert.ToBase64String(buffer.ToArray()) };
        }
    }
}