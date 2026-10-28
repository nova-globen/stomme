#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Sftp;
using Stomme.Modules.Connectors.Shared;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Stomme.Modules.Connectors.Ssh
{
    /// <summary>
    /// SSH signed in by a password (BL-229, n8n's SSH node): the Basic scheme's <c>username</c>/<c>password</c>, as SSH's
    /// password method - n8n's way. Everything else is <see cref="SshCommandConnectorBase"/>'s.
    /// </summary>
    public sealed class SshConnector : SshCommandConnectorBase
    {
        public override string ConnectorType => "ssh";

        public override string DisplayName => "SSH";

        public override AuthenticationType AuthType => AuthenticationType.Basic;

        internal override (string Username, AuthenticationMethod[] Methods) SignIn(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials) =>
            SshSignIn.Password(credentials, Protocol);
    }

    /// <summary>
    /// SSH signed in by a private key (BL-229, n8n's SSH node's Private Key): the key and its passphrase as SFTP (private key)'s
    /// (<see cref="SftpKeyConnector"/>), the user the connection's <c>username</c> setting.
    /// </summary>
    public sealed class SshKeyConnector : SshCommandConnectorBase
    {
        public override string ConnectorType => "sshkey";

        public override string DisplayName => "SSH (private key)";

        public override AuthenticationType AuthType => AuthenticationType.Certificate;

        public override IReadOnlyDictionary<string, string>? CredentialLabels => SshSignIn.KeyLabels;

        public override ConnectorConfigSchema ConfigSchema { get; } = SshSignIn.KeySchema;

        internal override (string Username, AuthenticationMethod[] Methods) SignIn(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials) =>
            SshSignIn.Key(settings, credentials, Protocol);
    }

    /// <summary>
    /// The SSH connectors' part (BL-229, n8n's SSH node): the SSH server's (<see cref="SshConnectorBase"/> - its address, its
    /// host key proved before anything is sent, the pin), and n8n's operations - a command run on the server, in a folder when
    /// given, its exit code, its signal, what it wrote and what it wrote as errors (each held to 1 MiB, the command stopped
    /// past it; a command that outlasts its time stopped and said so); a file downloaded into the pipeline run's folder and one
    /// uploaded from it, over the server's SFTP (<see cref="RemoteFiles"/>). A command is the step's to give: one line as
    /// <c>command</c>, or as many as it takes in the step's Query box. A connection for files alone is an SFTP one, which runs
    /// no command - a separate type, so a connection made to move files is never one that runs commands.
    /// </summary>
    public abstract class SshCommandConnectorBase : SshConnectorBase, IConnectorExtractor
    {
        public const string ExecuteOperation = "execute";

        /// <summary>The most a command's output - or its errors - is kept, in bytes; past it the command is stopped.</summary>
        public const int OutputCap = 1 << 20;

        internal const string Name = "SSH";

        internal override string Protocol => Name;

        internal override string Example => "ssh://server.kafe.example:22";

        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development, ConnectorCategory.NetworkInfrastructure };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ExecuteOperation, "Run a command", "A command run on the server - in a folder when given - its exit code, signal, output and errors; n8n's Execute.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("command", "Command", ConnectorConfigFieldType.Text, Required: false, Description: "The command, on one line - df -h /. Several lines: the step's Query box."),
                    new ConnectorConfigField("cwd", "Folder", ConnectorConfigFieldType.Text, Required: false, Description: "The folder it runs in - /var/log. Empty: the user's own."),
                    new ConnectorConfigField("timeout", "Seconds", ConnectorConfigFieldType.Integer, Required: false, Description: "How long it may run before it is stopped, 1 to 3600.") { Default = "60" },
                }),
            },
        }.Concat(RemoteFiles.Operations("SSH server").Where(operation => operation.Name is RemoteFiles.DownloadFileOperation or RemoteFiles.UploadOperation)).ToList();

        public IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = (context.OperationName ?? string.Empty).Trim().ToLowerInvariant();
            if (operation is not (ExecuteOperation or RemoteFiles.DownloadFileOperation or RemoteFiles.UploadOperation))
            {
                throw new InvalidOperationException($"'{context.OperationName}' is not an operation of the SSH connector - execute, download_file or upload.");
            }

            var options = Resolve(context.Endpoint, context.Settings, context.Credentials, context.Timeout);
            if (operation != ExecuteOperation)
            {
                return RemoteFiles.RunAsync(context, async token => new SftpFileSession(await ConnectAsync(options, context.Egress, Name, SftpConnectorBase.Sftp, token)), "SSH server", cancellationToken);
            }

            var command = Command.Of(context.Parameters);
            return ExecuteAsync(command, options, context.Egress, cancellationToken);
        }

        internal override async Task<string> ProbeAsync(SshOptions options, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            using var client = await ConnectAsync(options, egress, Name, info => new SshClient(info), cancellationToken);
            client.Disconnect();
            return $"Signed in to the SSH server as {options.Username} - its host key the one this connection trusts.";
        }

        private static async IAsyncEnumerable<ConnectorRecord> ExecuteAsync(Command command, SshOptions options, IConnectorEgress egress, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var client = await ConnectAsync(options, egress, Name, info => new SshClient(info), cancellationToken);
            using var run = client.CreateCommand(command.Script, Encoding.UTF8);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(command.Timeout);
            var output = new Capped(stop);
            var errors = new Capped(stop);
            var executing = run.ExecuteAsync(stop.Token);
            var reading = Task.WhenAll(output.ReadAsync(run.OutputStream), errors.ReadAsync(run.ExtendedOutputStream));
            try
            {
                await executing;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !output.Truncated && !errors.Truncated)
            {
                throw new TimeoutException($"The command ran past its {command.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds and was stopped.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Stopped because what it wrote passed the cap: what was kept is the record's, said truncated.
            }

            await reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            client.Disconnect();
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = run.ExitStatus,
                ["signal"] = run.ExitSignal,
                ["stdout"] = output.Text,
                ["stderr"] = errors.Text,
                ["truncated"] = output.Truncated || errors.Truncated,
            });
        }

        /// <summary>A command as a step gave it: its text - one line, or the Query box's lines - its folder and its time.</summary>
        internal sealed record Command(string Script, TimeSpan Timeout)
        {
            public static Command Of(IReadOnlyDictionary<string, string> p)
            {
                var line = Values.Get(p, "command");
                var lines = p.TryGetValue("query", out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : null;
                if (line is not null && lines is not null)
                {
                    throw new InvalidOperationException("Give the command once - as 'command', or in the step's Query box.");
                }

                var text = line ?? lines ?? throw new InvalidOperationException("Give the command - 'command = ...', or as many lines as it takes in the step's Query box.");
                var folder = Values.Get(p, "cwd");
                if (folder is not null && folder.Any(char.IsControl))
                {
                    throw new InvalidOperationException($"'{folder.Replace("\n", "\\n")}' holds a control character - a folder never does (cwd).");
                }

                var seconds = Values.Get(p, "timeout") is not { } said ? 60
                    : int.TryParse(said, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 3600 ? n
                    : throw new InvalidOperationException($"'{said}' is not a number of seconds from 1 to 3600 (timeout).");
                return new Command(folder is null ? text : "cd " + Quote(folder) + " && " + text, TimeSpan.FromSeconds(seconds));
            }

            /// <summary>A word for a POSIX shell, single-quoted - a quote inside it closed, escaped and opened again.</summary>
            internal static string Quote(string word) => "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        /// <summary>A stream read to its end, kept up to <see cref="OutputCap"/> bytes - past it, the command stopped.</summary>
        private sealed class Capped(CancellationTokenSource stop)
        {
            private readonly MemoryStream kept = new();

            public bool Truncated { get; private set; }

            public string Text => Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length).TrimEnd('\r', '\n');

            public async Task ReadAsync(Stream stream)
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, CancellationToken.None)) > 0)
                    {
                        var room = OutputCap - (int)kept.Length;
                        kept.Write(buffer, 0, Math.Min(read, room));
                        if (read > room)
                        {
                            Truncated = true;
                            await stop.CancelAsync();
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is ObjectDisposedException or IOException or SshException)
                {
                    // The channel closed under it - the command stopped, or ended.
                }
            }
        }
    }
}
