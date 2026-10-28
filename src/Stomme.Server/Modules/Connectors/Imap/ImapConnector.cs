#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Stomme.Modules.Connectors.Imap
{
    /// <summary>
    /// The IMAP connector (BL-229, for n8n's Email Trigger): a mailbox on an IMAP server - the connection's endpoint names it
    /// (<c>imap.example.com</c>, <c>imap.example.com:993</c>, <c>imaps://...</c>), TLS from the first byte by default (<c>encryption</c> ssl; starttls; none for a trusted network only), the Basic scheme's
    /// <c>username</c>/<c>password</c>. A test connects, signs in and leaves. Three operations: <c>status</c> - the mailbox's
    /// UIDVALIDITY and next UID, the anchor a watcher starts from; <c>read</c> - the messages with a UID after <c>after_uid</c>,
    /// oldest first, each its headers, its text and HTML (each at most <c>body_max_chars</c>) and its attachments (names and
    /// sizes, or their content as base64 with <c>attachments</c> content, each at most <c>attachment_max_bytes</c>); and
    /// <c>mark_read</c> - the <c>uids</c> given flagged \Seen. Reading never marks anything: the extraction port reads one
    /// record past its cap to say it was reached, so a message marked as it was read could be one nobody received - a watcher
    /// marks what it handed on, after. A <c>read</c> given <c>uid_validity</c> that the mailbox no longer has (it was made
    /// again, its UIDs renumbered) yields ONE record saying so - <c>renumbered</c> true, the new UIDVALIDITY and next UID -
    /// and no message, since after_uid means nothing in the new numbering.
    /// </summary>
    /// <remarks>
    /// The socket dials only the address the host pinned (<see cref="IConnectorEgress.ResolveAndPinAsync"/>), and MailKit is
    /// handed the ORIGINAL host name, so TLS names the server the certificate must name; <c>danger_accept_any_certificate</c>
    /// is a per-connection setting, off by default, as the FTP and SQL Server connectors have it. A message larger than
    /// <c>message_max_bytes</c> is not downloaded: its record carries its UID and size and <c>too_large</c>, so a watcher
    /// moves past it and says so.
    /// </remarks>
    public sealed class ImapConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultBodyMaxChars = 1_000_000;
        public const long DefaultAttachmentMaxBytes = 10L * 1024 * 1024;
        public const long DefaultMessageMaxBytes = 25L * 1024 * 1024;

        public string ConnectorType => "imap";

        public string DisplayName => "IMAP (e-mail)";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The endpoint is the server - imap.example.com, imap.example.com:993, imaps://imap.example.com - not an http URL; a credential written into it is refused (D110).</summary>
        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string EndpointExample => "imaps://imap.example.com (or imap.example.com:993)";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("port", "Port", ConnectorConfigFieldType.Integer, Required: false, Description: "The IMAP port, when the endpoint names none. Empty: 993 with ssl, 143 otherwise."),
            new ConnectorConfigField("encryption", "Encryption", ConnectorConfigFieldType.Select, Required: false,
                Description: "ssl: TLS from the first byte (port 993) - the default, and an imaps:// endpoint's. starttls: plain, then upgraded to TLS (port 143) - an imap:// endpoint's, refused when the server cannot. none: no TLS at all - a trusted network only.")
            {
                Default = "ssl",
                Options = new[] { new ConnectorConfigFieldOption("ssl"), new ConnectorConfigFieldOption("starttls"), new ConnectorConfigFieldOption("none") },
            },
            new ConnectorConfigField("danger_accept_any_certificate", "Accept any TLS certificate", ConnectorConfigFieldType.Boolean, Required: false,
                Description: "Skips server-certificate validation for this connection only. Dangerous - use solely for trusted hosts with self-signed certificates.")
            {
                Default = "false",
            },
        });

        private static readonly ConnectorConfigField Mailbox = new("mailbox", "Mailbox", ConnectorConfigFieldType.Text, Required: false, Description: "The mailbox (folder) - INBOX, or another by its full name.") { Default = "INBOX" };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("read", "Read messages", "The messages with a UID after after_uid, oldest first - one record per message: its UID, headers, text, HTML and attachments.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    Mailbox,
                    new ConnectorConfigField("after_uid", "After UID", ConnectorConfigFieldType.Integer, Required: false, Description: "Only messages with a higher UID. 0: every message.") { Default = "0" },
                    new ConnectorConfigField("uid_validity", "UIDVALIDITY", ConnectorConfigFieldType.Text, Required: false, Description: "The mailbox's UIDVALIDITY after_uid was read under; when the mailbox has another, one record says it was renumbered and no message is read."),
                    new ConnectorConfigField("attachments", "Attachments", ConnectorConfigFieldType.Select, Required: false, Description: "names: each attachment's name, type and size. content: its bytes too, as base64.")
                    {
                        Default = "names",
                        Options = new[] { new ConnectorConfigFieldOption("names"), new ConnectorConfigFieldOption("content") },
                    },
                    new ConnectorConfigField("body_max_chars", "Most characters of a body", ConnectorConfigFieldType.Integer, Required: false, Description: "The text and the HTML each cut at this many characters.") { Default = DefaultBodyMaxChars.ToString(CultureInfo.InvariantCulture) },
                    new ConnectorConfigField("attachment_max_bytes", "Most bytes of an attachment", ConnectorConfigFieldType.Integer, Required: false, Description: "An attachment larger than this is named, its content left out.") { Default = DefaultAttachmentMaxBytes.ToString(CultureInfo.InvariantCulture) },
                    new ConnectorConfigField("message_max_bytes", "Most bytes of a message", ConnectorConfigFieldType.Integer, Required: false, Description: "A message larger than this is not downloaded: its record says too_large.") { Default = DefaultMessageMaxBytes.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("status", "Mailbox status", "One record: the mailbox's UIDVALIDITY, its next UID and how many messages it holds.")
            {
                Parameters = new ConnectorConfigSchema(new[] { Mailbox }),
            },
            new ConnectorOperation("mark_read", "Mark as read", "Flags the messages with the UIDs given as read (\\Seen) - one record saying how many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    Mailbox,
                    new ConnectorConfigField("uids", "UIDs", ConnectorConfigFieldType.Text, Required: true, Description: "The messages' UIDs, separated by commas."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ImapSessionOptions options;
            try
            {
                options = ResolveOptions(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }

            try
            {
                using var client = await OpenAsync(options, context.Egress, cancellationToken).ConfigureAwait(false);
                await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
                return ConnectionTestResult.Ok("Signed in to the IMAP server.");
            }
            catch (MailKit.Security.AuthenticationException)
            {
                return ConnectionTestResult.Fail("The IMAP server refused the username or password.");
            }
            catch (Exception ex) when (Unreachable(ex) && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail($"Could not reach the IMAP server: {ex.Message}");
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var options = ResolveOptions(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "read" : context.OperationName.Trim().ToLowerInvariant();
            if (operation is not ("read" or "status" or "mark_read"))
            {
                throw new InvalidOperationException($"The IMAP connector has no operation '{context.OperationName}' - read, status or mark_read.");
            }

            var mailboxName = Parameter(context.Parameters, "mailbox") ?? "INBOX";
            using var client = await OpenAsync(options, context.Egress, cancellationToken).ConfigureAwait(false);
            var mailbox = string.Equals(mailboxName, "INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(mailboxName, cancellationToken).ConfigureAwait(false);
            await mailbox.OpenAsync(operation == "mark_read" ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            switch (operation)
            {
                case "status":
                    yield return new ConnectorRecord(new Dictionary<string, object?>
                    {
                        ["mailbox"] = mailbox.FullName,
                        ["uid_validity"] = (long)mailbox.UidValidity,
                        ["uid_next"] = (long)(mailbox.UidNext?.Id ?? 0),
                        ["messages"] = mailbox.Count,
                    });
                    break;
                case "mark_read":
                    var uids = Uids(Parameter(context.Parameters, "uids"));
                    if (uids.Count > 0)
                    {
                        await mailbox.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, cancellationToken).ConfigureAwait(false);
                    }

                    yield return new ConnectorRecord(new Dictionary<string, object?> { ["marked"] = uids.Count });
                    break;
                default:
                    await foreach (var record in ReadAsync(mailbox, context.Parameters, cancellationToken).ConfigureAwait(false))
                    {
                        yield return record;
                    }

                    break;
            }

            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }

        private static async IAsyncEnumerable<ConnectorRecord> ReadAsync(IMailFolder mailbox, IReadOnlyDictionary<string, string> parameters, [EnumeratorCancellation] CancellationToken ct)
        {
            var validity = (long)mailbox.UidValidity;
            if (Parameter(parameters, "uid_validity") is { } expected && long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known) && known != validity)
            {
                yield return new ConnectorRecord(new Dictionary<string, object?>
                {
                    ["renumbered"] = true,
                    ["uid_validity"] = validity,
                    ["uid_next"] = (long)(mailbox.UidNext?.Id ?? 0),
                });
                yield break;
            }

            var after = Number(parameters, "after_uid", 0, 0, uint.MaxValue - 1);
            var bodyMax = (int)Number(parameters, "body_max_chars", DefaultBodyMaxChars, 0, int.MaxValue);
            var attachmentMax = Number(parameters, "attachment_max_bytes", DefaultAttachmentMaxBytes, 0, long.MaxValue);
            var messageMax = Number(parameters, "message_max_bytes", DefaultMessageMaxBytes, 1, long.MaxValue);
            var content = string.Equals(Parameter(parameters, "attachments"), "content", StringComparison.OrdinalIgnoreCase);

            // "n:*" holds the highest UID even when it is not above n (RFC 3501), so the UIDs are held to after_uid here too.
            var found = await mailbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)after + 1), UniqueId.MaxValue)), ct).ConfigureAwait(false);
            foreach (var uid in found.Where(uid => uid.Id > after).OrderBy(uid => uid.Id))
            {
                var summary = (await mailbox.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, ct).ConfigureAwait(false)).FirstOrDefault();
                var size = (long)(summary?.Size ?? 0);
                if (size > messageMax)
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>
                    {
                        ["uid"] = (long)uid.Id,
                        ["uid_validity"] = validity,
                        ["size"] = size,
                        ["too_large"] = true,
                    });
                    continue;
                }

                using var message = await mailbox.GetMessageAsync(uid, ct).ConfigureAwait(false);
                yield return new ConnectorRecord(Fields(message, uid, validity, size, bodyMax, content, attachmentMax));
            }
        }

        /// <summary>A message as a record: its UID, headers, bodies (each cut at the limit, said) and attachments as JSON text.</summary>
        internal static Dictionary<string, object?> Fields(MimeMessage message, UniqueId uid, long validity, long size, int bodyMax, bool content, long attachmentMax)
        {
            var text = message.TextBody;
            var html = message.HtmlBody;
            var attachments = new JsonArray();
            foreach (var part in message.Attachments.OfType<MimePart>())
            {
                using var bytes = new MemoryStream();
                part.Content?.DecodeTo(bytes);
                var item = new JsonObject
                {
                    ["name"] = part.FileName ?? "attachment",
                    ["contentType"] = part.ContentType.MimeType,
                    ["size"] = bytes.Length,
                };
                if (content)
                {
                    if (bytes.Length <= attachmentMax)
                    {
                        item["content"] = Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length);
                    }
                    else
                    {
                        item["skipped"] = $"larger than {attachmentMax} bytes";
                    }
                }

                attachments.Add(item);
            }

            return new Dictionary<string, object?>
            {
                ["uid"] = (long)uid.Id,
                ["uid_validity"] = validity,
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
                ["size"] = size,
                ["attachments"] = attachments.ToJsonString(Readable),
            };
        }

        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static string? Cut(string? text, int most) => text is null || text.Length <= most ? text : text[..most];

        /// <summary>
        /// A client signed in on the PINNED address - MailKit dials it, as FluentFTP dials the FTP connector's - TLS as the settings
        /// say, the certificate held to the ORIGINAL host name (<see cref="IsAcceptable"/>): the pin changes where the socket
        /// goes, not whom the certificate must name. Dialled by address, the TLS handshake names no server (no SNI), as the FTP
        /// connector's does.
        /// </summary>
        private static async Task<ImapClient> OpenAsync(ImapSessionOptions options, IConnectorEgress egress, CancellationToken ct)
        {
            var pinned = await egress.ResolveAndPinAsync(options.Host, options.Port, ct).ConfigureAwait(false)
                ?? throw new ConnectorEgressDeniedException($"The IMAP host '{options.Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            var client = new ImapClient
            {
                Timeout = (int)Math.Clamp(options.Timeout.TotalMilliseconds, 1_000, int.MaxValue),
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificateValidationCallback = (_, certificate, _, errors) => options.DangerAcceptAnyCertificate || IsAcceptable(certificate, errors, options.Host),
            };
            try
            {
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connecting.CancelAfter(options.Timeout);
                await client.ConnectAsync(pinned.ToString(), options.Port, options.Encryption, connecting.Token).ConfigureAwait(false);
                await client.AuthenticateAsync(options.Username, options.Password, ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>A certificate is acceptable when it validates, or when its only fault is the name - checked against the original host.</summary>
        internal static bool IsAcceptable(X509Certificate? certificate, SslPolicyErrors errors, string originalHost)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (errors != SslPolicyErrors.RemoteCertificateNameMismatch || certificate is null)
            {
                return false;
            }

            using var named = new X509Certificate2(certificate);
            return named.MatchesHostname(originalHost);
        }

        private static bool Unreachable(Exception ex) =>
            ex is SocketException or IOException or TimeoutException or OperationCanceledException or ImapProtocolException or ImapCommandException or SslHandshakeException or ServiceNotConnectedException or InvalidOperationException;

        /// <summary>
        /// The session inputs: the server from the connection's endpoint, the port and encryption from the settings (else the
        /// endpoint's, else 993 with ssl and 143 otherwise), the certificate setting, the scheme's credentials, the timeout - or
        /// what is wrong.
        /// </summary>
        internal static ImapSessionOptions ResolveOptions(string? endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout)
        {
            var (host, endpointPort, scheme) = Server(endpoint);
            var named = settings.TryGetValue("encryption", out var mode) && !string.IsNullOrWhiteSpace(mode) ? mode.Trim().ToLowerInvariant() : scheme == "imap" ? "starttls" : "ssl";
            var encryption = named switch
            {
                "ssl" => SecureSocketOptions.SslOnConnect,
                "starttls" => SecureSocketOptions.StartTls,
                "none" => SecureSocketOptions.None,
                var other => throw new InvalidOperationException($"The 'encryption' setting '{other}' must be one of: ssl, starttls, none."),
            };
            var port = endpointPort ?? (encryption == SecureSocketOptions.SslOnConnect ? 993 : 143);
            if (settings.TryGetValue("port", out var portValue) && !string.IsNullOrWhiteSpace(portValue) &&
                (!int.TryParse(portValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            {
                throw new InvalidOperationException($"The 'port' setting '{portValue}' is not a valid TCP port.");
            }

            if (!credentials.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("The IMAP connector requires a username/password credential; no username was supplied.");
            }

            credentials.TryGetValue("password", out var password);
            var acceptAny = settings.TryGetValue("danger_accept_any_certificate", out var flag) && bool.TryParse(flag, out var parsed) && parsed;
            return new ImapSessionOptions(host, port, encryption, acceptAny, username, password ?? string.Empty, timeout);
        }

        /// <summary>The server a connection's endpoint names - imap.example.com, imap.example.com:993, imaps://imap.example.com - or what is wrong.</summary>
        internal static (string Host, int? Port, string? Scheme) Server(string? endpoint)
        {
            var text = (endpoint ?? "").Trim();
            const string Expected = "imap.example.com, imap.example.com:993 or imaps://imap.example.com";
            if (text.Length == 0)
            {
                throw new InvalidOperationException($"The connection's endpoint is the IMAP server: {Expected}.");
            }

            if (text.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("imap" or "imaps") || uri.Host.Length == 0 || uri.AbsolutePath.Trim('/').Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{text}' is not an IMAP server: {Expected}.");
                }

                return (uri.Host, uri.Port > 0 ? uri.Port : null, uri.Scheme);
            }

            var colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                return int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 && Uri.CheckHostName(text[..colon]) != UriHostNameType.Unknown
                    ? (text[..colon], port, null)
                    : throw new InvalidOperationException($"'{text}' is not an IMAP server: {Expected}.");
            }

            return Uri.CheckHostName(text) != UriHostNameType.Unknown ? (text, null, null) : throw new InvalidOperationException($"'{text}' is not an IMAP server: {Expected}.");
        }

        private static string? Parameter(IReadOnlyDictionary<string, string> parameters, string name) =>
            parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        private static long Number(IReadOnlyDictionary<string, string> parameters, string name, long fallback, long least, long most)
        {
            if (Parameter(parameters, name) is not { } text)
            {
                return fallback;
            }

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= least && value <= most
                ? value
                : throw new InvalidOperationException($"The '{name}' parameter '{text}' must be a whole number from {least} to {most}.");
        }

        /// <summary>UIDs written as "4, 5,6" - each a positive whole number - or what is wrong.</summary>
        internal static List<UniqueId> Uids(string? text)
        {
            var uids = new List<UniqueId>();
            foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!uint.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id == 0)
                {
                    throw new InvalidOperationException($"'{part}' is not a message's UID.");
                }

                uids.Add(new UniqueId(id));
            }

            return uids;
        }
    }

    /// <summary>The resolved IMAP session inputs a client is opened with.</summary>
    internal sealed record ImapSessionOptions(string Host, int Port, SecureSocketOptions Encryption, bool DangerAcceptAnyCertificate, string Username, string Password, TimeSpan Timeout);
}
