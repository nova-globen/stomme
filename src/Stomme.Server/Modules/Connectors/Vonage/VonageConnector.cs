#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Vonage
{
    /// <summary>
    /// Vonage (n8n's Vonage node: an SMS sent) over Vonage's SMS API (<c>https://rest.nexmo.com/sms/json</c>) by an
    /// account's API key and secret - Basic, as Vonage takes them, never in the body or the address. A test reads the account's
    /// balance, so it sends nothing.
    /// </summary>
    /// <remarks>
    /// <c>send</c> (a text from a name or a number to a number in E.164 - as GSM-7 when it can be, as Unicode when it carries a
    /// character GSM-7 has not, where n8n's node always sent it as text and garbled it - with an account reference, a delivery
    /// receipt's callback, a client reference, a message class, a protocol id, whether a receipt is wanted and a time to live in
    /// minutes, as n8n's node takes them), form-encoded as Vonage's SMS API takes it: a record for each part Vonage sends, and a
    /// part it refuses Vonage's status and words. Every number, name, link, length and choice held to Vonage's shape before
    /// anything is sent.
    /// </remarks>
    public sealed class VonageConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://rest.nexmo.com";

        private const int MostText = 1600; // Vonage's most for one message, its parts joined

        private const int MostReference = 100; // a callback's and a client reference's, as Vonage's API has them

        // GSM 03.38's basic set and its extension - what a text SMS carries; anything else is sent as Unicode.
        private const string Gsm7 = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà^{}\\[~]|€\f";

        private static readonly Regex Number = new("^\\+?[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant);

        private static readonly Regex Name = new("^[A-Za-z0-9 ]{1,11}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "vonage";

        public string DisplayName => "Vonage";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The Basic scheme as Vonage names it: the account's API key and secret.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "API key (Vonage dashboard, API settings)", ["password"] = "API secret" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://www.vonage.com/communications-apis/sms/");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.vonage.com/en/api/sms");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: false, Description: "The name or number a step sends from when it names none - Kassan, or 46701740605."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("send", "Send an SMS", "n8n's SMS Send.")
            {
                Parameters = new(new[]
                {
                    Field("to", "To", "The number in E.164 - 46701740605.", required: true),
                    Field("text", "Text", "What it says - over several lines from the step's Query box.", required: true),
                    Field("from", "From", "A name of up to 11 letters and digits, or a number. Empty: the connection's."),
                    Field("account_ref", "Account reference", "Which of the account's sub-accounts it is billed to."),
                    Field("callback", "Callback", "Where Vonage posts the delivery receipt - https://kafe.example/sms/kvitto."),
                    Field("client_ref", "Client reference", "Up to 100 characters of the sender's own, in the receipt."),
                    Field("message_class", "Message class", "0 (shown at once, not stored), 1, 2 or 3."),
                    Field("protocol_id", "Protocol id", "0 to 255."),
                    Field("status_report", "Delivery receipt", "true or false - Vonage asks for one unless told not to."),
                    Field("ttl", "Time to live", "Minutes Vonage keeps trying, up to 10080 - 4320, three days, by default."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.SendAsync(HttpMethod.Get, "/account/get-balance", null, cancellationToken);
                return answer["value"] is JsonValue value && value.TryGetValue<decimal>(out var balance)
                    ? ConnectionTestResult.Ok($"Vonage answers - the account holds {balance.ToString("0.00", CultureInfo.InvariantCulture)} EUR.")
                    : ConnectionTestResult.Fail("Vonage answered without the account's balance - check the address: a sign-in page or a proxy answers so.");
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
            if (!string.Equals((context.OperationName ?? "send").Trim(), "send", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Vonage has no operation '{context.OperationName}' - send.");
            }

            var p = context.Parameters;

            // Everything held to Vonage's shape before anything is sent.
            var to = Values.Get(p, "to") ?? throw new InvalidOperationException("Give the number to send to, in E.164 - 46701740605 (to).");
            var compact = to.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
            if (!Number.IsMatch(compact))
            {
                throw new InvalidOperationException($"'{to}' is not a number in E.164 - 46701740605 (to).");
            }

            var text = (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : Values.Get(p, "text"))
                ?? throw new InvalidOperationException("Give the text (text) - over several lines in the step's Query box.");
            if (text.Length > MostText)
            {
                throw new InvalidOperationException($"A text is at most {MostText} characters - this is {text.Length} (text).");
            }

            var from = Values.Get(p, "from") ?? Values.Get(context.Settings, "from")
                ?? throw new InvalidOperationException("Give who it is from - a name of up to 11 letters and digits, or a number (from) - or set the connection's.");
            var fromNumber = Number.IsMatch(from);
            if (!fromNumber && !Name.IsMatch(from))
            {
                throw new InvalidOperationException($"'{from}' is not a sender - a name of up to 11 letters and digits, or a number (from).");
            }

            var form = new List<KeyValuePair<string, string>>
            {
                new("from", fromNumber ? from.TrimStart('+') : from),
                new("to", compact.TrimStart('+')),
                new("text", text),
                new("type", text.All(character => Gsm7.Contains(character)) ? "text" : "unicode"),
            };
            if (Values.Get(p, "account_ref") is { } account)
            {
                form.Add(new("account-ref", account));
            }

            if (Values.Get(p, "callback") is { } callback)
            {
                form.Add(new("callback", callback.Length <= MostReference && Stomme.Shared.Utilities.TryCreateAbsoluteUri(callback, out var link) && link.Scheme is "https" or "http"
                    ? callback
                    : throw new InvalidOperationException($"'{callback}' is not a web address of at most {MostReference} characters - https://kafe.example/sms/kvitto (callback).")));
            }

            if (Values.Get(p, "client_ref") is { } client)
            {
                form.Add(new("client-ref", client.Length <= MostReference ? client : throw new InvalidOperationException($"A client reference is at most {MostReference} characters - this is {client.Length} (client_ref).")));
            }

            if (Values.Get(p, "message_class") is { } messageClass)
            {
                form.Add(new("message-class", messageClass is "0" or "1" or "2" or "3" ? messageClass : throw new InvalidOperationException($"'{messageClass}' is not 0, 1, 2 or 3 (message_class).")));
            }

            if (Values.Get(p, "protocol_id") is { } protocol)
            {
                form.Add(new("protocol-id", int.TryParse(protocol, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id <= 255 ? id.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{protocol}' is not 0 to 255 (protocol_id).")));
            }

            if (Values.Get(p, "status_report") is { } report)
            {
                form.Add(new("status-report-req", bool.TryParse(report, out var wanted) ? (wanted ? "true" : "false") : throw new InvalidOperationException($"'{report}' is not true or false (status_report).")));
            }

            if (Values.Get(p, "ttl") is { } ttl)
            {
                // Minutes, as n8n's node takes them; Vonage's milliseconds, at most seven days.
                form.Add(new("ttl", int.TryParse(ttl, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 10080
                    ? ((long)minutes * 60000).ToString(CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{ttl}' is not a number of minutes from 1 to 10080 - seven days (ttl).")));
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            var answer = await session.SendAsync(HttpMethod.Post, "/sms/json", form, cancellationToken);
            var parts = (answer["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            if (parts.Count == 0)
            {
                throw new InvalidOperationException("Vonage answered with no message - check the address: a sign-in page or a proxy answers so.");
            }

            if (parts.FirstOrDefault(part => Text(part["status"]) != "0") is { } refused)
            {
                var sent = parts.Count(part => Text(part["status"]) == "0");
                throw new InvalidOperationException($"Vonage refused the message to {compact.TrimStart('+')}: {Text(refused["error-text"]) ?? "no reason given"} (status {Text(refused["status"])}){(sent > 0 ? $" - {sent} of its {parts.Count} parts were sent" : string.Empty)}.");
            }

            foreach (var part in parts)
            {
                yield return new ConnectorRecord(part.ToDictionary(pair => pair.Key, pair => (object?)(Text(pair.Value) ?? pair.Value?.ToJsonString()), StringComparer.Ordinal));
            }
        }

        private static string? Text(JsonNode? node) => node is JsonValue value ? value.TryGetValue<string>(out var text) ? text : value.ToJsonString() : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;

            private Session(HttpClient client, string api)
            {
                this.client = client;
                this.api = api;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Vonage's address - its own, {DefaultEndpoint}.");
                }

                return new Session(BearerClient.Create(egress, timeout, BearerClient.Basic(credentials, "Vonage"), acceptJson: true), root);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call under the API; Vonage's JSON object, or its HTTP status and words on a failure.</summary>
            public async Task<JsonObject> SendAsync(HttpMethod method, string path, IReadOnlyList<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(method, api + path) { Content = form is null ? null : new FormUrlEncodedContent(form) }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Vonage answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = Text(json?["error-code-label"]) ?? Text(json?["title"]) ?? Text(json?["detail"]) ?? Text(json?["error-text"]);
                    var hint = (int)response.StatusCode == 401 ? " - check the API key and secret" : string.Empty;
                    throw new InvalidOperationException($"Vonage answered HTTP {(int)response.StatusCode}{(said is null ? string.Empty : ": " + (said.Length > 300 ? said[..300] : said))}{hint}.");
                }

                return json as JsonObject ?? throw new InvalidOperationException("Vonage answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
            }
        }
    }
}
