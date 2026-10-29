#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Stomme.Modules.Connectors.Ftp
{
    /// <summary>
    /// The FTP / FTPS connector: the server is the <c>host</c>/<c>port</c>
    /// settings, the Basic scheme's <c>username</c>/<c>password</c> authenticate the session, TLS follows the settings
    /// (<c>encryption_mode</c> none by default, TLS 1.2+ when on). A test connects, authenticates and disconnects. Its
    /// operations are the file servers' (<see cref="RemoteFiles"/>, covering n8n's FTP node, shared with SFTP): a
    /// folder listed, files stored through the host's managed file sink into a site folder, one file downloaded into the
    /// pipeline run's folder or uploaded from it, a file or a folder deleted, an entry renamed - never a host path.
    /// </summary>
    /// <remarks>
    /// <para><b>The control connection dials only the address the host pinned</b> (<see cref="IConnectorEgress.ResolveAndPinAsync"/>);
    /// the client is built on that address. <b>The data connection is EPSV only</b>: with PASV the server names the
    /// address the client must dial next, which is a second destination the policy never saw - so the port refuses
    /// PASV and every data channel goes to the same pinned address the control channel uses (a server without EPSV
    /// fails the operation, and says so).</para>
    /// <para>TLS validation with a pinned address: the certificate is checked against the ORIGINAL host name (the
    /// pin changes where the socket goes, not whom the certificate must name). <c>danger_accept_any_certificate</c>
    /// stays a per-connection setting, default off, for the same reason as SQL Server's: the driver's TLS policy is
    /// not the egress's.</para>
    /// </remarks>
    public sealed class FtpConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "ftp";

        public string DisplayName => "FTP / FTPS";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        /// <summary>Its address is not a web address: the server, <c>ftp://ftp.kafe.example:21</c> - no user in it.</summary>
        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string EndpointExample => "ftp://ftp.kafe.example:21";

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("host", "Host", ConnectorConfigFieldType.Text, Required: false, Description: "FTP server host name or IP address - empty: the address's. A host here names the server, its address then unread."),
            new ConnectorConfigField("port", "Port", ConnectorConfigFieldType.Integer, Required: false, Description: "FTP control port, with the host setting - empty: 21. The address's port otherwise."),
            new ConnectorConfigField("encryption_mode", "Encryption mode", ConnectorConfigFieldType.Select, Required: false,
                Description: "FTPS mode: none (plain FTP, the legacy default), explicit (upgrade via AUTH TLS), implicit (TLS from the first byte), or auto (negotiate the best the server offers).")
            {
                Default = "none",
                Options = new[] { new ConnectorConfigFieldOption("none"), new ConnectorConfigFieldOption("explicit"), new ConnectorConfigFieldOption("implicit"), new ConnectorConfigFieldOption("auto") },
            },
            new ConnectorConfigField("danger_accept_any_certificate", "Accept any TLS certificate", ConnectorConfigFieldType.Boolean, Required: false,
                Description: "Skips server-certificate validation for this connection only. Dangerous - use solely for trusted hosts with self-signed certificates.")
            {
                Default = "false",
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = RemoteFiles.Operations("FTP server");


        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            FtpSessionOptions options;
            try
            {
                options = ResolveOptions(context.Settings, context.Credentials, context.Timeout, context.Endpoint);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }

            var pinned = await PinAsync(options, context.Egress, cancellationToken);
            try
            {
                using var client = CreateClient(options, pinned);
                await client.Connect(cancellationToken);
                await client.Disconnect(cancellationToken);
                return ConnectionTestResult.Ok("Connected to the FTP server.");
            }
            catch (FtpAuthenticationException ex)
            {
                return ConnectionTestResult.Fail($"FTP authentication failed: {ex.Message}");
            }
            catch (Exception ex) when (ex is FtpException or SocketException or IOException or TimeoutException or AuthenticationException or InvalidOperationException)
            {
                return ConnectionTestResult.Fail($"Could not reach the FTP server: {ex.Message}");
            }
        }

        public IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var options = ResolveOptions(context.Settings, context.Credentials, context.Timeout, context.Endpoint);
            return RemoteFiles.RunAsync(context, async token =>
            {
                var pinned = await PinAsync(options, context.Egress, token);
                var client = CreateClient(options, pinned);
                try
                {
                    await client.Connect(token);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                return new FtpFileSession(client);
            }, "FTP server", cancellationToken);
        }

        /// <summary>Anchored <c>*</c>/<c>?</c> wildcard match over a remote file name, case-insensitive.</summary>
        internal static bool MatchesWildcard(string name, string pattern) => RemoteFiles.MatchesWildcard(name, pattern);

        /// <summary>
        /// The session inputs: the server - the <c>host</c>/<c>port</c> settings where a host is set (a connection made before the
        /// address named it), otherwise the address, <c>ftp://ftp.kafe.example:21</c> - its encryption and certificate settings, the
        /// scheme's named credentials, the timeout; an actionable message for a bad setting or a missing username.
        /// </summary>
        internal static FtpSessionOptions ResolveOptions(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, string? endpoint = null)
        {
            string host;
            var port = 21;
            if (settings.TryGetValue("host", out var named) && !string.IsNullOrWhiteSpace(named))
            {
                host = named.Trim();
                if (settings.TryGetValue("port", out var portValue) && !string.IsNullOrWhiteSpace(portValue) &&
                    (!int.TryParse(portValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
                {
                    throw new InvalidOperationException($"The 'port' setting '{portValue}' is not a valid TCP port.");
                }
            }
            else
            {
                (host, port) = Server(endpoint);
            }

            var encryptionMode = FtpEncryptionMode.None;
            if (settings.TryGetValue("encryption_mode", out var mode) && !string.IsNullOrWhiteSpace(mode))
            {
                encryptionMode = mode.Trim().ToLowerInvariant() switch
                {
                    "none" => FtpEncryptionMode.None,
                    "explicit" => FtpEncryptionMode.Explicit,
                    "implicit" => FtpEncryptionMode.Implicit,
                    "auto" => FtpEncryptionMode.Auto,
                    _ => throw new InvalidOperationException($"The 'encryption_mode' setting '{mode}' must be one of: none, explicit, implicit, auto."),
                };
            }

            var acceptAnyCertificate = settings.TryGetValue("danger_accept_any_certificate", out var flag) && bool.TryParse(flag, out var parsed) && parsed;
            if (!credentials.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("The FTP connector requires a username/password credential; no username was supplied.");
            }

            credentials.TryGetValue("password", out var password);
            return new FtpSessionOptions(host, port, encryptionMode, acceptAnyCertificate, username, password ?? string.Empty, timeout);
        }

        /// <summary>
        /// The server the address names - <c>ftp://ftp.kafe.example:21</c>, <c>ftps://</c> or no scheme at all, port 21 where none is
        /// given - and nothing else: a user in it is refused (the scheme's username signs in), and so is a folder, a query or
        /// a fragment (a step's path names its folder).
        /// </summary>
        internal static (string Host, int Port) Server(string? endpoint)
        {
            var given = endpoint?.Trim() ?? string.Empty;
            if (given.Length == 0)
            {
                throw new InvalidOperationException("Give the FTP server's address - ftp://ftp.kafe.example:21 - or its 'host' setting.");
            }

            var address = given.Contains("://", StringComparison.Ordinal) ? given : "ftp://" + given;
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(address, out var uri) || uri.Scheme is not ("ftp" or "ftps") || uri.DnsSafeHost.Length == 0)
            {
                throw new InvalidOperationException($"'{given}' is not an FTP server's address - ftp://ftp.kafe.example:21.");
            }

            if (uri.UserInfo.Length > 0)
            {
                throw new InvalidOperationException("The address carries a user - the connection's username signs in, never its address (ftp://ftp.kafe.example:21).");
            }

            if (uri.AbsolutePath is not ("/" or "") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new InvalidOperationException($"'{given}' names more than the server - its address alone (ftp://ftp.kafe.example:21); a step's path names its folder.");
            }

            return (uri.DnsSafeHost, uri.Port > 0 ? uri.Port : 21);
        }

        private static async Task<IPAddress> PinAsync(FtpSessionOptions options, IConnectorEgress egress, CancellationToken cancellationToken) =>
            await egress.ResolveAndPinAsync(options.Host, options.Port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The FTP host '{options.Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");

        /// <summary>The FluentFTP client on the PINNED address: EPSV-only data connections, TLS 1.2+, the certificate checked against the original host, the timeout on every channel.</summary>
        internal static AsyncFtpClient CreateClient(FtpSessionOptions options, IPAddress pinned)
        {
            var client = new AsyncFtpClient(pinned.ToString(), options.Username, options.Password, options.Port);
            client.Config.DataConnectionType = FtpDataConnectionType.EPSV; // the data channel goes where the control channel went - never to an address the server names
            client.Config.EncryptionMode = options.EncryptionMode;
            client.Config.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            client.Config.ValidateAnyCertificate = options.DangerAcceptAnyCertificate;
            if (!options.DangerAcceptAnyCertificate)
            {
                client.ValidateCertificate += (_, e) => e.Accept = IsAcceptable(e, options.Host);
            }

            var timeoutMs = (int)Math.Clamp(options.Timeout.TotalMilliseconds, 1_000, int.MaxValue);
            client.Config.ConnectTimeout = timeoutMs;
            client.Config.ReadTimeout = timeoutMs;
            client.Config.DataConnectionConnectTimeout = timeoutMs;
            client.Config.DataConnectionReadTimeout = timeoutMs;
            return client;
        }

        /// <summary>A certificate is acceptable when it validates, or when its only fault is the name - which is checked against the ORIGINAL host, since the socket dialled the pinned address.</summary>
        internal static bool IsAcceptable(FtpSslValidationEventArgs e, string originalHost)
        {
            if (e.PolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            if (e.PolicyErrors != SslPolicyErrors.RemoteCertificateNameMismatch || e.Certificate is null)
            {
                return false;
            }

            using var certificate = new X509Certificate2(e.Certificate);
            return certificate.MatchesHostname(originalHost);
        }

    }

    /// <summary>The resolved FTP session inputs (settings + credentials + timeout) a client is built from.</summary>
    internal sealed record FtpSessionOptions(string Host, int Port, FtpEncryptionMode EncryptionMode, bool DangerAcceptAnyCertificate, string Username, string Password, TimeSpan Timeout);

    /// <summary>
    /// A signed-in FluentFTP client as the file operations' session: a listing parsed by FluentFTP, a missing path null, a
    /// server's refusal said with its reply - every data channel EPSV to the pinned address, as the client was built.
    /// </summary>
    internal sealed class FtpFileSession(AsyncFtpClient client) : IRemoteFileSession
    {
        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        {
            var listing = await Said(() => client.GetListing(folder, cancellationToken), folder) ?? Array.Empty<FtpListItem>();
            return listing.Where(item => item.Name is not ("." or "..")).Select(item => Entry(item, item.Name, item.FullName)).ToList();
        }

        public async Task<RemoteEntry?> StatAsync(string path, CancellationToken cancellationToken)
        {
            // FluentFTP's own lookup - MLST, or its folder's listing where the server has no MLST - for a path from the root; for one
            // it does not find (a relative path, a missing one), the entry in its folder's listing. FluentFTP's DirectoryExists
            // changes into the folder and back out by "..", which its own path sanitizer refuses.
            var trimmed = path.TrimEnd('/');
            var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            if (await Said(() => client.GetObjectInfo(path, dateModified: true, cancellationToken), path) is { } item)
            {
                return Entry(item, name, path);
            }

            var folder = RemoteFiles.Parent(trimmed) ?? (trimmed.StartsWith('/') ? "/" : string.Empty);
            var listing = await Said(() => client.GetListing(folder, cancellationToken), path) ?? Array.Empty<FtpListItem>();
            return listing.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal)) is { } found ? Entry(found, name, path) : null;
        }
        public async Task DownloadAsync(string path, Stream output, CancellationToken cancellationToken)
        {
            if (!await Said(() => client.DownloadStream(output, path, token: cancellationToken), path))
            {
                throw new InvalidOperationException($"The FTP server did not send '{path}'.");
            }
        }

        public async Task UploadAsync(string path, Stream content, CancellationToken cancellationToken)
        {
            // Its folders made here, and STOR replacing a file of its name: FluentFTP's own createRemoteDir and its existence check walk
            // up by "..", which its path sanitizer then refuses.
            if (RemoteFiles.Parent(path) is { } parent)
            {
                await CreateFolderAsync(parent, cancellationToken);
            }

            if (await Said(() => client.UploadStream(content, path, FtpRemoteExists.NoCheck, createRemoteDir: false, token: cancellationToken), path) != FtpStatus.Success)
            {
                throw new InvalidOperationException($"The FTP server did not take '{path}'.");
            }
        }

        public Task CreateFolderAsync(string path, CancellationToken cancellationToken) => Said(() => client.CreateDirectory(path, true, cancellationToken), path);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.DeleteFile(path, cancellationToken);
            return true;
        }, path);

        public Task DeleteFolderAsync(string path, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.DeleteDirectory(path, cancellationToken);
            return true;
        }, path);

        public Task RenameAsync(string from, string to, CancellationToken cancellationToken) => Said(async () =>
        {
            await client.Rename(from, to, cancellationToken);
            return true;
        }, from);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await client.Disconnect(CancellationToken.None);
            }
            catch (Exception ex) when (ex is FtpException or IOException or SocketException or TimeoutException or ObjectDisposedException)
            {
                // The session is over either way.
            }

            client.Dispose();
        }

        private static RemoteEntry Entry(FtpListItem item, string name, string path) => new(name, path, Type(item.Type), item.Type == FtpObjectType.Directory || item.Size < 0 ? null : item.Size,
            item.Modified == default || item.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc)));

        private static string Type(FtpObjectType type) => type switch
        {
            FtpObjectType.Directory => RemoteEntry.Folder,
            FtpObjectType.Link => RemoteEntry.Link,
            _ => RemoteEntry.File,
        };

        // A refusal the server sent, said with its reply rather than as FluentFTP's exception.
        private static async Task<T> Said<T>(Func<Task<T>> call, string path)
        {
            try
            {
                return await call();
            }
            catch (FtpCommandException ex)
            {
                throw new InvalidOperationException($"The FTP server refused it ({path}): {ex.CompletionCode} {ex.Message}");
            }
        }
    }
}