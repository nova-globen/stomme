#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Messaging;
using Stomme.Modules.Connectors.Shared;
using MimeKit;

namespace Stomme.Modules.Connectors.Outlook
{
    /// <summary>
    /// Microsoft Outlook (BL-229, n8n's Microsoft Outlook node: messages got, sent and replied to): a mailbox of a Microsoft 365
    /// tenant over Microsoft Graph (<c>https://graph.microsoft.com/v1.0</c>), app-only - an Entra ID app registration's client id
    /// and secret and the tenant's token endpoint (OAuth2 client credentials, the <c>.default</c> scope; the app granted Mail.Send
    /// and Mail.ReadWrite) through the shared <see cref="OAuth2TokenProvider"/>, the access token cached, every call through the
    /// HOST's egress. The app acts as the one mailbox the connection names (<c>mailbox</c>, its address or user id). A test reads
    /// that mailbox's Inbox.
    /// </summary>
    /// <remarks>
    /// <c>search</c>: a folder's messages (Inbox by default, a well-known folder name or a folder's id), newest first - those
    /// Outlook's search keeps (<c>query</c>, as in its search box) or an OData <c>filter</c> keeps (<c>isRead eq false</c>) - at most
    /// <c>max</c>, page by page through the next links Graph gives, each held to the endpoint before the token goes with it; one
    /// record each: its id, conversation, headers, its body as text or HTML (cut at <c>body_max_chars</c>, said), its preview and
    /// whether it has attachments. <c>send</c>: a message - <c>to</c>, <c>cc</c>, <c>bcc</c>, a one-line <c>subject</c>, an HTML or
    /// a text body (the HTML when both) - made a draft and sent, so it has an id; given <c>reply_to</c>, a reply Outlook makes in that
    /// message's conversation - to its sender, its subject "RE:", the original quoted below the body given (to, cc, bcc and a
    /// subject given change Outlook's). A send answers ONE record, as the chat connectors'
    /// message does: its <c>id</c>, or <c>refused</c> with why - a recipient that is not an address, Graph's own code and message -
    /// because the extraction port answers a thrown failure with its fixed words alone (D110). <c>mark_read</c>: the messages with the
    /// ids given marked read. The mailbox, a folder and a message id are held to their shapes and escaped before they reach the path;
    /// a throttled call waits as Graph says; a failure of a search or a mark is thrown with its status and Graph's code.
    /// </remarks>
    public sealed class OutlookConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com/v1.0";

        public const string DefaultScope = "https://graph.microsoft.com/.default";

        public const string SearchOperation = "search";

        public const string SendOperation = "send";

        public const string MarkReadOperation = "mark_read";

        public const int DefaultMax = 50;

        public const int MostMax = 500;

        public const int DefaultBodyMaxChars = 100_000;

        private static readonly Regex GraphId = new("^[A-Za-z0-9+/=_-]{1,512}$", RegexOptions.CultureInvariant);

