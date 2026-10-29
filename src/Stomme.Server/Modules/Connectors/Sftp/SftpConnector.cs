#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Stomme.Modules.Connectors.Sftp
{
    /// <summary>
    /// SFTP signed in by a password (n8n's FTP node over SFTP): the Basic scheme's <c>username</c>/<c>password</c>, as
    /// SSH's password method - n8n's way. Everything else is <see cref="SftpConnectorBase"/>'s.
    /// </summary>
    public sealed class SftpConnector : SftpConnectorBase
    {
        public override string ConnectorType => "sftp";

        public override string DisplayName => "SFTP";

        public override AuthenticationType AuthType => AuthenticationType.Basic;

        internal override (string Username, AuthenticationMethod[] Methods) SignIn(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials) =>
            SshSignIn.Password(credentials, Protocol);
    }

    /// <summary>
    /// SFTP signed in by a private key (n8n's SFTP credential's Private Key): the Certificate scheme's <c>certificate</c>
    /// holds the key - OpenSSH's or PEM, ed25519, ECDSA or RSA - and its <c>passphrase</c> the key's own, where it has one; the
    /// user is the connection's <c>username</c> setting. A key pasted into a one-line field loses its line breaks (a browser
    /// drops them, or makes them spaces), so its lines are made again from its BEGIN and END lines before it is read.
    /// </summary>
    public sealed class SftpKeyConnector : SftpConnectorBase
    {
        public override string ConnectorType => "sftpkey";

        public override string DisplayName => "SFTP (private key)";

        public override AuthenticationType AuthType => AuthenticationType.Certificate;

        public override IReadOnlyDictionary<string, string>? CredentialLabels => SshSignIn.KeyLabels;

        public override ConnectorConfigSchema ConfigSchema { get; } = SshSignIn.KeySchema;

        internal override (string Username, AuthenticationMethod[] Methods) SignIn(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials) =>
            SshSignIn.Key(settings, credentials, Protocol);
    }

    /// <summary>
    /// The SFTP connectors' part: the SSH server's (<see cref="SshConnectorBase"/>) and the file servers' operations
    /// (<see cref="RemoteFiles"/>) - a folder listed, files stored through the host's managed file sink into a site folder, one
    /// file downloaded into the pipeline run's folder or uploaded from it, a file or a folder deleted, an entry renamed.
    /// </summary>
    public abstract class SftpConnectorBase : SshConnectorBase, IConnectorExtractor
    {
        internal const string Name = "SFTP";

        internal override string Protocol => Name;

        internal override string Example => "sftp://files.kafe.example:22";

        public IReadOnlyList<ConnectorOperation> Operations { get; } = RemoteFiles.Operations("SFTP server");

        public IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var options = Resolve(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            return RemoteFiles.RunAsync(context, async token => new SftpFileSession(await ConnectAsync(options, context.Egress, Name, Sftp, token)), "SFTP server", cancellationToken);
        }

        /// <summary>The SFTP server named by an address (<see cref="SshConnectorBase.Server(string?, string, string)"/>).</summary>
        internal static (string Host, int Port) Server(string? endpoint) => Server(endpoint, Name, "sftp://files.kafe.example:22");

        /// <summary>An SFTP client on the connection, its operations held to the connection's timeout.</summary>
        internal static SftpClient Sftp(ConnectionInfo info) => new(info) { OperationTimeout = info.Timeout };

        internal override async Task<string> ProbeAsync(SshOptions options, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            using var client = await ConnectAsync(options, egress, Name, Sftp, cancellationToken);
            var home = client.WorkingDirectory;
            client.Disconnect();
            return $"Signed in to the SFTP server as {options.Username} (its folder {home}) - its host key the one this connection trusts.";
        }
    }

    /// <summary>
    /// A password's or a key's sign-in, the SSH connectors' and the SFTP connectors' alike: every credential checked, a
    /// key read, before the server is dialled.
    /// </summary>
    internal static class SshSignIn
    {
        public static readonly IReadOnlyDictionary<string, string> KeyLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["certificate"] = "Private key (OpenSSH or PEM - the whole file, its BEGIN and END lines too)",
            ["passphrase"] = "The key's passphrase (empty when it has none)",
        };

        public static readonly ConnectorConfigSchema KeySchema = new(SshConnectorBase.Fields.Concat(new[]
        {
            new ConnectorConfigField("username", "Username", ConnectorConfigFieldType.Text, Required: true, Description: "The user the key signs in as."),
        }).ToArray());

        /// <summary>The Basic scheme's <c>username</c>/<c>password</c>, as SSH's password method - n8n's way.</summary>
        public static (string Username, AuthenticationMethod[] Methods) Password(IReadOnlyDictionary<string, string> credentials, string protocol)
        {
            if (!credentials.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException($"The {protocol} connection needs its username.");
            }

            if (!credentials.TryGetValue("password", out var password) || password.Length == 0)
            {
                throw new InvalidOperationException($"The {protocol} connection needs its password - or make an {protocol} (private key) connection to sign in by a key.");
            }

            var user = username.Trim();
            return (user, new AuthenticationMethod[] { new PasswordAuthenticationMethod(user, password) });
        }

        /// <summary>The Certificate scheme's <c>certificate</c> - the private key - and its <c>passphrase</c>, the user the connection's <c>username</c> setting.</summary>
        public static (string Username, AuthenticationMethod[] Methods) Key(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, string protocol)
        {
            if (Values.Get(settings, "username") is not { } user)
            {
                throw new InvalidOperationException($"The {protocol} connection needs its username - the user the key signs in as.");
            }

            if (!credentials.TryGetValue("certificate", out var key) || string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException($"The {protocol} connection needs its private key.");
            }

            credentials.TryGetValue("passphrase", out var passphrase);
            return (user, new AuthenticationMethod[] { new PrivateKeyAuthenticationMethod(user, SshPrivateKeys.Read(key, passphrase)) });
        }
    }

    /// <summary>
    /// The SSH connectors' common part, SFTP's and SSH's commands': the server is the connection's address
    /// (<c>sftp://files.kafe.example:22</c>, <c>ssh://</c>), dialled only at the address the host's egress pinned, and it must prove
    /// itself by a host key the connection names - <c>host_key</c>, its SHA-256 fingerprint as <c>ssh-keygen -lf</c> prints it
    /// or its public key's line, as <c>ssh-keyscan</c> prints it. A connection that names none, or a server whose key is
    /// another, is refused before anything is sent to it - the password or the key's signature included - as the contract's
    /// <see cref="ConnectorHostKeyUntrustedException"/>, whose category's text says what to set; the key the server offered is
    /// in its message alone, never shown (a key is trusted for what was learned of it
    /// elsewhere, not for what a server answered).
    /// </summary>
    /// <remarks>
    /// SSH runs inside one connection - there is no second channel to a second address, as FTP's data connections are - and
    /// SSH.NET dials only the address it is given, the pinned one; a host key is not bound to a name, so the pin changes nothing
    /// the server must prove. SSH.NET's proxy settings are never set.
    /// </remarks>
    public abstract class SshConnectorBase : IConnector
    {
        internal static readonly ConnectorConfigField[] Fields =
        {
            new("host_key", "Host key", ConnectorConfigFieldType.Text, Required: false,
                Description: "The server's host key, which it must prove it holds: its fingerprint, SHA256:... as ssh-keygen -lf prints it, or its public key's line, ssh-ed25519 AAAA..., as the server's .pub file holds it - several on lines of their own, or fingerprints separated by commas. Empty: the server is refused - learn its key from the server itself (ssh-keygen -lf on its host key, or ssh-keyscan from a machine you trust) or its provider."),
        };

        public abstract string ConnectorType { get; }

        public abstract string DisplayName { get; }

        public abstract AuthenticationType AuthType { get; }

        public virtual IReadOnlyDictionary<string, string>? CredentialLabels => null;

        public virtual IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        /// <summary>Its address is not a web address: the server, <c>sftp://files.kafe.example:22</c> - no user in it.</summary>
        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string EndpointExample => Example;

        public virtual ConnectorConfigSchema ConfigSchema { get; } = new(Fields);

        /// <summary>The protocol's name in what a person reads - SFTP, SSH.</summary>
        internal abstract string Protocol { get; }

        /// <summary>The address an example names.</summary>
        internal abstract string Example { get; }

        /// <summary>The user and the ways it signs in - every credential checked, a key read, before the server is dialled.</summary>
        internal abstract (string Username, AuthenticationMethod[] Methods) SignIn(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials);

        /// <summary>A test's sign-in, and what it says of the server.</summary>
        internal abstract Task<string> ProbeAsync(SshOptions options, IConnectorEgress egress, CancellationToken cancellationToken);

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            SshOptions options;
            try
            {
                options = Resolve(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }

            try
            {
                return ConnectionTestResult.Ok(await ProbeAsync(options, context.Egress, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        /// <summary>The address, the settings and the credentials, every one checked: an actionable message for a missing or malformed one.</summary>
        internal SshOptions Resolve(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout)
        {
            var (host, port) = Server(endpoint, Protocol, Example);
            var keys = SshHostKeys.Parse(Values.Get(settings, "host_key"));
            var (username, methods) = SignIn(settings, credentials);
            return new SshOptions(host, port, keys, username, methods, TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds, 1_000, 120_000)));
        }

        /// <summary>
        /// The server the address names - <c>sftp://</c>, <c>ssh://</c> or no scheme at all, port 22 where none is given - and
        /// nothing else: a user in it is refused (the connection's username signs in), and so is a folder, a query or a
        /// fragment (a step's path names its folder).
        /// </summary>
        internal static (string Host, int Port) Server(string? endpoint, string protocol, string example)
        {
            var given = endpoint?.Trim() ?? string.Empty;
            if (given.Length == 0)
            {
                throw new InvalidOperationException($"Give the {protocol} server's address - {example}.");
            }

            var address = given.Contains("://", StringComparison.Ordinal) ? given : "ssh://" + given;
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(address, out var uri) || uri.Scheme is not ("sftp" or "ssh") || uri.DnsSafeHost.Length == 0)
            {
                throw new InvalidOperationException($"'{given}' is not an {protocol} server's address - {example}.");
            }

            if (uri.UserInfo.Length > 0)
            {
                throw new InvalidOperationException($"The address carries a user - the connection's username signs in, never its address ({example}).");
            }

            if (uri.AbsolutePath is not ("/" or "") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new InvalidOperationException($"'{given}' names more than the server - its address alone ({example}); a step's path names its folder.");
            }

            return (uri.DnsSafeHost, uri.Port > 0 ? uri.Port : 22);
        }

        /// <summary>
        /// The client on the PINNED address, signed in: the server's host key checked against the connection's before anything
        /// is sent to it; a refusal said - an untrusted key as the contract's own refusal, a sign-in refused as one, a server not reached as one.
        /// </summary>
        internal static async Task<TClient> ConnectAsync<TClient>(SshOptions options, IConnectorEgress egress, string protocol, Func<ConnectionInfo, TClient> create, CancellationToken cancellationToken)
            where TClient : BaseClient
        {
            var pinned = await egress.ResolveAndPinAsync(options.Host, options.Port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The {protocol} host '{options.Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            var client = create(new ConnectionInfo(pinned.ToString(), options.Port, options.Username, options.Methods) { Timeout = options.Timeout, RetryAttempts = 1 });
            string? offered = null;
            var trusted = false;
            client.HostKeyReceived += (_, e) =>
            {
                offered = SshHostKeys.Describe(e.HostKeyName, e.HostKey);
                e.CanTrust = trusted = options.HostKeys.Any(key => key.Matches(e.HostKey));
            };

            try
            {
                await client.ConnectAsync(cancellationToken);
                return client;
            }
            // SSH.NET ends the key exchange on an untrusted key ("Host key could not be verified", as KeyExchangeFailed): the key it offered, not trusted.
            catch (SshConnectionException) when (offered is not null && !trusted)
            {
                client.Dispose();
                throw new ConnectorHostKeyUntrustedException(options.HostKeys.Count == 0
                    ? $"No host key is set for this connection, so the server is not trusted - it offered {offered}. Check it is your server's (ssh-keygen -lf on its host key's .pub file, or its provider's console says it) and put it in the connection's Host key setting."
                    : $"The server's host key is {offered}, not one this connection trusts - it may not be your server, and nothing was sent to it. If its key was changed, put the new one in the connection's Host key setting.");
            }
            catch (SshAuthenticationException)
            {
                client.Dispose();
                throw new InvalidOperationException($"The {protocol} server refused the sign-in as {options.Username} - check the username and the password, or the key.");
            }
            catch (Exception ex) when (ex is SocketException or SshOperationTimeoutException or SshConnectionException or SshException or IOException or TimeoutException)
            {
                client.Dispose();
                throw new InvalidOperationException($"Could not reach the {protocol} server: {ex.Message}");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>The resolved SSH session inputs a client is built from.</summary>
    internal sealed record SshOptions(string Host, int Port, IReadOnlyList<SshHostKey> HostKeys, string Username, AuthenticationMethod[] Methods, TimeSpan Timeout);

    /// <summary>A host key a connection trusts: by its SHA-256 fingerprint, its (legacy) MD5 one, or the key itself.</summary>
    internal sealed record SshHostKey(string? Sha256, string? Md5)
    {
        public bool Matches(byte[] blob) =>
            (Sha256 is not null && string.Equals(Sha256, SshHostKeys.Sha256(blob), StringComparison.Ordinal))
            || (Md5 is not null && string.Equals(Md5, SshHostKeys.Md5(blob), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>host_key</c> setting read, and a server's key named as <c>ssh-keygen -lf</c> names it.</summary>
    internal static class SshHostKeys
    {
        private static readonly Regex Md5Hex = new("^(?:[0-9a-fA-F]{2}:){15}[0-9a-fA-F]{2}$", RegexOptions.CultureInvariant);

        private static readonly Regex KeyLine = new(@"(?:^|\s)(?:ssh-|ecdsa-sha2-|sk-)\S+\s+\S", RegexOptions.CultureInvariant);

        /// <summary>Each key the setting names - SHA256:..., MD5:aa:bb:..., or a public key's line - separated by lines or semicolons, fingerprints by commas too.</summary>
        public static IReadOnlyList<SshHostKey> Parse(string? setting)
        {
            if (string.IsNullOrWhiteSpace(setting))
            {
                return Array.Empty<SshHostKey>();
            }

            // A public key's line - a known_hosts entry's hosts are separated by commas - is one key; fingerprints may share a line.
            return setting.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(piece => KeyLine.IsMatch(piece) ? new[] { piece } : piece.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .SelectMany(One)
                .ToList();
        }

        public static string Sha256(byte[] blob) => Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        public static string Md5(byte[] blob) => string.Join(':', MD5.HashData(blob).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

        public static string Describe(string algorithm, byte[] blob) => $"{algorithm} SHA256:{Sha256(blob)}";

        private static IEnumerable<SshHostKey> One(string entry)
        {
            if (entry.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            {
                var value = entry[7..].Trim().TrimEnd('=');
                return Base64(value + new string('=', (4 - value.Length % 4) % 4)) is { Length: 32 }
                    ? new[] { new SshHostKey(value, null) }
                    : throw Unreadable(entry);
            }

            var md5 = entry.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase) ? entry[4..].Trim() : entry;
            if (Md5Hex.IsMatch(md5))
            {
                return new[] { new SshHostKey(null, md5.ToLowerInvariant()) };
            }

            // Public keys' lines: [host] type base64 [comment], each key's own type inside its bytes agreeing - every key in the
            // piece, as ssh-keyscan's lines pasted into a one-line field (their breaks made spaces) hold several.
            var tokens = entry.Split(' ', '\t').Where(token => token.Length > 0).ToArray();
            var keys = new List<SshHostKey>();
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (!(tokens[i].StartsWith("ssh-", StringComparison.Ordinal) || tokens[i].StartsWith("ecdsa-sha2-", StringComparison.Ordinal) || tokens[i].StartsWith("sk-", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (Base64(tokens[i + 1]) is not { Length: > 4 } blob || TypeOf(blob) != tokens[i])
                {
                    throw Unreadable(entry);
                }

                keys.Add(new SshHostKey(Sha256(blob), null));
                i++;
            }

            return keys.Count > 0 ? keys : throw Unreadable(entry);
        }

        private static string? TypeOf(byte[] blob)
        {
            var length = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
            return length > 0 && length <= blob.Length - 4 ? Encoding.ASCII.GetString(blob, 4, length) : null;
        }

        private static byte[]? Base64(string value)
        {
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static InvalidOperationException Unreadable(string entry) =>
            new($"'{(entry.Length > 60 ? entry[..60] + "..." : entry)}' is not a host key - its fingerprint, SHA256:... as ssh-keygen -lf prints it, or its public key's line, ssh-ed25519 AAAA..., as the server's .pub file holds it (host_key).");
    }

    /// <summary>A private key read from the text a connection holds - its lines made again where a one-line field dropped them.</summary>
    internal static class SshPrivateKeys
    {
        private static readonly Regex Armoured = new(@"^-----BEGIN (?<label>[A-Z0-9 ]+)-----(?<body>.*?)-----END \k<label>-----$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex Encrypted = new(@"^\s*Proc-Type:\s*(?<type>4,ENCRYPTED)\s*DEK-Info:\s*(?<cipher>[A-Z0-9-]+),", RegexOptions.CultureInvariant);

        public static PrivateKeyFile Read(string text, string? passphrase)
        {
            var lines = Lines(text);
            if (string.IsNullOrEmpty(passphrase) && lines.Contains("ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            {
                // PKCS#8 says so in its armour, and SSH.NET would try it with an empty passphrase (an OpenSSH or a legacy PEM key it
                // finds encrypted itself, and says so).
                throw new InvalidOperationException("The private key is encrypted - give its passphrase.");
            }

            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(lines));
            try
            {
                return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                throw new InvalidOperationException("The private key is encrypted - give its passphrase.");
            }
            // Reading the key is parsing alone: whatever fails it - SSH.NET's, .NET's or BouncyCastle's own (a wrong passphrase's
            // padding) - the key could not be read.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"The private key could not be read ({ex.Message}) - an OpenSSH or PEM key, the whole file, and its passphrase where it has one.");
            }
        }

        /// <summary>The key's text with its lines: as given when it has them; otherwise its BEGIN and END lines, any PEM headers, and its body wrapped at 64.</summary>
        internal static string Lines(string text)
        {
            var given = text.Trim();
            if (given.Contains('\n'))
            {
                return given + "\n";
            }

            var armoured = Armoured.Match(given);
            if (!armoured.Success)
            {
                return given;
            }

            var label = armoured.Groups["label"].Value;
            var body = armoured.Groups["body"].Value;
            var headers = string.Empty;
            var encrypted = Encrypted.Match(body);
            if (encrypted.Success)
            {
                // The IV is hex of the cipher's block: 8 bytes for DES, 16 for AES - read by its length, since the body follows it unbroken.
                var cipher = encrypted.Groups["cipher"].Value;
                var ivLength = cipher.StartsWith("DES", StringComparison.Ordinal) ? 16 : 32;
                var rest = body[encrypted.Length..].TrimStart();
                if (rest.Length < ivLength)
                {
                    return given;
                }

                headers = $"Proc-Type: {encrypted.Groups["type"].Value}\nDEK-Info: {cipher},{rest[..ivLength]}\n\n";
                body = rest[ivLength..];
            }

            var base64 = Regex.Replace(body, @"\s+", string.Empty);
            var wrapped = string.Join('\n', Enumerable.Range(0, (base64.Length + 63) / 64).Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - (i * 64)))));
            return $"-----BEGIN {label}-----\n{headers}{wrapped}\n-----END {label}-----\n";
        }
    }

    /// <summary>
    /// A signed-in SSH.NET client as the file operations' session: a folder's entries as the server lists them (a link as a link,
    /// never followed), a missing path null, the folders of an upload's path made on the way, a refusal said.
    /// </summary>
    internal sealed class SftpFileSession(SftpClient client) : IRemoteFileSession
    {
        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        {
            var entries = new List<RemoteEntry>();
            await Said(async () =>
            {
                await foreach (var file in client.ListDirectoryAsync(folder, cancellationToken))
                {
                    if (file.Name is not ("." or ".."))
                    {
                        entries.Add(Entry(file.Name, file.FullName, file.Attributes));
                    }
                }

                return true;
            }, folder);
            return entries;
        }

        public async Task<RemoteEntry?> StatAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                var attributes = await client.GetAttributesAsync(path, cancellationToken);
                return Entry(path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..], path, attributes);
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }

        public Task DownloadAsync(string path, Stream output, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.DownloadFileAsync(path, output, cancellationToken);
            return true;
        }, path);

        public async Task UploadAsync(string path, Stream content, CancellationToken cancellationToken)
        {
            if (RemoteFiles.Parent(path) is { } parent)
            {
                await CreateFolderAsync(parent, cancellationToken);
            }

            await Said(async () =>
            {
                await client.UploadFileAsync(content, path, cancellationToken);
                return true;
            }, path);
        }

        public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
        {
            var at = path.StartsWith('/') ? "/" : string.Empty;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                at = at.Length == 0 ? segment : RemoteFiles.Join(at, segment);
                var folder = at;
                if (!await Said(() => client.ExistsAsync(folder, cancellationToken), folder))
                {
                    await Said(async () =>
                    {
                        await client.CreateDirectoryAsync(folder, cancellationToken);
                        return true;
                    }, folder);
                }
            }
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.DeleteFileAsync(path, cancellationToken);
            return true;
        }, path);

        public Task DeleteFolderAsync(string path, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.DeleteDirectoryAsync(path, cancellationToken);
            return true;
        }, path);

        public Task RenameAsync(string from, string to, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.RenameFileAsync(from, to, cancellationToken);
            return true;
        }, from);

        public ValueTask DisposeAsync()
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception ex) when (ex is SshException or IOException or SocketException or ObjectDisposedException)
            {
                // The session is over either way.
            }

            client.Dispose();
            return ValueTask.CompletedTask;
        }

        private static RemoteEntry Entry(string name, string path, SftpFileAttributes attributes) => new(
            name,
            path,
            attributes.IsSymbolicLink ? RemoteEntry.Link : attributes.IsDirectory ? RemoteEntry.Folder : RemoteEntry.File,
            attributes.IsDirectory ? null : attributes.Size,
            attributes.LastWriteTimeUtc == default ? null : new DateTimeOffset(DateTime.SpecifyKind(attributes.LastWriteTimeUtc, DateTimeKind.Utc)));

        // A refusal the server sent, said rather than as SSH.NET's exception.
        private static async Task<T> Said<T>(Func<Task<T>> call, string path)
        {
            try
            {
                return await call();
            }
            catch (SftpPathNotFoundException)
            {
                throw new InvalidOperationException($"'{path}' is not on the SFTP server.");
            }
            catch (SftpPermissionDeniedException)
            {
                throw new InvalidOperationException($"The SFTP server refused it ({path}): permission denied.");
            }
            catch (SshException ex)
            {
                throw new InvalidOperationException($"The SFTP server refused it ({path}): {ex.Message}");
            }
        }
    }
}
