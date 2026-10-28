#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Mailjet
{
    /// <summary>
    /// Mailjet SMS (BL-229, n8n's Mailjet node: an SMS sent): Mailjet's SMS API v4 with an SMS token as a bearer token - a token
    /// of its own, apart from the e-mail API's keys - over the host's egress. A test reads the latest SMS sent.
    /// </summary>
    /// <remarks>
    /// <c>send_sms</c>: a sender (3 to 11 letters and digits) a number in its international form and a text of up to 1,600
    /// characters; Mailjet's state, its count of SMS, its cost and its id kept. <c>sms</c>: what was sent, to a number, between two
    /// moments, by Mailjet's state - page by page by offset, up to <c>max</c> (1,000). Every sender, number, moment and state held
    /// to its shape before anything is sent. A throttled call waits as Mailjet says; a failure is thrown with its status and
    /// Mailjet's message.
    /// </remarks>
    public sealed class MailjetSmsConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.mailjet.com";

        public const string SendSmsOperation = "send_sms";

        public const string SmsOperation = "sms";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Sender = new("^[A-Za-z0-9]{3,11}$", RegexOptions.CultureInvariant);

        private static readonly Regex Phone = new("^\\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant);

        private static readonly Regex Moment = new("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2}(\\.\\d{1,7})?)?(Z|[+\\-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SendSmsOperation, SmsOperation };

        // Mailjet's SMS states by name and number.
        private static readonly (string Name, int Code)[] States = { ("pending", 1), ("sent", 2), ("delivered", 3), ("undelivered", 4), ("rejected", 5) };

        public string ConnectorType => "mailjetsms";

        public string DisplayName => "Mailjet SMS";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "SMS token (Account settings > SMS > token)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://www.mailjet.com/products/sms/");

        public Uri ApiDocumentationUrl { get; } = new("https://dev.mailjet.com/sms/reference/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SendSmsOperation, "Send an SMS", "n8n's Mailjet SMS Send.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: true, Description: "The sender's name - 3 to 11 letters and digits."),
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: true, Description: "A number in its international form - +46701740605."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: true, Description: "Up to 1,600 characters."),
                }),
            },
            new ConnectorOperation(SmsOperation, "Get sent SMS", "What was sent and its state.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: false, Description: "Only to this number."),
                    new ConnectorConfigField("from_time", "From", ConnectorConfigFieldType.Text, Required: false, Description: "A moment with its offset - 2026-10-01T00:00:00Z."),
                    new ConnectorConfigField("to_time", "Until", ConnectorConfigFieldType.Text, Required: false, Description: "A moment with its offset."),
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "pending, sent, delivered, undelivered or rejected."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var (client, call) = Session(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using (client)
                {
                    var latest = ((await call(HttpMethod.Get, "/v4/sms?Limit=1", null, cancellationToken))?["Data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                    return ConnectionTestResult.Ok(latest is null ? "Mailjet SMS: signed in - nothing sent yet." : $"Mailjet SMS: signed in - the latest to {Text(latest["To"]) ?? "a number"}.");
                }
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SendSmsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Mailjet SMS has no operation '{context.OperationName}' - {Known[0]} or {Known[1]}.");
            var p = context.Parameters;
            string? PhoneOf(string name) => Values.Get(p, name) is { } given ? Phone.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a number in its international form - +46701740605 ({name}).") : null;
            if (operation == SendSmsOperation)
            {
                var from = Values.Get(p, "from") is { } sender ? Sender.IsMatch(sender) ? sender : throw new InvalidOperationException($"'{sender}' is not a sender's name - 3 to 11 letters and digits (from).") : throw new InvalidOperationException("Give the sender's name (from).");
                var to = PhoneOf("to") ?? throw new InvalidOperationException("Give the number (to).");
                var text = Values.Get(p, "text") is { } given ? given.Length <= 1600 ? given : throw new InvalidOperationException($"The text is {given.Length} characters - 1,600 at most (text).") : throw new InvalidOperationException("Give the text (text).");
                var (client, call) = Session(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using (client)
                {
                    var sent = await call(HttpMethod.Post, "/v4/sms-send", new JsonObject { ["From"] = from, ["To"] = to, ["Text"] = text }, cancellationToken) as JsonObject ?? new JsonObject();
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = Text(sent["MessageId"]) ?? Text(sent["ID"]),
                        ["to"] = Text(sent["To"]) ?? to,
                        ["state"] = Text(sent["Status"]?["Name"]),
                        ["sms_count"] = sent["SmsCount"] is JsonValue count && count.TryGetValue<long>(out var n) ? n : null,
                        ["cost"] = sent["Cost"] is JsonObject cost ? cost.ToJsonString(Wire) : null,
                    });
                }

                yield break;
            }

            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = new List<string>();
            if (PhoneOf("to") is { } number)
            {
                query.Add("To=" + Uri.EscapeDataString(number));
            }

            foreach (var (name, wire) in new[] { ("from_time", "FromTS"), ("to_time", "ToTS") })
            {
                if (Values.Get(p, name) is { } given)
                {
                    query.Add(wire + "=" + (Moment.IsMatch(given) && DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not a moment with its offset - 2026-10-01T00:00:00Z ({name}).")));
                }
            }

            if (Values.Get(p, "state") is { } state)
            {
                query.Add("StatusCode=" + (States.FirstOrDefault(known => string.Equals(known.Name, state, StringComparison.OrdinalIgnoreCase)) is { Name: not null } found ? found.Code.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{state}' is not pending, sent, delivered, undelivered or rejected (state).")));
            }

            var (listing, get) = Session(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using (listing)
            {
                // Mailjet pages by offset: the size fixed at the start, a short page the end.
                var size = Math.Min(PageSize, max);
                var read = 0;
                for (var offset = 0; ; offset += size)
                {
                    var answer = await get(HttpMethod.Get, "/v4/sms?" + string.Join("&", query.Append($"Limit={size}").Append($"Offset={offset}")), null, cancellationToken);
                    var items = (answer?["Data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return new ConnectorRecord(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    if (items.Count < size)
                    {
                        yield break;
                    }
                }
            }
        }

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Mailjet's API with the SMS token as a bearer token; a throttled call waiting as Mailjet says; a
        // failure thrown with its status and Mailjet's message.
        private static (HttpClient Client, Func<HttpMethod, string, JsonNode?, CancellationToken, Task<JsonNode?>> Call) Session(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
        {
            var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Mailjet SMS connection has no token.");
            var baseUrl = Values.BaseUrl(endpoint, DefaultEndpoint);
            baseUrl = baseUrl.EndsWith("/v4", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^3] : baseUrl;
            var client = egress.CreateHttpClient(timeout);
            async Task<JsonNode?> Call(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

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
                    throw new InvalidOperationException($"Mailjet answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = Text(json?["ErrorMessage"]) ?? Text(json?["message"]) ?? "";
                    var related = (json?["ErrorRelatedTo"] as JsonArray)?.Select(field => Text(field)).OfType<string>().ToList();
                    said += related is { Count: > 0 } ? " (" + string.Join(", ", related) + ")" : "";
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }

            return (client, Call);
        }
    }
}