        private static readonly Regex Mailbox = new("^[^\\s/\\\\?#@]+@[^\\s/\\\\?#@]+$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "outlook";

        public string DisplayName => "Microsoft Outlook";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://outlook.office.com");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/resources/mail-api-overview");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("mailbox", "Mailbox", ConnectorConfigFieldType.Text, Required: true,
                Description: "The mailbox the app acts as - its address (orders@contoso.com) or its user id."),
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false,
                Description: "The token scope requested from the identity platform. Leave it as it is.") { Default = DefaultScope },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Get messages", "A folder's messages, newest first - those a search or a filter keeps - one record each: its id, conversation, headers, body and preview - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("folder", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "inbox, sentitems, archive... or a folder's id.") { Default = "inbox" },
                    new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "As in Outlook's search box - from:ada subject:order. Empty: every message."),
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "An OData filter - isRead eq false. Not with a search."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: $"How many messages, at most {MostMax}.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    new ConnectorConfigField("body_max_chars", "Most characters of a body", ConnectorConfigFieldType.Integer, Required: false, Description: "The body cut at this many characters.") { Default = DefaultBodyMaxChars.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(SendOperation, "Send a message", "Sends a message from the mailbox, or a reply in a message's conversation - one record: its id - n8n's Send and Reply.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated. Empty on a reply: its sender."),
                    new ConnectorConfigField("cc", "Cc", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("bcc", "Bcc", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "One line. Empty on a reply: RE: the original's."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The plain-text body."),
                    new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "The HTML body - sent instead of the text when both are given."),
                    new ConnectorConfigField("reply_to", "Reply to", ConnectorConfigFieldType.Text, Required: false, Description: "A message's id: sent as a reply in its conversation."),
                }),
            },
            new ConnectorOperation(MarkReadOperation, "Mark as read", "Marks the messages with the ids given as read - one record saying how many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("ids", "Message ids", ConnectorConfigFieldType.Text, Required: true, Description: "Message ids, comma-separated, or a JSON list."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var box = MailboxUrl(context.Endpoint, context.Settings);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var inbox = await SendAsync(client, token, HttpMethod.Get, box + "/mailFolders/inbox?$select=displayName,totalItemCount", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in: {inbox["displayName"]?.GetValue<string>() ?? "Inbox"} holds {inbox["totalItemCount"]?.ToString() ?? "its"} messages.");
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
            var box = MailboxUrl(context.Endpoint, context.Settings);

            if (string.Equals(context.OperationName, SendOperation, StringComparison.OrdinalIgnoreCase))
            {
                var (draft, problem) = Draft(context.Parameters); // refused before a token is asked for
                if (draft is null)
                {
                    yield return ChatMessages.Refused(DisplayName, problem);
                    yield break;
                }

                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                yield return await SendMessageAsync(client, token, box, draft, cancellationToken);
                yield break;
            }

            if (string.Equals(context.OperationName, MarkReadOperation, StringComparison.OrdinalIgnoreCase))
            {
                var ids = Ids(Values.Get(context.Parameters, "ids"));
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                foreach (var id in ids)
                {
                    await SendAsync(client, token, HttpMethod.Patch, $"{box}/messages/{Uri.EscapeDataString(id)}", new JsonObject { ["isRead"] = true }, cancellationToken);
                }

                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["marked"] = (long)ids.Count });
                yield break;
            }

            await foreach (var record in SearchAsync(context, box, cancellationToken))
            {
                yield return record;
            }
        }

        private async IAsyncEnumerable<ConnectorRecord> SearchAsync(ConnectorExtractContext context, string box, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var folder = Folder(Values.Get(context.Parameters, "folder") ?? "inbox");
            var query = Values.Get(context.Parameters, "query");
            var filter = Values.Get(context.Parameters, "filter");
            if (query is not null && filter is not null)
            {
                throw new InvalidOperationException("Give a search or a filter - Graph takes one or the other.");
            }

            var max = Math.Clamp(int.TryParse(Values.Get(context.Parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) ? given : DefaultMax, 1, MostMax);
            var bodyMax = int.TryParse(Values.Get(context.Parameters, "body_max_chars"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultBodyMaxChars;
            var parameters = new List<string>
            {
                "$top=" + Math.Min(100, max).ToString(CultureInfo.InvariantCulture),
                "$select=" + Uri.EscapeDataString("id,conversationId,internetMessageId,receivedDateTime,from,toRecipients,ccRecipients,replyTo,subject,body,bodyPreview,isRead,hasAttachments,webLink"),
            };
            if (query is not null)
            {
                parameters.Add("$search=" + Uri.EscapeDataString("\"" + query.Replace("\"", "", StringComparison.Ordinal) + "\""));
            }
            else
            {
                parameters.Add("$orderby=" + Uri.EscapeDataString("receivedDateTime desc"));
                if (filter is not null)
                {
                    parameters.Add("$filter=" + Uri.EscapeDataString(filter));
                }
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var next = $"{box}/mailFolders/{Uri.EscapeDataString(folder)}/messages?{string.Join("&", parameters)}";
            var read = 0;
            while (next is not null && read < max)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await SendAsync(client, token, HttpMethod.Get, next, null, cancellationToken);
                foreach (var message in (page["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (read >= max)
                    {
                        break;
                    }

                    read++;
                    yield return Record(message, bodyMax);
                }

                // Graph's next link, followed only while it stays under the mailbox's own address - the token goes with it.
                next = page["@odata.nextLink"]?.GetValue<string>() is { } link && link.StartsWith(box + "/", StringComparison.OrdinalIgnoreCase) ? link : null;
            }
        }

        // A message as a record: its ids and conversation, its people as an IMAP record writes them, its body by its kind.
        private static ConnectorRecord Record(JsonObject message, int bodyMax)
        {
            var body = message["body"]?["content"]?.GetValue<string>();
            var html = string.Equals(message["body"]?["contentType"]?.GetValue<string>(), "html", StringComparison.OrdinalIgnoreCase);
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = message["id"]?.GetValue<string>(),
                ["conversation_id"] = message["conversationId"]?.GetValue<string>(),
                ["message_id"] = message["internetMessageId"]?.GetValue<string>()?.Trim('<', '>'),
                ["date"] = message["receivedDateTime"]?.GetValue<string>(),
                ["from"] = People(message["from"] is JsonObject from ? new JsonArray(from.DeepClone()) : null),
                ["to"] = People(message["toRecipients"] as JsonArray),
                ["cc"] = People(message["ccRecipients"] as JsonArray),
                ["reply_to"] = People(message["replyTo"] as JsonArray),
                ["subject"] = message["subject"]?.GetValue<string>() ?? "",
                ["text"] = html ? null : Cut(body, bodyMax),
                ["html"] = html ? Cut(body, bodyMax) : null,
                ["truncated"] = (body?.Length ?? 0) > bodyMax,
                ["snippet"] = message["bodyPreview"]?.GetValue<string>(),
                ["is_read"] = message["isRead"]?.GetValue<bool>() ?? false,
                ["has_attachments"] = message["hasAttachments"]?.GetValue<bool>() ?? false,
                ["web_link"] = message["webLink"]?.GetValue<string>(),
            });
        }

        // Graph's recipients written as a mail header lists them: "Name" <address>, comma-separated.
        private static string People(JsonArray? recipients)
        {
            var list = new InternetAddressList();
            foreach (var address in (recipients ?? new JsonArray()).OfType<JsonObject>().Select(recipient => recipient["emailAddress"]).OfType<JsonObject>())
            {
                if (address["address"]?.GetValue<string>() is { Length: > 0 } email)
                {
                    list.Add(new MailboxAddress(address["name"]?.GetValue<string>() is { Length: > 0 } name && name != email ? name : null, email));
                }
            }

            return list.ToString();
        }

        private static string? Cut(string? text, int most) => text is null || text.Length <= most ? text : text[..most];

        // What to send, read and checked before anything is asked of Microsoft.
        private sealed record Outgoing(IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc, string? Subject, string? Text, string? Html, string? ReplyTo);

        // The message to send, or why not - in words an author can act on, answered as a record (D110).
        private static (Outgoing? Draft, string? Problem) Draft(IReadOnlyDictionary<string, string> parameters)
        {
            try
            {
                var subject = Values.Get(parameters, "subject");
                if (subject is not null && (subject.Contains('\r', StringComparison.Ordinal) || subject.Contains('\n', StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("The subject has a line break - one line only.");
                }

                var replyTo = Values.Get(parameters, "reply_to") is { } original ? Id(original) : null;
                var to = Addresses(parameters, "to");
                if (to.Count == 0 && replyTo is null)
                {
                    throw new InvalidOperationException("Give whom it goes to - 'to'.");
                }

                var text = Values.Get(parameters, "text");
                var html = Values.Get(parameters, "html");
                if (text is null && html is null)
                {
                    throw new InvalidOperationException("Give its body - 'text', 'html' or both.");
                }

                return (new Outgoing(to, Addresses(parameters, "cc"), Addresses(parameters, "bcc"), subject, text, html, replyTo), null);
            }
            catch (InvalidOperationException ex)
            {
                return (null, ex.Message);
            }
        }

        private static IReadOnlyList<string> Addresses(IReadOnlyDictionary<string, string> parameters, string name)
        {
            var given = Values.Get(parameters, name);
            if (given is null)
            {
                return Array.Empty<string>();
            }

            if (!InternetAddressList.TryParse(given, out var list) || list.Count == 0 || list.Mailboxes.Any(mailbox => !mailbox.Address.Contains('@', StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"'{given}' is not a list of addresses - for {name}.");
            }

            return list.Mailboxes.Select(mailbox => mailbox.Address).ToList();
        }

        private static JsonArray Recipients(IEnumerable<string> addresses) =>
            new(addresses.Select(address => (JsonNode?)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = address } }).ToArray());

        // A draft made and sent - a new message, or a reply Outlook makes in the original's conversation - one record, its id; Graph's
        // refusal, or the connector's own, as a record.
        private static async Task<ConnectorRecord> SendMessageAsync(HttpClient client, string token, string box, Outgoing draft, CancellationToken cancellationToken)
        {
            try
            {
                var body = new JsonObject { ["contentType"] = draft.Html is not null ? "HTML" : "Text", ["content"] = draft.Html ?? draft.Text };
                JsonObject made;
                if (draft.ReplyTo is { } original)
                {
                    // Outlook makes the reply - its sender, "RE:" its subject, the original quoted below - the body given above it.
                    made = await SendAsync(client, token, HttpMethod.Post, $"{box}/messages/{Uri.EscapeDataString(original)}/createReply", new JsonObject { ["comment"] = draft.Html ?? draft.Text }, cancellationToken);
                    var id = Id(made["id"]?.GetValue<string>());
                    var changes = new JsonObject();
                    if (draft.To.Count > 0)
                    {
                        changes["toRecipients"] = Recipients(draft.To);
                    }

                    if (draft.Cc.Count > 0)
                    {
                        changes["ccRecipients"] = Recipients(draft.Cc);
                    }

                    if (draft.Bcc.Count > 0)
                    {
                        changes["bccRecipients"] = Recipients(draft.Bcc);
                    }

                    if (draft.Subject is not null)
                    {
                        changes["subject"] = draft.Subject;
                    }

                    if (changes.Count > 0)
                    {
                        made = await SendAsync(client, token, HttpMethod.Patch, $"{box}/messages/{Uri.EscapeDataString(id)}", changes, cancellationToken);
                    }
                }
                else
                {
                    made = await SendAsync(client, token, HttpMethod.Post, $"{box}/messages", new JsonObject
                    {
                        ["subject"] = draft.Subject ?? "",
                        ["body"] = body,
                        ["toRecipients"] = Recipients(draft.To),
                        ["ccRecipients"] = Recipients(draft.Cc),
                        ["bccRecipients"] = Recipients(draft.Bcc),
                    }, cancellationToken);
                }

                var sent = Id(made["id"]?.GetValue<string>());
                await SendAsync(client, token, HttpMethod.Post, $"{box}/messages/{Uri.EscapeDataString(sent)}/send", null, cancellationToken);
                return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = sent,
                    ["thread_id"] = made["conversationId"]?.GetValue<string>(),
                    ["message_id"] = made["internetMessageId"]?.GetValue<string>()?.Trim('<', '>'),
                });
            }
            catch (GraphRefusal refusal)
            {
                return ChatMessages.Refused("Microsoft Outlook", refusal.Message + (refusal.Reason is null ? "" : " " + refusal.Reason));
            }
            catch (InvalidOperationException ex)
            {
                return ChatMessages.Refused("Microsoft Outlook", ex.Message);
            }
        }

        // Message ids: a JSON list, or comma-separated.
        private static List<string> Ids(string? given)
        {
            if (string.IsNullOrWhiteSpace(given))
            {
                throw new InvalidOperationException("Give the ids of the messages.");
            }

            IEnumerable<string> ids;
            if (given.TrimStart().StartsWith('['))
            {
                try
                {
                    ids = (JsonNode.Parse(given) as JsonArray ?? new JsonArray()).Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : item?.ToJsonString() ?? "");
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The ids are not a JSON list.");
                }
            }
            else
            {
                ids = given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            return ids.Select(Id).Distinct(StringComparer.Ordinal).ToList();
        }

        private static string Id(string? id) =>
            id is not null && GraphId.IsMatch(id.Trim()) ? id.Trim() : throw new InvalidOperationException($"'{id}' is not an Outlook message id.");

        private static string Folder(string folder) =>
            GraphId.IsMatch(folder.Trim()) ? folder.Trim() : throw new InvalidOperationException($"'{folder}' is not a folder - inbox, sentitems, archive... or a folder's id.");

        // The mailbox's own address under Graph: the endpoint, users/, the mailbox held to an address's or a user id's shape.
        private static string MailboxUrl(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            var mailbox = Values.Get(settings, "mailbox") ?? throw new InvalidOperationException("Name the mailbox the app acts as - the connection's mailbox setting.");
            if (!Mailbox.IsMatch(mailbox))
            {
                throw new InvalidOperationException($"'{mailbox}' is not a mailbox - an address or a user id.");
            }

            return $"{Values.BaseUrl(endpoint, DefaultEndpoint)}/users/{Uri.EscapeDataString(mailbox)}";
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        // Graph's answer as JSON (an empty answer an empty object), a throttled call waiting as Graph says; a failure thrown with its
        // status and Graph's own code, its message beside it.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Microsoft Outlook answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = json?["error"]?["code"] is JsonValue word && word.TryGetValue<string>(out var text) ? text : null;
                var reason = json?["error"]?["message"] is JsonValue said && said.TryGetValue<string>(out var words) ? words : null;
                throw new GraphRefusal($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}.", reason);
            }

            return json as JsonObject ?? new JsonObject();
        }

        /// <summary>Graph's refusal: its status and code as the message, its own reason beside it (a send answers both).</summary>
        private sealed class GraphRefusal(string message, string? reason) : InvalidOperationException(message)
        {
            public string? Reason { get; } = reason;
        }
    }
}
