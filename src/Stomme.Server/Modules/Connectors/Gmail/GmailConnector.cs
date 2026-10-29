#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using Stomme.Modules.Connectors.Messaging;
using Stomme.Modules.Connectors.Shared;
using MimeKit;

namespace Stomme.Modules.Connectors.Gmail
{
    /// <summary>
    /// Gmail (n8n's Gmail node: messages got, sent, replied to and marked as read): the Gmail API
    /// (<c>https://gmail.googleapis.com/gmail/v1</c>) over the OAuth2 REFRESH-TOKEN scheme - a Google Cloud OAuth client's id and
    /// secret and a refresh token granted the Gmail scopes - exchanged at Google's token endpoint (the <c>token_url</c> setting)
    /// through the shared <see cref="OAuth2TokenProvider"/>, as the Google Sheets connector's is. A test reads the mailbox's address.
    /// </summary>
    /// <remarks>
    /// <c>search</c>: the messages Gmail's own search keeps (<c>query</c>, as in its search box - <c>from:ada is:unread</c>), in the
    /// labels named, newest first, at most <c>max</c>; each read whole and parsed (MimeKit): its id, thread, labels, headers, text
    /// and HTML (each cut at <c>body_max_chars</c>, said) and its attachments' names, types and sizes. <c>send</c>: a message to
    /// <c>to</c> (and <c>cc</c>, <c>bcc</c>) with a <c>subject</c> and a <c>text</c> and/or <c>html</c> body, built by MimeKit, so a
    /// header carries what it is given and nothing more; given <c>reply_to</c> - a message's id - it is sent in that message's
    /// thread, In-Reply-To and References set, to its sender when no <c>to</c> is given, "Re:" its subject when none is. A subject
    /// with a line break is refused. A send answers ONE record, as the chat connectors' message does: its <c>id</c>, or
    /// <c>refused</c> with why - a recipient that is not an address, Google's own reason - because the extraction port answers a
    /// thrown failure with its fixed words alone. <c>mark_read</c>: the messages with the ids given marked read. A message
    /// id is held to the letters, digits, '-' and '_' Gmail's are made of before it reaches the path. A throttled call waits as
    /// Google says; a failure of a search or a mark is thrown with its status and Google's own short status word
    /// (<c>HTTP 404 NOT_FOUND.</c>), the port answering it in its words.
    /// </remarks>
    public sealed class GmailConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://gmail.googleapis.com/gmail/v1";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string SearchOperation = "search";

        public const string SendOperation = "send";

        public const string MarkReadOperation = "mark_read";

        /// <summary>Where a label's history stands - what the Email received trigger asks when a pipeline is published.</summary>
        public const string StatusOperation = "status";

        /// <summary>The messages added to a label since a point in its history - what the Email received trigger reads.</summary>
        public const string ReadOperation = "read";

        /// <summary>A label's newest message - what the editor's Fetch test event shows.</summary>
        public const string NewestOperation = "newest";

        /// <summary>
        /// Gmail's history is never renumbered as an IMAP mailbox can be; a point too old to read from is answered as a mailbox made
        /// again would be (<c>renumbered</c>), so the trigger watches afresh.
        /// </summary>
        public const long UidValidity = 1;

        public const string Inbox = "INBOX";

        public const long DefaultAttachmentMaxBytes = 10 * 1024 * 1024;

        public const int DefaultMax = 50;

        public const int MostMax = 500;

        public const int DefaultBodyMaxChars = 100_000;

        private static readonly Regex MessageId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex LabelId = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "gmail";

