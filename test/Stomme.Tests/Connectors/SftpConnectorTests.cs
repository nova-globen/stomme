#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Sftp;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - the SFTP connectors without a server: SFTP by a password and SFTP by a private key, Data &amp; Files, the file
    /// servers' operations; the host key a connection trusts read from its fingerprint (SHA-256 as ssh-keygen -lf prints it,
    /// padded or not, or legacy MD5) or from its public key's line (a .pub file's, a known_hosts entry's - the key's own type
    /// agreeing), several at once, anything else refused; a key's lines made again where a one-line field dropped them or made
    /// them spaces, PEM's encryption headers included; a key read - its passphrase missing or wrong said; every setting and
    /// credential checked before the egress is asked; a host the egress refuses denied; a closed port said.
    /// </summary>
    public sealed class SftpConnectorTests
    {
        private static readonly Dictionary<string, string> Password = new() { ["username"] = "kafe", ["password"] = "pw-1" };

        private static string Armour(string label, bool end = false) => "-----" + (end ? "END " : "BEGIN ") + label + "-----";

        // An ed25519 public key's bytes: its type, then its 32 bytes.
        private static byte[] Blob(string type = "ssh-ed25519", byte fill = 7)
        {
            var name = Encoding.ASCII.GetBytes(type);
            var blob = new List<byte>();
            blob.AddRange(BitConverter.GetBytes(name.Length).Reverse());
            blob.AddRange(name);
            blob.AddRange(BitConverter.GetBytes(32).Reverse());
            blob.AddRange(Enumerable.Repeat(fill, 32));
            return blob.ToArray();
        }

        private static string Sha256(byte[] blob) => Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        private const string Address = "sftp://sftp.kafe.test";

        private static Dictionary<string, string> Settings(string? hostKey = "SHA256:" + "abc", string? username = null)
        {
            var settings = new Dictionary<string, string>();
            if (hostKey is not null)
            {
                settings["host_key"] = hostKey;
            }

            if (username is not null)
            {
                settings["username"] = username;
            }

            return settings;
        }

        [Fact]
        public void Both_connectors_declare_their_scheme_their_category_and_the_file_servers_operations()
        {
            var password = new SftpConnector();
            var key = new SftpKeyConnector();
            Assert.Equal(("sftp", "SFTP", AuthenticationType.Basic), (password.ConnectorType, password.DisplayName, password.AuthType));
            Assert.Equal(("sftpkey", "SFTP (private key)", AuthenticationType.Certificate), (key.ConnectorType, key.DisplayName, key.AuthType));
            Assert.All(new SftpConnectorBase[] { password, key }, connector =>
            {
                Assert.Equal(new[] { ConnectorCategory.DataAndFiles }, connector.Categories);
                Assert.Equal(new[] { "list", "download", "download_file", "upload", "delete", "rename" }, connector.Operations.Select(o => o.Name).ToArray());
                Assert.Contains("SFTP server", connector.Operations[0].Description, StringComparison.Ordinal);
                Assert.Equal(("sftp://files.kafe.example:22", ConnectorEndpointKind.ConnectionString), (((IConnector)connector).EndpointExample, ((IConnector)connector).EndpointKind));
                Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "host_key").Required); // a test without it names the key to set
            });
            Assert.Equal(new[] { "host_key" }, password.ConfigSchema.Fields.Select(f => f.Name).ToArray());
            Assert.Null(((IConnector)password).CredentialLabels);
            Assert.Equal(new[] { "host_key", "username" }, key.ConfigSchema.Fields.Select(f => f.Name).ToArray());
            Assert.True(key.ConfigSchema.Fields.Single(f => f.Name == "username").Required);
            Assert.Equal(new[] { "certificate", "passphrase" }, key.CredentialLabels!.Keys.OrderBy(k => k).ToArray());
            Assert.StartsWith("Private key (OpenSSH or PEM", key.CredentialLabels["certificate"], StringComparison.Ordinal);
        }

        [Fact]
        public void A_host_key_is_read_from_its_fingerprint_or_its_public_keys_line()
        {
            var blob = Blob();
            var other = Blob(fill: 8);
            var line = "ssh-ed25519 " + Convert.ToBase64String(blob);
            foreach (var setting in new[] { "SHA256:" + Sha256(blob), "sha256:" + Sha256(blob) + "=", " SHA256: " + Sha256(blob) + " ", line + " kafe@kassa", "sftp.kafe.test,10.0.0.5 " + line, "[sftp.kafe.test]:2222 " + line })
            {
                var key = Assert.Single(SshHostKeys.Parse(setting));
                Assert.True(key.Matches(blob), setting);
                Assert.False(key.Matches(other), setting);
            }

            var md5 = string.Join(':', MD5.HashData(blob).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(Assert.Single(SshHostKeys.Parse("MD5:" + md5.ToUpperInvariant())).Matches(blob));
            Assert.True(Assert.Single(SshHostKeys.Parse(md5)).Matches(blob));
            Assert.False(Assert.Single(SshHostKeys.Parse(md5)).Matches(other));
            Assert.Equal(md5, SshHostKeys.Md5(blob));

            var scanned = SshHostKeys.Parse("sftp.kafe.test " + line + " sftp.kafe.test ssh-ed25519 " + Convert.ToBase64String(other)); // ssh-keyscan's lines, their breaks made spaces
            Assert.Equal(2, scanned.Count);
            Assert.True(scanned[0].Matches(blob));
            Assert.True(scanned[1].Matches(other));
            var several = SshHostKeys.Parse("SHA256:" + Sha256(other) + ",\n" + line + "; MD5:" + md5);
            Assert.Equal(3, several.Count);
            Assert.True(several[0].Matches(other));
            Assert.Empty(SshHostKeys.Parse(null));
            Assert.Empty(SshHostKeys.Parse("  "));
            Assert.Equal("ssh-ed25519 SHA256:" + Sha256(blob), SshHostKeys.Describe("ssh-ed25519", blob));
            Assert.Contains("is not a host key", Assert.Throws<InvalidOperationException>(() => SshHostKeys.Parse("ssh-rsa " + Convert.ToBase64String(blob))).Message, StringComparison.Ordinal); // its bytes say another type
            Assert.Contains("is not a host key", Assert.Throws<InvalidOperationException>(() => SshHostKeys.Parse(line + " ssh-rsa " + Convert.ToBase64String(blob))).Message, StringComparison.Ordinal); // one bad key of several
        }

        [Theory]
        [InlineData("SHA256:abc")] // not 32 bytes
        [InlineData("SHA256:!!!!")]
        [InlineData("ab:cd:ef")]
        [InlineData("a fingerprint")]
        [InlineData("ssh-ed25519")] // its key missing
        [InlineData("ssh-ed25519 not-base64!")]

        public void Anything_else_is_refused_as_a_host_key(string setting)
        {
            var refusal = Assert.Throws<InvalidOperationException>(() => SshHostKeys.Parse(setting));
            Assert.Contains("is not a host key", refusal.Message, StringComparison.Ordinal);
            Assert.EndsWith("(host_key).", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_keys_lines_are_made_again_where_a_one_line_field_dropped_them()
        {
            var body = new string('A', 100) + new string('b', 50);
            var lines = Armour("OPENSSH PRIVATE KEY") + "\n" + body[..64] + "\n" + body[64..128] + "\n" + body[128..] + "\n" + Armour("OPENSSH PRIVATE KEY", end: true) + "\n";
            Assert.Equal(lines, SshPrivateKeys.Lines(Armour("OPENSSH PRIVATE KEY") + body + Armour("OPENSSH PRIVATE KEY", end: true))); // Chrome's paste
            Assert.Equal(lines, SshPrivateKeys.Lines(" " + Armour("OPENSSH PRIVATE KEY") + " " + body[..70] + " " + body[70..] + " " + Armour("OPENSSH PRIVATE KEY", end: true) + " ")); // Firefox's
            Assert.Equal("first\r\nsecond\n", SshPrivateKeys.Lines(" first\r\nsecond ")); // with its lines: as given
            Assert.Equal("PuTTY-User-Key-File-3: ssh-ed25519", SshPrivateKeys.Lines("PuTTY-User-Key-File-3: ssh-ed25519")); // no armour: left to the reader

            var aes = Armour("RSA PRIVATE KEY") + "Proc-Type: 4,ENCRYPTEDDEK-Info: AES-128-CBC,0123456789ABCDEF0123456789ABCDEFMIIEowIB" + Armour("RSA PRIVATE KEY", end: true);
            Assert.Equal(Armour("RSA PRIVATE KEY") + "\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,0123456789ABCDEF0123456789ABCDEF\n\nMIIEowIB\n" + Armour("RSA PRIVATE KEY", end: true) + "\n", SshPrivateKeys.Lines(aes));
            var des = Armour("RSA PRIVATE KEY") + " Proc-Type: 4,ENCRYPTED DEK-Info: DES-EDE3-CBC,0123456789ABCDEF  MIIEowIB " + Armour("RSA PRIVATE KEY", end: true);
            Assert.Equal(Armour("RSA PRIVATE KEY") + "\nProc-Type: 4,ENCRYPTED\nDEK-Info: DES-EDE3-CBC,0123456789ABCDEF\n\nMIIEowIB\n" + Armour("RSA PRIVATE KEY", end: true) + "\n", SshPrivateKeys.Lines(des));
            var cut = Armour("RSA PRIVATE KEY") + "Proc-Type: 4,ENCRYPTEDDEK-Info: AES-128-CBC,0123" + Armour("RSA PRIVATE KEY", end: true);
            Assert.Equal(cut, SshPrivateKeys.Lines(cut)); // too short for its IV: left to the reader
        }

        [Fact]
        public void A_key_is_read_its_passphrase_missing_or_wrong_said()
        {
            using var rsa = RSA.Create(2048);
            var pem = rsa.ExportRSAPrivateKeyPem();
            Assert.NotNull(SshPrivateKeys.Read(pem, null));
            Assert.NotNull(SshPrivateKeys.Read(pem.Replace("\r", string.Empty).Replace("\n", string.Empty), "")); // pasted on one line; an empty passphrase none
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.NotNull(SshPrivateKeys.Read(ec.ExportECPrivateKeyPem().Replace("\n", " "), null));

            var encrypted = rsa.ExportEncryptedPkcs8PrivateKeyPem("kassa-losen", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));
            Assert.NotNull(SshPrivateKeys.Read(encrypted, "kassa-losen"));
            Assert.Equal("The private key is encrypted - give its passphrase.", Assert.Throws<InvalidOperationException>(() => SshPrivateKeys.Read(encrypted, null)).Message);
            var legacy = Armour("RSA PRIVATE KEY") + "\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,00112233445566778899AABBCCDDEEFF\n\nMIIE\n" + Armour("RSA PRIVATE KEY", end: true);
            Assert.Equal("The private key is encrypted - give its passphrase.", Assert.Throws<InvalidOperationException>(() => SshPrivateKeys.Read(legacy, "")).Message);
            Assert.StartsWith("The private key could not be read (", Assert.Throws<InvalidOperationException>(() => SshPrivateKeys.Read(encrypted, "not-it")).Message, StringComparison.Ordinal);
            Assert.EndsWith("an OpenSSH or PEM key, the whole file, and its passphrase where it has one.", Assert.Throws<InvalidOperationException>(() => SshPrivateKeys.Read("not a key", null)).Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Every_setting_and_credential_is_checked_before_the_egress_is_asked()
        {
            async Task<string> Failed(SftpConnectorBase connector, Dictionary<string, string> settings, Dictionary<string, string> credentials, string endpoint = Address)
            {
                var egress = new StubEgress();
                var result = await connector.TestConnectionAsync(Contexts.Test(egress, endpoint, settings, credentials));
                Assert.False(result.Success);
                Assert.Empty(egress.Pins);
                return result.Message;
            }

            Assert.Equal("Give the SFTP server's address - sftp://files.kafe.example:22.", await Failed(new SftpConnector(), Settings(), Password, " "));
            Assert.Equal("'https://sftp.kafe.test' is not an SFTP server's address - sftp://files.kafe.example:22.", await Failed(new SftpConnector(), Settings(), Password, "https://sftp.kafe.test"));
            Assert.Equal("'sftp://sftp.kafe.test:99999' is not an SFTP server's address - sftp://files.kafe.example:22.", await Failed(new SftpConnector(), Settings(), Password, "sftp://sftp.kafe.test:99999"));
            Assert.Equal("The address carries a user - the connection's username signs in, never its address (sftp://files.kafe.example:22).", await Failed(new SftpConnector(), Settings(), Password, "sftp://kafe:pw@sftp.kafe.test"));
            Assert.Equal("'sftp://sftp.kafe.test/files' names more than the server - its address alone (sftp://files.kafe.example:22); a step's path names its folder.", await Failed(new SftpConnector(), Settings(), Password, "sftp://sftp.kafe.test/files"));
            Assert.Contains("names more than the server", await Failed(new SftpConnector(), Settings(), Password, "sftp://sftp.kafe.test?x=1"), StringComparison.Ordinal);
            Assert.Contains("names more than the server", await Failed(new SftpConnector(), Settings(), Password, "sftp://sftp.kafe.test#x"), StringComparison.Ordinal);
            Assert.Contains("is not a host key", await Failed(new SftpConnector(), Settings("SHA256:abc"), Password), StringComparison.Ordinal);
            var good = "SHA256:" + Sha256(Blob());
            Assert.Equal("The SFTP connection needs its username.", await Failed(new SftpConnector(), Settings(good), new() { ["password"] = "pw" }));
            Assert.Equal("The SFTP connection needs its password - or make an SFTP (private key) connection to sign in by a key.", await Failed(new SftpConnector(), Settings(good), new() { ["username"] = "kafe", ["password"] = "" }));
            Assert.Equal("The SFTP connection needs its username - the user the key signs in as.", await Failed(new SftpKeyConnector(), Settings(good), new() { ["certificate"] = "k" }));
            Assert.Equal("The SFTP connection needs its private key.", await Failed(new SftpKeyConnector(), Settings(good, "kafe"), new() { ["passphrase"] = "p" }));
            Assert.StartsWith("The private key could not be read", await Failed(new SftpKeyConnector(), Settings(good, "kafe"), new() { ["certificate"] = "not a key" }), StringComparison.Ordinal);

            var context = Contexts.Extract(new StubEgress(), "", "list", new Dictionary<string, string>(), Settings(), Password);
            Assert.Equal("Give the SFTP server's address - sftp://files.kafe.example:22.", Assert.Throws<InvalidOperationException>(() => new SftpConnector().ExtractAsync(context)).Message);
        }

        [Fact]
        public void The_options_carry_the_server_its_port_the_user_and_the_timeout_clamped()
        {
            var good = "SHA256:" + Sha256(Blob());
            var options = new SftpConnector().Resolve(" sftp://SFTP.kafe.test:2222/ ", Settings(good), new Dictionary<string, string> { ["username"] = " kafe ", ["password"] = "pw" }, TimeSpan.FromSeconds(15));
            Assert.Equal(("sftp.kafe.test", 2222, "kafe", TimeSpan.FromSeconds(15)), (options.Host, options.Port, options.Username, options.Timeout));
            Assert.IsType<Renci.SshNet.PasswordAuthenticationMethod>(Assert.Single(options.Methods)); // the password, n8n's way
            Assert.True(Assert.Single(options.HostKeys).Matches(Blob()));
            Assert.Equal(("sftp.kafe.test", 22), SftpConnectorBase.Server("sftp://sftp.kafe.test"));
            Assert.Equal(("sftp.kafe.test", 22), SftpConnectorBase.Server("sftp.kafe.test"));
            Assert.Equal(("10.0.0.5", 2222), SftpConnectorBase.Server("10.0.0.5:2222"));
            Assert.Equal(("sftp.kafe.test", 23), SftpConnectorBase.Server("ssh://sftp.kafe.test:23"));
            Assert.Equal(("::1", 2222), SftpConnectorBase.Server("sftp://[::1]:2222"));
            Assert.Equal(TimeSpan.FromSeconds(1), new SftpConnector().Resolve(Address, Settings(good), Password, TimeSpan.FromMilliseconds(10)).Timeout);
            Assert.Equal(TimeSpan.FromSeconds(120), new SftpConnector().Resolve(Address, Settings(good), Password, TimeSpan.FromHours(1)).Timeout);
            using var rsa = RSA.Create(2048);
            var byKey = new SftpKeyConnector().Resolve(Address, Settings(good, " kafe "), new Dictionary<string, string> { ["certificate"] = rsa.ExportRSAPrivateKeyPem() }, TimeSpan.FromSeconds(5));
            Assert.Equal("kafe", byKey.Username);
            Assert.Single(byKey.Methods);
        }

        [Fact]
        public async Task A_host_the_egress_refuses_is_denied_and_a_closed_port_is_said()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new SftpConnector().TestConnectionAsync(Contexts.Test(refused, Address + ":2222", Settings("SHA256:" + Sha256(Blob())), Password)));
            Assert.Contains("'sftp.kafe.test'", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("sftp.kafe.test", 2222), refused.Pins.Single());

            var closed = await new SftpConnector().TestConnectionAsync(Contexts.Test(new StubEgress { Pinned = IPAddress.Loopback }, Address + ":1", Settings("SHA256:" + Sha256(Blob())), Password, timeoutSeconds: 3));
            Assert.False(closed.Success);
            Assert.StartsWith("Could not reach the SFTP server: ", closed.Message, StringComparison.Ordinal);

            var context = Contexts.Extract(refused, Address, "list", new Dictionary<string, string>(), Settings("SHA256:" + Sha256(Blob())), Password);
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => Contexts.DrainAsync(new SftpConnector(), context));
            var parameters = Contexts.Extract(new StubEgress(), Address, "delete", new Dictionary<string, string> { ["path"] = "/" }, Settings("SHA256:" + Sha256(Blob())), Password);
            Assert.Contains("never deleted", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SftpConnector(), parameters))).Message, StringComparison.Ordinal);
        }
    }
}
