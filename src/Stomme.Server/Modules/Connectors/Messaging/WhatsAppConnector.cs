#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Messaging
{
    /// <summary>
    /// WhatsApp Business (BL-229, n8n's WhatsApp Business Cloud node, its text message sent): Meta's WhatsApp Cloud API under
    /// Graph (<c>https://graph.facebook.com/v23.0</c>, the version the endpoint names), a system user's access token as the
    /// bearer, the connection's <c>phone_number_id</c> the sender - the number's ID in WhatsApp Manager, not the number. A test
    /// reads the number and names it; <c>message</c> sends a text to a phone number - <c>reply_to</c> a message's id
    /// (<c>wamid....</c>) to quote it in the reply. WhatsApp delivers free-form text only to someone who wrote to the number in
    /// the last 24 hours; outside that window Meta refuses it, and its refusal says so. A refusal is a 4xx whose JSON error says
    /// why, its details beside it.
    /// </summary>
    public sealed class WhatsAppConnector : IConnector, IConnectorExtractor
    {
        public const string PhoneNumberSetting = "phone_number_id";

        public const int MaxText = 4096;

        private static readonly Regex MessageId = new("^wamid\\.[A-Za-z0-9._=+/-]{1,500}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "whatsapp";

        public string DisplayName => "WhatsApp Business";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Meta names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Access token (a system user's)" };

        public string EndpointExample => "https://graph.facebook.com/v23.0";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://business.whatsapp.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.facebook.com/docs/whatsapp/cloud-api/reference/messages");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField(PhoneNumberSetting, "Phone number ID", ConnectorConfigFieldType.Text, Required: true,
                Description: "The sending number's ID - WhatsApp Manager > Phone numbers - its digits, not the number itself."),
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            ChatMessages.Describe("A phone number with its country code (+46701740605).", "The id of a message it answers (wamid....), quoted in the reply."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryNumber(context.Endpoint, context.Settings, "?fields=display_phone_number,verified_name", out var target, out var problem))
            {
                return ConnectionTestResult.Fail(problem);
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            Authorize(request, context.Credentials);
            try
            {
                var (status, json) = await ChatMessages.ReadAsync(client, request, cancellationToken);
                return status is >= 200 and < 300 && ChatMessages.Text(json?["display_phone_number"]) is { Length: > 0 } number
                    ? ConnectionTestResult.Ok($"Signed in as {ChatMessages.Text(json!["verified_name"]) ?? "the business"} ({number}).")
                    : ConnectionTestResult.Fail(Reason(json) is { } reason ? "WhatsApp: " + reason : $"HTTP {status}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var message = ChatMessages.Read(context.Parameters, out var problem) ?? throw new InvalidOperationException(problem);
            var to = new string(message.To.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '(' and not ')').ToArray()).TrimStart('+');
            if (to.Length is < 7 or > 15 || !to.All(char.IsAsciiDigit))
            {
                yield return ChatMessages.Refused("WhatsApp", $"'{message.To}' is not a phone number with its country code (+46701740605).");
                yield break;
            }

            if (message.ReplyTo is { } reply && !MessageId.IsMatch(reply))
            {
                yield return ChatMessages.Refused("WhatsApp", $"'{reply}' is not a WhatsApp message's id (wamid....).");
                yield break;
            }

            if (message.Text.Length > MaxText)
            {
                yield return ChatMessages.Refused("WhatsApp", $"a text message holds at most {MaxText} characters - this one has {message.Text.Length}.");
                yield break;
            }

            if (!TryNumber(context.Endpoint, context.Settings, "/messages", out var target, out problem))
            {
                throw new InvalidOperationException(problem);
            }

            var body = new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                ["to"] = to,
                ["type"] = "text",
                ["text"] = new JsonObject { ["preview_url"] = false, ["body"] = message.Text },
            };
            if (message.ReplyTo is { } quoted)
            {
                body["context"] = new JsonObject { ["message_id"] = quoted };
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            Authorize(request, context.Credentials);
            var (status, json) = await ChatMessages.PostAsync(client, request, body, cancellationToken);
            if (status is >= 200 and < 300 && ChatMessages.Text(json?["messages"]?[0]?["id"]) is { Length: > 0 } id)
            {
                yield return ChatMessages.Sent(id, ChatMessages.Text(json!["contacts"]?[0]?["wa_id"]) ?? to);
            }
            else if (status is >= 400 and < 500 && Reason(json) is { } reason)
            {
                yield return ChatMessages.Refused("WhatsApp", reason);
            }
            else
            {
                throw new InvalidOperationException($"HTTP {status}: not WhatsApp's answer.");
            }
        }

        // Meta's error: its message, and the details beside it when they say more (re-engagement, a number not on WhatsApp).
        private static string? Reason(JsonNode? json)
        {
            var error = json?["error"] as JsonObject;
            var said = ChatMessages.Text(error?["message"]);
            var details = ChatMessages.Text(error?["error_data"]?["details"]);
            return said is null ? details : details is { Length: > 0 } && details != said ? said + " - " + details : said;
        }

        // The sending number's own address under the endpoint: {phone_number_id}{rest}, the ID - written into the path - held to
        // digits.
        private static bool TryNumber(string endpoint, IReadOnlyDictionary<string, string> settings, string rest, out Uri target, out string problem)
        {
            target = null!;
            var id = Values.Get(settings, PhoneNumberSetting);
            if (id is null || id.Length is 0 or > 20 || !id.All(char.IsAsciiDigit))
            {
                problem = "The connection's phone number ID must be its digits - WhatsApp Manager > Phone numbers names it.";
                return false;
            }

            if (!ChatMessages.TryUnder(endpoint, id, out var number))
            {
                problem = "Endpoint must be an absolute http(s) URL - Meta's is https://graph.facebook.com/v23.0.";
                return false;
            }

            target = new Uri(number.AbsoluteUri + rest);
            problem = string.Empty;
            return true;
        }

        private static void Authorize(HttpRequestMessage request, IReadOnlyDictionary<string, string> credentials)
        {
            if (credentials.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }
}
