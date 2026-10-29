#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Imap;
using MimeKit;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The IMAP connector over MailKit for real, against a scripted IMAP server on loopback: a test signs in or says the
    /// password was refused; status gives the mailbox's UIDVALIDITY and next UID; read gives the messages after a UID - headers,
    /// text, HTML, attachments by name or with their content, bounded - and never one at or below it (an IMAP server's "n:*"
    /// holds the highest UID even below n); a read under another UIDVALIDITY says the mailbox was renumbered and reads nothing;
    /// a message too large is not downloaded; reading marks nothing, mark_read marks the UIDs given; the host's pin is the
    /// address dialled, and a refused host is the contract's denial.
    /// </summary>
    public sealed class ImapConnectorTests : IDisposable
    {
        private const string User = "ops@example.com";
        private const string Password = "imap-test-password";
        private readonly ScriptedImapServer _server = new(User, Password);
        private readonly ImapConnector _connector = new();

        public void Dispose() => _server.Dispose();

        private const string Endpoint = "mail.example.com";

        private Dictionary<string, string> Settings => new() { ["port"] = _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["encryption"] = "none" };

        private static Dictionary<string, string> Credentials(string password = Password) => new() { ["username"] = User, ["password"] = password };

        private async Task<List<IReadOnlyDictionary<string, object?>>> ExtractAsync(string operation, Dictionary<string, string>? parameters = null, StubEgress? egress = null)
        {
            var context = new ConnectorExtractContext(Endpoint, Settings, TimeSpan.FromSeconds(10), egress ?? new StubEgress(), operation, parameters ?? new Dictionary<string, string>())
            {
                Credentials = Credentials(),
            };
            var records = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var record in _connector.ExtractAsync(context))
            {
                records.Add(record.Fields);
            }

            return records;
        }

        private static MimeMessage Message(string subject, string text, string? html = null, (string Name, byte[] Bytes)? attachment = null)
        {
            var message = new MimeMessage { Subject = subject, MessageId = subject.Replace(' ', '-') + "@example.com", Date = new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero) };
            message.From.Add(new MailboxAddress("Grace Supplier", "grace@supplier.example"));
            message.To.Add(new MailboxAddress("Orders", "orders@example.com"));
            var body = new BodyBuilder { TextBody = text, HtmlBody = html };
            if (attachment is { } file)
            {
                body.Attachments.Add(file.Name, file.Bytes, new ContentType("application", "pdf"));
            }

            message.Body = body.ToMessageBody();
            return message;
        }

        [Fact]
        public void Declarations_say_imap_with_basic_auth_and_its_three_operations()
        {
            Assert.Equal(("imap", AuthenticationType.Basic), (_connector.ConnectorType, _connector.AuthType));
            Assert.Equal(new[] { "read", "status", "mark_read" }, _connector.Operations.Select(operation => operation.Name));
            Assert.Equal(ConnectorEndpointKind.ConnectionString, _connector.EndpointKind);
            // The form shows an IMAP server as the example, not the kind's database connection string.
            Assert.Equal("imaps://imap.example.com (or imap.example.com:993)", Stomme.Modules.Connectivity.Application.ConnectorViews.From(_connector).EndpointExample);
            Assert.Equal(new[] { "port", "encryption", "danger_accept_any_certificate" }, _connector.ConfigSchema.Fields.Select(field => field.Name));
            ImapSessionOptions Resolve(string endpoint, Dictionary<string, string>? settings = null) => ImapConnector.ResolveOptions(endpoint, settings ?? new(), Credentials(), TimeSpan.FromSeconds(5));
            Assert.Equal(("imap.example.com", 993, MailKit.Security.SecureSocketOptions.SslOnConnect), (Resolve("imap.example.com").Host, Resolve("imap.example.com").Port, Resolve("imap.example.com").Encryption));
            Assert.Equal((143, MailKit.Security.SecureSocketOptions.StartTls), (Resolve("imap://imap.example.com").Port, Resolve("imap://imap.example.com").Encryption));
            Assert.Equal((1993, 2993), (Resolve("imaps://imap.example.com:1993").Port, Resolve("imap.example.com:1993", new() { ["port"] = "2993" }).Port));
            Assert.Equal(143, Resolve("imap.example.com", new() { ["encryption"] = "starttls" }).Port);
            foreach (var bad in new[] { "", "https://imap.example.com", "imap.example.com:99999", "imaps://user:pw@imap.example.com", "imaps://imap.example.com/INBOX", "two words" })
            {
                Assert.Contains("IMAP server", Assert.Throws<InvalidOperationException>(() => Resolve(bad)).Message);
            }

            Assert.Equal("The 'encryption' setting 'tls9' must be one of: ssl, starttls, none.",
                Assert.Throws<InvalidOperationException>(() => Resolve("h", new() { ["encryption"] = "tls9" })).Message);
        }

        [Fact]
        public async Task A_test_signs_in_on_the_pinned_address_or_says_the_password_was_refused()
        {
            var egress = new StubEgress();
            var ok = await _connector.TestConnectionAsync(new ConnectorTestContext(Endpoint, Settings, TimeSpan.FromSeconds(10), egress) { Credentials = Credentials() });
            Assert.True(ok.Success, ok.Message);
            Assert.Equal(("mail.example.com", _server.Port), Assert.Single(egress.Pins)); // the host named, the pin dialled
            var refused = await _connector.TestConnectionAsync(new ConnectorTestContext(Endpoint, Settings, TimeSpan.FromSeconds(10), new StubEgress()) { Credentials = Credentials("wrong") });
            Assert.Equal((false, "The IMAP server refused the username or password."), (refused.Success, refused.Message));
            Assert.DoesNotContain(_server.Commands, line => line.Contains(Password, StringComparison.Ordinal)); // what this server keeps never holds it
        }

        [Fact]
        public async Task A_refused_host_is_the_contracts_denial()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => ExtractAsync("status", egress: new StubEgress { Pinned = null }));
            Assert.Empty(_server.Commands);
        }

        [Fact]
        public async Task Status_gives_the_mailboxs_uidvalidity_next_uid_and_count()
        {
            _server.Add(Message("First", "one"));
            _server.Add(Message("Second", "two"));
            var status = Assert.Single(await ExtractAsync("status"));
            Assert.Equal(("INBOX", 7L, 3L, 2), (status["mailbox"], status["uid_validity"], status["uid_next"], status["messages"]));
        }

        [Fact]
        public async Task Read_gives_the_messages_after_a_uid_with_their_headers_bodies_and_attachments_and_marks_nothing()
        {
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 invoice 1001");
            var first = _server.Add(Message("Invoice 1001", "Please find the invoice attached.", "<p>Please find the invoice attached.</p>", ("invoice-1001.pdf", pdf)));
            var second = _server.Add(Message("Reminder", "Has it been paid?"));

            var all = await ExtractAsync("read");
            Assert.Equal(new object?[] { (long)first, (long)second }, all.Select(record => record["uid"]));
            var invoice = all[0];
            Assert.Equal(("Invoice 1001", "\"Grace Supplier\" <grace@supplier.example>", "\"Orders\" <orders@example.com>", "Invoice-1001@example.com"),
                (invoice["subject"], invoice["from"], invoice["to"], invoice["message_id"]));
            Assert.Equal(("Please find the invoice attached.", "<p>Please find the invoice attached.</p>", "2026-10-10T09:30:00.0000000Z", 7L),
                (((string)invoice["text"]!).Trim(), ((string)invoice["html"]!).Trim(), invoice["date"], invoice["uid_validity"]));
            var named = JsonNode.Parse((string)invoice["attachments"]!)!.AsArray().Single()!;
            Assert.Equal(("invoice-1001.pdf", "application/pdf", (long)pdf.Length, null), (named["name"]!.GetValue<string>(), named["contentType"]!.GetValue<string>(), named["size"]!.GetValue<long>(), named["content"]));

            // After a UID: only what is above it - and nothing at all after the highest, though the server's "n:*" holds it.
            Assert.Equal(new object?[] { (long)second }, (await ExtractAsync("read", new() { ["after_uid"] = first.ToString(System.Globalization.CultureInfo.InvariantCulture) })).Select(record => record["uid"]));
            Assert.Empty(await ExtractAsync("read", new() { ["after_uid"] = second.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

            // Reading marks nothing.
            Assert.False(_server.IsSeen(first) || _server.IsSeen(second));
            Assert.DoesNotContain(_server.Commands, line => line.Contains("STORE", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Content_bounds_and_renumbering()
        {
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 invoice 1002");
            _server.Add(Message("Invoice 1002", new string('x', 50), null, ("invoice-1002.pdf", pdf)));

            var withContent = Assert.Single(await ExtractAsync("read", new() { ["attachments"] = "content", ["body_max_chars"] = "20" }));
            var attached = JsonNode.Parse((string)withContent["attachments"]!)!.AsArray().Single()!;
            Assert.Equal(pdf, Convert.FromBase64String(attached["content"]!.GetValue<string>()));
            Assert.Equal((20, true), (((string)withContent["text"]!).Length, withContent["truncated"]));

            var tooBig = Assert.Single(await ExtractAsync("read", new() { ["attachments"] = "content", ["attachment_max_bytes"] = "5" }));
            Assert.Equal("larger than 5 bytes", JsonNode.Parse((string)tooBig["attachments"]!)!.AsArray().Single()!["skipped"]!.GetValue<string>());

            var whole = Assert.Single(await ExtractAsync("read", new() { ["message_max_bytes"] = "100" }));
            Assert.Equal((true, (object?)null), (whole["too_large"], whole.GetValueOrDefault("subject")));

            // Another UIDVALIDITY: the mailbox was made again - one record says so, and no message is read.
            var renumbered = Assert.Single(await ExtractAsync("read", new() { ["uid_validity"] = "6" }));
            Assert.Equal((true, 7L, 2L), (renumbered["renumbered"], renumbered["uid_validity"], renumbered["uid_next"]));
            Assert.Equal("The 'after_uid' parameter 'soon' must be a whole number from 0 to 4294967294.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => ExtractAsync("read", new() { ["after_uid"] = "soon" }))).Message);
        }

        [Fact]
        public async Task Mark_read_marks_the_uids_given_and_no_other()
        {
            var first = _server.Add(Message("One", "1"));
            var second = _server.Add(Message("Two", "2"));
            var marked = Assert.Single(await ExtractAsync("mark_read", new() { ["uids"] = first.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            Assert.Equal(1, marked["marked"]);
            Assert.True(_server.IsSeen(first));
            Assert.False(_server.IsSeen(second));
            Assert.Equal("'x' is not a message's UID.", (await Assert.ThrowsAsync<InvalidOperationException>(() => ExtractAsync("mark_read", new() { ["uids"] = "1, x" }))).Message);
            Assert.Equal("The IMAP connector has no operation 'delete' - read, status or mark_read.", (await Assert.ThrowsAsync<InvalidOperationException>(() => ExtractAsync("delete"))).Message);
        }

        [Fact]
        public void A_certificate_naming_the_original_host_is_acceptable_after_the_pin_and_no_other_fault_is()
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=mail.example.com", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Assert.True(ImapConnector.IsAcceptable(certificate, System.Net.Security.SslPolicyErrors.None, "anything"));
            Assert.True(ImapConnector.IsAcceptable(certificate, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, "mail.example.com"));
            Assert.False(ImapConnector.IsAcceptable(certificate, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, "evil.example.com"));
            Assert.False(ImapConnector.IsAcceptable(certificate, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, "mail.example.com"));
        }
    }
}