        public string DisplayName => "Gmail";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Messaging };

        public Uri WebsiteUrl { get; } = new("https://mail.google.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/gmail/api/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Get messages", "The messages Gmail's search keeps, newest first - one record each: its id, thread, labels, headers, text, HTML and attachments - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "As in Gmail's search box - from:ada is:unread newer_than:2d. Empty: every message."),
                    new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Label ids, comma-separated - INBOX, UNREAD, or a label's own id. Empty: any."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: $"How many messages, at most {MostMax}.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                    new ConnectorConfigField("body_max_chars", "Most characters of a body", ConnectorConfigFieldType.Integer, Required: false, Description: "The text and the HTML each cut at this many characters.") { Default = DefaultBodyMaxChars.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(SendOperation, "Send a message", "Sends a message from the mailbox, or a reply in a message's thread - one record: its id, thread and labels - n8n's Send and Reply.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated. Empty on a reply: the sender of the message replied to."),
                    new ConnectorConfigField("cc", "Cc", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("bcc", "Bcc", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "One line. Empty on a reply: Re: the original's."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The plain-text body."),
                    new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "The HTML body; with a text one too, both are sent and the reader's mail program picks."),
                    new ConnectorConfigField("reply_to", "Reply to", ConnectorConfigFieldType.Text, Required: false, Description: "A message's id: sent as a reply in its thread."),
                }),
            },
            new ConnectorOperation(MarkReadOperation, "Mark as read", "Marks the messages with the ids given as read - one record saying how many - n8n's Mark as Read.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("ids", "Message ids", ConnectorConfigFieldType.Text, Required: true, Description: "Message ids, comma-separated, or a JSON list."),
                }),
            },
            new ConnectorOperation(StatusOperation, "Label status", "One record: where the label's history stands (uid_next) and how many messages the mailbox holds - what the Email received trigger asks when a pipeline is published.")
            {
                Parameters = new ConnectorConfigSchema(new[] { Mailbox }),
            },
            new ConnectorOperation(ReadOperation, "Read new messages", "The messages added to the label since a point in its history (after_uid, from Label status), oldest first - one record each, its uid the point it was added at - what the Email received trigger reads.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    Mailbox,
                    new ConnectorConfigField("after_uid", "After", ConnectorConfigFieldType.Integer, Required: true, Description: "A point in the label's history - Label status's uid_next less one, or a message's uid."),
                    new ConnectorConfigField("attachments", "Attachments", ConnectorConfigFieldType.Select, Required: false, Description: "names: each attachment's name, type and size. content: its bytes too, as base64.")
                    {
                        Default = "names",
                        Options = new[] { new ConnectorConfigFieldOption("names"), new ConnectorConfigFieldOption("content") },
                    },
                    new ConnectorConfigField("attachment_max_bytes", "Most bytes of an attachment", ConnectorConfigFieldType.Integer, Required: false, Description: "An attachment larger than this is named, its content left out.") { Default = DefaultAttachmentMaxBytes.ToString(CultureInfo.InvariantCulture) },
                    new ConnectorConfigField("body_max_chars", "Most characters of a body", ConnectorConfigFieldType.Integer, Required: false, Description: "The text and the HTML each cut at this many characters.") { Default = DefaultBodyMaxChars.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(NewestOperation, "Newest message", "The label's newest message, one record - what the editor's Fetch test event shows.")
            {
                Parameters = new ConnectorConfigSchema(new[] { Mailbox }),
            },
        };

        private static readonly ConnectorConfigField Mailbox = new("mailbox", "Label", ConnectorConfigFieldType.Text, Required: false, Description: "The label - INBOX, or a label's own id.") { Default = Inbox };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var profile = await SendAsync(client, token, HttpMethod.Get, baseUrl + "/users/me/profile", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {profile["emailAddress"]?.GetValue<string>() ?? "the mailbox"}.");
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
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/users/me";

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
                yield return await SendMessageAsync(client, token, baseUrl, draft, cancellationToken);
                yield break;
            }

            if (string.Equals(context.OperationName, MarkReadOperation, StringComparison.OrdinalIgnoreCase))
            {
                var ids = Ids(Values.Get(context.Parameters, "ids"));
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                await SendAsync(client, token, HttpMethod.Post, baseUrl + "/messages/batchModify", new JsonObject
                {
                    ["ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["removeLabelIds"] = new JsonArray("UNREAD"),
                }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["marked"] = (long)ids.Count });
                yield break;
            }

            if (string.Equals(context.OperationName, StatusOperation, StringComparison.OrdinalIgnoreCase))
            {
                var label = Label(context.Parameters);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var profile = await SendAsync(client, token, HttpMethod.Get, baseUrl + "/profile", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["mailbox"] = label,
                    ["uid_validity"] = UidValidity,
                    ["uid_next"] = History(profile["historyId"]) + 1,
                    ["messages"] = History(profile["messagesTotal"]),
                });
                yield break;
            }

            if (string.Equals(context.OperationName, ReadOperation, StringComparison.OrdinalIgnoreCase))
            {
                await foreach (var record in ReadNewAsync(context, baseUrl, cancellationToken))
                {
                    yield return record;
                }

                yield break;
            }

            if (string.Equals(context.OperationName, NewestOperation, StringComparison.OrdinalIgnoreCase))
            {
                var label = Label(context.Parameters);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var list = await SendAsync(client, token, HttpMethod.Get, $"{baseUrl}/messages?maxResults=1&labelIds={Uri.EscapeDataString(label)}", null, cancellationToken);
                if ((list["messages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>() is { } newest
                    && await MessageAsync(client, token, baseUrl, Id(newest), cancellationToken) is { } message)
                {
                    yield return await RecordAsync(message, DefaultBodyMaxChars, cancellationToken, History(message["historyId"]), content: false, attachmentMax: 0);
                }

                yield break;
            }

            var max = Math.Clamp(int.TryParse(Values.Get(context.Parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) ? given : DefaultMax, 1, MostMax);
            var bodyMax = int.TryParse(Values.Get(context.Parameters, "body_max_chars"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultBodyMaxChars;
            var labels = (Values.Get(context.Parameters, "labels") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(label => LabelId.IsMatch(label) ? label : throw new InvalidOperationException($"'{label}' is not a label id - INBOX, UNREAD, or a label's own id."))
                .ToList();
            var query = Values.Get(context.Parameters, "query");
            var accessToken = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var http = context.Egress.CreateHttpClient(context.Timeout);
            var read = 0;
            string? pageToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parameters = new List<string> { "maxResults=" + Math.Min(100, max - read).ToString(CultureInfo.InvariantCulture) };
                if (query is not null)
                {
                    parameters.Add("q=" + Uri.EscapeDataString(query));
                }

                parameters.AddRange(labels.Select(label => "labelIds=" + Uri.EscapeDataString(label)));
                if (pageToken is not null)
                {
                    parameters.Add("pageToken=" + Uri.EscapeDataString(pageToken));
                }

                var page = await SendAsync(http, accessToken, HttpMethod.Get, baseUrl + "/messages?" + string.Join("&", parameters), null, cancellationToken);
                foreach (var listed in (page["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (read >= max)
                    {
                        break;
                    }

                    var id = Id(listed["id"]?.GetValue<string>());
                    var message = await SendAsync(http, accessToken, HttpMethod.Get, $"{baseUrl}/messages/{id}?format=raw", null, cancellationToken);
                    read++;
                    yield return await RecordAsync(message, bodyMax, cancellationToken);
                }

                pageToken = read < max ? page["nextPageToken"]?.GetValue<string>() : null;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }

        // The messages added to the label since a point in its history, oldest first, each record's uid the point it was added at;
        // a point Gmail no longer keeps answered as a mailbox made again would be - one record, renumbered, where it stands now.
        // A history record adding several messages hands each on with its one point.
        private async IAsyncEnumerable<ConnectorRecord> ReadNewAsync(ConnectorExtractContext context, string baseUrl, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var label = Label(context.Parameters);
            var after = long.TryParse(Values.Get(context.Parameters, "after_uid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var point) && point > 0
                ? point
                : throw new InvalidOperationException("Give after_uid - a point in the label's history, Label status's uid_next less one.");
            var content = string.Equals(Values.Get(context.Parameters, "attachments"), "content", StringComparison.OrdinalIgnoreCase);
            var attachmentMax = long.TryParse(Values.Get(context.Parameters, "attachment_max_bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0 ? bytes : DefaultAttachmentMaxBytes;
            var bodyMax = int.TryParse(Values.Get(context.Parameters, "body_max_chars"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultBodyMaxChars;
            var renumbered = Values.Get(context.Parameters, "uid_validity") is { } validity && validity != UidValidity.ToString(CultureInfo.InvariantCulture);
            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            string? pageToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = renumbered ? null : await HistoryPageAsync(client, token, baseUrl, after, label, pageToken, cancellationToken);
                if (page is null)
                {
                    // A point too old for Gmail's history (or read under another numbering): watched afresh from where it stands now.
                    var profile = await SendAsync(client, token, HttpMethod.Get, baseUrl + "/profile", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["renumbered"] = true,
                        ["uid_validity"] = UidValidity,
                        ["uid_next"] = History(profile["historyId"]) + 1,
                    });
                    yield break;
                }

                foreach (var change in (page["history"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().OrderBy(change => History(change["id"])))
                {
                    var uid = History(change["id"]);
                    foreach (var added in (change["messagesAdded"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var listed = added["message"] as JsonObject;
                        var labels = (listed?["labelIds"] as JsonArray ?? new JsonArray()).Select(item => item?.GetValue<string>()).ToList();
                        if (listed?["id"]?.GetValue<string>() is not { } id || !labels.Contains(label, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        // A message gone since it was added (deleted, or moved out of reach) is passed over.
                        if (await MessageAsync(client, token, baseUrl, Id(id), cancellationToken) is { } message)
                        {
                            yield return await RecordAsync(message, bodyMax, cancellationToken, uid, content, attachmentMax);
                        }
                    }
                }

                pageToken = page["nextPageToken"]?.GetValue<string>();
            }
            while (!string.IsNullOrEmpty(pageToken));
        }

        // What to send, read and checked before anything is asked of Google.
        private sealed record Outgoing(InternetAddressList To, InternetAddressList Cc, InternetAddressList Bcc, string? Subject, string? Text, string? Html, string? ReplyTo);

        // The message to send, or why not - in words an author can act on, answered as a record (a thrown failure reaches the
        // author as the port's fixed words alone).
        private static (Outgoing? Draft, string? Problem) Draft(IReadOnlyDictionary<string, string> parameters)
        {
            try
            {
                return (Read(parameters), null);
            }
            catch (InvalidOperationException ex)
            {
                return (null, ex.Message);
            }
        }

        private static Outgoing Read(IReadOnlyDictionary<string, string> parameters)
        {
            var subject = Values.Get(parameters, "subject");
            if (subject is not null && (subject.Contains('\r', StringComparison.Ordinal) || subject.Contains('\n', StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The subject has a line break - one line only.");
            }

            var text = Values.Get(parameters, "text");
            var html = Values.Get(parameters, "html");
            var replyTo = Values.Get(parameters, "reply_to") is { } original ? Id(original) : null;
            var to = Addresses(parameters, "to");
            if (to.Count == 0 && replyTo is null)
            {
                throw new InvalidOperationException("Give whom it goes to - 'to'.");
            }

            if (text is null && html is null)
            {
                throw new InvalidOperationException("Give its body - 'text', 'html' or both.");
            }

            return new Outgoing(to, Addresses(parameters, "cc"), Addresses(parameters, "bcc"), subject, text, html, replyTo);
        }

        private static InternetAddressList Addresses(IReadOnlyDictionary<string, string> parameters, string name)
        {
            var given = Values.Get(parameters, name);
            if (given is null)
            {
                return new InternetAddressList();
            }

            if (!InternetAddressList.TryParse(given, out var list) || list.Count == 0 || list.Mailboxes.Any(mailbox => !mailbox.Address.Contains('@', StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"'{given}' is not a list of addresses - for {name}.");
            }

            return list;
        }

        // The message sent - one record, its id - or Google's refusal as a record, its status word and its own reason.
        private static async Task<ConnectorRecord> SendMessageAsync(HttpClient client, string token, string baseUrl, Outgoing draft, CancellationToken cancellationToken)
        {
            try
            {
                return await BuildAndSendAsync(client, token, baseUrl, draft, cancellationToken);
            }
            catch (GmailRefusal refusal)
            {
                return ChatMessages.Refused("Gmail", refusal.Message + (refusal.Reason is null ? "" : " " + refusal.Reason));
            }
            catch (InvalidOperationException ex)
            {
                return ChatMessages.Refused("Gmail", ex.Message);
            }
        }

        private static async Task<ConnectorRecord> BuildAndSendAsync(HttpClient client, string token, string baseUrl, Outgoing draft, CancellationToken cancellationToken)
        {
            var message = new MimeMessage();
            message.To.AddRange(draft.To);
            message.Cc.AddRange(draft.Cc);
            message.Bcc.AddRange(draft.Bcc);
            message.Subject = draft.Subject ?? "";
            var send = new JsonObject();
            if (draft.ReplyTo is { } replyTo)
            {
                var original = await SendAsync(client, token, HttpMethod.Get, $"{baseUrl}/messages/{replyTo}?format=metadata&metadataHeaders=Message-ID&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Reply-To&metadataHeaders=References", null, cancellationToken);
                var headers = (original["payload"]?["headers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .GroupBy(header => header["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First()["value"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);
                if (headers.TryGetValue("Message-ID", out var messageId) && messageId.Length > 0)
                {
                    message.InReplyTo = messageId.Trim('<', '>'); // MimeKit takes an id without its brackets and writes them
                    foreach (var reference in (headers.TryGetValue("References", out var references) ? references : "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        message.References.Add(reference.Trim('<', '>'));
                    }

                    message.References.Add(messageId.Trim('<', '>'));
                }

                if (draft.To.Count == 0 && InternetAddressList.TryParse(headers.TryGetValue("Reply-To", out var back) && back.Length > 0 ? back : headers.GetValueOrDefault("From", ""), out var sender))
                {
                    message.To.AddRange(sender);
                }

                if (draft.Subject is null)
                {
                    var subject = headers.GetValueOrDefault("Subject", "");
                    message.Subject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : "Re: " + subject;
                }

                if (original["threadId"]?.GetValue<string>() is { } thread)
                {
                    send["threadId"] = thread;
                }
            }

            if (message.To.Count == 0)
            {
                throw new InvalidOperationException("The message replied to names no sender - give 'to'.");
            }

            message.Body = new BodyBuilder { TextBody = draft.Text, HtmlBody = draft.Html }.ToMessageBody();
            using var bytes = new MemoryStream();
            await message.WriteToAsync(bytes, cancellationToken);
            send["raw"] = Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var sent = await SendAsync(client, token, HttpMethod.Post, baseUrl + "/messages/send", send, cancellationToken);
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = sent["id"]?.GetValue<string>(),
                ["thread_id"] = sent["threadId"]?.GetValue<string>(),
                ["labels"] = (sent["labelIds"] as JsonArray ?? new JsonArray()).ToJsonString(),
            });
        }

        // A message as a record: Gmail's own fields, then the message itself, parsed - the fields an IMAP connector's read gives, so
        // the Email received trigger hands it on as it does any; given a uid, the point in the label's history it was added at.
        // Each attachment named with its type and size; its bytes too, as base64, when asked and no larger than attachmentMax.
        private static async Task<ConnectorRecord> RecordAsync(JsonObject gmail, int bodyMax, CancellationToken cancellationToken, long? uid = null, bool content = false, long attachmentMax = 0)
        {
            var raw = gmail["raw"]?.GetValue<string>() ?? "";
            var padded = raw.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var bytes = Convert.FromBase64String(padded);
            using var stream = new MemoryStream(bytes);
            using var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            var text = message.TextBody;
            var html = message.HtmlBody;
            var attachments = new JsonArray();
            foreach (var part in message.Attachments.OfType<MimePart>())
            {
                using var decoded = new MemoryStream();
                part.Content?.DecodeTo(decoded, cancellationToken);
                var item = new JsonObject
                {
                    ["name"] = part.FileName ?? "attachment",
                    ["contentType"] = part.ContentType.MimeType,
                    ["size"] = decoded.Length,
                };
                if (content)
                {
                    if (decoded.Length <= attachmentMax)
                    {
                        item["content"] = Convert.ToBase64String(decoded.GetBuffer(), 0, (int)decoded.Length);
                    }
                    else
                    {
                        item["skipped"] = $"larger than {attachmentMax} bytes";
                    }
                }

                attachments.Add(item);
            }

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (uid is { } point)
            {
                fields["uid"] = point;
                fields["uid_validity"] = UidValidity;
            }

            foreach (var (name, value) in new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = gmail["id"]?.GetValue<string>(),
                ["thread_id"] = gmail["threadId"]?.GetValue<string>(),
                ["labels"] = (gmail["labelIds"] as JsonArray ?? new JsonArray()).ToJsonString(),
                ["snippet"] = gmail["snippet"]?.GetValue<string>(),
                ["message_id"] = message.MessageId,
                ["date"] = message.Date == DateTimeOffset.MinValue ? null : message.Date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["from"] = message.From.ToString(),
                ["to"] = message.To.ToString(),
                ["cc"] = message.Cc.ToString(),
                ["reply_to"] = message.ReplyTo.ToString(),
                ["subject"] = message.Subject ?? "",
                ["text"] = Cut(text, bodyMax),
                ["html"] = Cut(html, bodyMax),
                ["truncated"] = (text?.Length ?? 0) > bodyMax || (html?.Length ?? 0) > bodyMax,
                ["size"] = (long)bytes.Length,
                ["attachments"] = attachments.ToJsonString(Readable),
            })
            {
                fields[name] = value;
            }

            return new ConnectorRecord(fields);
        }

        // The label named, held to Gmail's letters before it reaches an address; INBOX when none is.
        private static string Label(IReadOnlyDictionary<string, string> parameters) =>
            Values.Get(parameters, "mailbox") is not { } label ? Inbox
            : LabelId.IsMatch(label) ? label
            : throw new InvalidOperationException($"'{label}' is not a label id - INBOX, UNREAD, or a label's own id.");

        // A history id (or a count) as Gmail writes it - a number, or a number in a string; 0 when there is none.
        private static long History(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<long>(out var number) => number,
            JsonValue value when value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        // A page of the label's history since a point: the messages added to it; null when Gmail no longer keeps that point (404).
        private static async Task<JsonObject?> HistoryPageAsync(HttpClient client, string token, string baseUrl, long after, string label, string? pageToken, CancellationToken cancellationToken)
        {
            try
            {
                return await SendAsync(client, token, HttpMethod.Get, $"{baseUrl}/history?startHistoryId={after.ToString(CultureInfo.InvariantCulture)}&historyTypes=messageAdded&labelId={Uri.EscapeDataString(label)}&maxResults=100"
                    + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)), null, cancellationToken);
            }
            catch (GmailRefusal refusal) when (refusal.Status == 404)
            {
                return null;
            }
        }

        // A message, raw; null when it is gone (404).
        private static async Task<JsonObject?> MessageAsync(HttpClient client, string token, string baseUrl, string id, CancellationToken cancellationToken)
        {
            try
            {
                return await SendAsync(client, token, HttpMethod.Get, $"{baseUrl}/messages/{id}?format=raw", null, cancellationToken);
            }
            catch (GmailRefusal refusal) when (refusal.Status == 404)
            {
                return null;
            }
        }

        private static string? Cut(string? text, int most) => text is null || text.Length <= most ? text : text[..most];

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
            id is not null && MessageId.IsMatch(id.Trim()) ? id.Trim() : throw new InvalidOperationException($"'{id}' is not a Gmail message id.");

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON (an empty answer an empty object), a throttled call waiting as Google says; a failure thrown with its
        // status and Google's own short status word.
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
                throw new InvalidOperationException($"Gmail answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = json?["error"]?["status"] is JsonValue word && word.TryGetValue<string>(out var text) ? text : null;
                var reason = json?["error"]?["message"] is JsonValue said && said.TryGetValue<string>(out var words) ? words : null;
                throw new GmailRefusal((int)response.StatusCode, $"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}.", reason);
            }

            return json as JsonObject ?? new JsonObject();
        }

        /// <summary>Google's refusal: its status and short word as the message, its own reason beside it (a send answers both).</summary>
        private sealed class GmailRefusal(int status, string message, string? reason) : InvalidOperationException(message)
        {
            public int Status { get; } = status;

            public string? Reason { get; } = reason;
        }
    }
}
