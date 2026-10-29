#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Ssh;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The SSH connectors without a server: SSH by a password and SSH by a private key, Development and Network &amp;
    /// Infrastructure, n8n's operations - a command, a file's download and its upload - and no other; a command given once, as
    /// <c>command</c> or in the step's Query box, its folder quoted for the shell, its time held from 1 to 3600 seconds; an
    /// operation, a parameter and the address checked before the egress is asked.
    /// </summary>
    public sealed class SshConnectorTests
    {
        private static readonly Dictionary<string, string> Password = new() { ["username"] = "ops", ["password"] = "pw-1" };

        private static Dictionary<string, string> Settings => new() { ["host_key"] = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=') };

        [Fact]
        public void Both_connectors_declare_their_scheme_their_categories_and_n8ns_operations()
        {
            var password = new SshConnector();
            var key = new SshKeyConnector();
            Assert.Equal(("ssh", "SSH", AuthenticationType.Basic), (password.ConnectorType, password.DisplayName, password.AuthType));
            Assert.Equal(("sshkey", "SSH (private key)", AuthenticationType.Certificate), (key.ConnectorType, key.DisplayName, key.AuthType));
            Assert.All(new SshCommandConnectorBase[] { password, key }, connector =>
            {
                Assert.Equal(new[] { ConnectorCategory.Development, ConnectorCategory.NetworkInfrastructure }, connector.Categories);
                Assert.Equal(new[] { "execute", "download_file", "upload" }, connector.Operations.Select(o => o.Name).ToArray());
                Assert.Equal(new[] { "command", "cwd", "timeout" }, connector.Operations[0].Parameters.Fields.Select(f => f.Name).ToArray());
                Assert.Equal("60", connector.Operations[0].Parameters.Fields.Single(f => f.Name == "timeout").Default);
                Assert.Contains("SSH server", connector.Operations[1].Description, StringComparison.Ordinal);
                Assert.Equal(("ssh://server.kafe.example:22", ConnectorEndpointKind.ConnectionString), (((IConnector)connector).EndpointExample, ((IConnector)connector).EndpointKind));
            });
            Assert.Equal(new[] { "host_key" }, password.ConfigSchema.Fields.Select(f => f.Name).ToArray());
            Assert.Equal(new[] { "host_key", "username" }, key.ConfigSchema.Fields.Select(f => f.Name).ToArray());
            Assert.Equal(new[] { "certificate", "passphrase" }, key.CredentialLabels!.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void A_command_is_given_once_its_folder_quoted_and_its_time_held()
        {
            var one = SshCommandConnectorBase.Command.Of(new Dictionary<string, string> { ["command"] = " df -h / " });
            Assert.Equal(("df -h /", TimeSpan.FromSeconds(60)), (one.Script, one.Timeout));
            var many = SshCommandConnectorBase.Command.Of(new Dictionary<string, string> { ["query"] = "\nx=1\necho $((x+1))\n", ["cwd"] = "/var/log", ["timeout"] = "3600" });
            Assert.Equal(("cd '/var/log' && x=1\necho $((x+1))", TimeSpan.FromSeconds(3600)), (many.Script, many.Timeout));
            Assert.Equal("cd '/tmp/med mellan'\\''rum' && pwd", SshCommandConnectorBase.Command.Of(new Dictionary<string, string> { ["command"] = "pwd", ["cwd"] = "/tmp/med mellan'rum" }).Script);
            Assert.Equal("'a'\\''b'\\'''", SshCommandConnectorBase.Command.Quote("a'b'"));
            Assert.Equal(TimeSpan.FromSeconds(1), SshCommandConnectorBase.Command.Of(new Dictionary<string, string> { ["command"] = "true", ["timeout"] = "1" }).Timeout);

            string Refused(Dictionary<string, string> p) => Assert.Throws<InvalidOperationException>(() => SshCommandConnectorBase.Command.Of(p)).Message;
            Assert.Equal("Give the command once - as 'command', or in the step's Query box.", Refused(new() { ["command"] = "ls", ["query"] = "ls" }));
            Assert.Equal("Give the command - 'command = ...', or as many lines as it takes in the step's Query box.", Refused(new() { ["command"] = " ", ["query"] = "\n" }));
            Assert.Equal("'/tmp\\nrm' holds a control character - a folder never does (cwd).", Refused(new() { ["command"] = "ls", ["cwd"] = "/tmp\nrm" }));
            foreach (var bad in new[] { "0", "3601", "a minute", "-5" })
            {
                Assert.Equal($"'{bad}' is not a number of seconds from 1 to 3600 (timeout).", Refused(new() { ["command"] = "ls", ["timeout"] = bad }));
            }
        }

        [Fact]
        public async Task An_operation_a_parameter_and_the_address_are_checked_before_the_egress_is_asked()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "ssh://server.kafe.test")
            {
                var egress = new StubEgress();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SshConnector(), Contexts.Extract(egress, endpoint, operation, parameters, Settings, Password)));
                Assert.Empty(egress.Pins);
                return refusal.Message;
            }

            Assert.Equal("'list' is not an operation of the SSH connector - execute, download_file or upload.", await Refused("list", new()));
            Assert.Equal("'delete' is not an operation of the SSH connector - execute, download_file or upload.", await Refused("delete", new() { ["path"] = "/tmp/x" }));
            Assert.Equal("Give the command - 'command = ...', or as many lines as it takes in the step's Query box.", await Refused("execute", new()));
            Assert.Equal("Give the SSH server's address - ssh://server.kafe.example:22.", await Refused("execute", new() { ["command"] = "ls" }, " "));
            Assert.Equal("'https://server.kafe.test' is not an SSH server's address - ssh://server.kafe.example:22.", await Refused("execute", new() { ["command"] = "ls" }, "https://server.kafe.test"));
            Assert.Equal("Give the path on the server (path).", await Refused("upload", new() { ["text"] = "hej" }));

            var test = await new SshConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), "ssh://server.kafe.test", Settings, new Dictionary<string, string> { ["username"] = "ops" }));
            Assert.Equal("The SSH connection needs its password - or make an SSH (private key) connection to sign in by a key.", test.Message);
            var keyTest = await new SshKeyConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), "ssh://server.kafe.test", Settings, new Dictionary<string, string> { ["certificate"] = "k" }));
            Assert.Equal("The SSH connection needs its username - the user the key signs in as.", keyTest.Message);

            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => Contexts.DrainAsync(new SshConnector(), Contexts.Extract(refused, "ssh://server.kafe.test:2222", "execute", new Dictionary<string, string> { ["command"] = "ls" }, Settings, Password)));
            Assert.Equal("The SSH host 'server.kafe.test' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).", denied.Message);
            Assert.Equal(("server.kafe.test", 2222), refused.Pins.Single());
        }
    }

    /// <summary>
    /// The SSH connectors against a real OpenSSH (this machine's throwaway container, <see cref="FileServerTestEndpoint"/>,
    /// its user with a shell) through the HOST's egress: loopback refused until allowlisted; a server whose key the connection
    /// does not name refused as the contract's own refusal; a password and a key signing in; a command's exit code, output and
    /// errors; a folder - a quote in its name - run in; several lines from the Query box; a command past its time stopped and
    /// said; one writing past the cap stopped, what was kept said truncated; a file uploaded and downloaded over the server's
    /// SFTP. Fails, never skips, without a server.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SshConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Address => $"ssh://{FileServerTestEndpoint.Host}:{FileServerTestEndpoint.SftpPort}";

        private static string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(FileServerTestEndpoint.HostKeyLine.Split(' ')[1]))).TrimEnd('=');

        private static Dictionary<string, string> Settings(string? hostKey = null, string? username = null)
        {
            var settings = new Dictionary<string, string> { ["host_key"] = hostKey ?? Fingerprint };
            if (username is not null)
            {
                settings["username"] = username;
            }

            return settings;
        }

        private static Dictionary<string, string> Credentials => new() { ["username"] = FileServerTestEndpoint.ShellUser, ["password"] = FileServerTestEndpoint.SftpPassword };

        private static HostConnectorEgress Egress => new(new[] { FileServerTestEndpoint.Host });

        private static async Task<IReadOnlyDictionary<string, object?>> Run(Dictionary<string, string> parameters, IConnectorExtractor? connector = null, Dictionary<string, string>? settings = null, Dictionary<string, string>? credentials = null) =>
            Assert.Single(await Contexts.DrainAsync(connector ?? new SshConnector(), Contexts.Extract(Egress, Address, "execute", parameters, settings ?? Settings(), credentials ?? Credentials, timeoutSeconds: 15))).Fields;

        [Fact]
        public async Task The_server_proves_its_key_and_a_password_or_a_key_signs_in()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new SshConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Address, Settings(), Credentials, timeoutSeconds: 15)));
            await Assert.ThrowsAsync<ConnectorHostKeyUntrustedException>(() => new SshConnector().TestConnectionAsync(Contexts.Test(Egress, Address, new Dictionary<string, string>(), Credentials, timeoutSeconds: 15)));
            var signedIn = await new SshConnector().TestConnectionAsync(Contexts.Test(Egress, Address, Settings(), Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {signedIn.Success}: {signedIn.Message}");
            Assert.True(signedIn.Success, signedIn.Message);
            Assert.Equal("Signed in to the SSH server as ops - its host key the one this connection trusts.", signedIn.Message);
            var byKey = await Run(new() { ["command"] = "whoami" }, new SshKeyConnector(), Settings(username: FileServerTestEndpoint.ShellUser), new() { ["certificate"] = FileServerTestEndpoint.RsaPemKey });
            Assert.Equal("ops", byKey["stdout"]);
        }

        [Fact]
        public async Task A_command_answers_its_code_output_and_errors_in_its_folder()
        {
            var ran = await Run(new() { ["command"] = "echo hej; echo fel >&2; exit 3" });
            Assert.Equal((3, (object?)null, "hej", "fel", false), ((int?)ran["code"], ran["signal"], (string)ran["stdout"]!, (string)ran["stderr"]!, (bool)ran["truncated"]!));

            var folder = "/tmp/med mellan'rum " + Guid.NewGuid().ToString("N")[..6];
            await Run(new() { ["command"] = "mkdir -p " + SshCommandConnectorBase.Command.Quote(folder) });
            Assert.Equal(folder, (await Run(new() { ["command"] = "pwd", ["cwd"] = folder }))["stdout"]);
            Assert.Equal("2", (await Run(new() { ["query"] = "x=1\necho $((x+1))", ["cwd"] = folder }))["stdout"]);
            await Run(new() { ["command"] = "rm -r " + SshCommandConnectorBase.Command.Quote(folder) });
        }

        [Fact]
        public async Task A_command_past_its_time_or_its_cap_is_stopped()
        {
            var started = DateTime.UtcNow;
            var late = await Assert.ThrowsAsync<TimeoutException>(() => Run(new() { ["command"] = "sleep 20", ["timeout"] = "1" }));
            Assert.Equal("The command ran past its 1 seconds and was stopped.", late.Message);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the command was not stopped at its time");

            var flood = await Run(new() { ["command"] = "head -c 3000000 /dev/zero | tr '\\000' a; echo slut" });
            Assert.True((bool)flood["truncated"]!);
            Assert.Equal(SshCommandConnectorBase.OutputCap, ((string)flood["stdout"]!).Length);
            Assert.DoesNotContain("slut", (string)flood["stdout"]!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_file_is_uploaded_and_downloaded_over_the_servers_sftp()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-ssh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var remote = "/home/ops/ssh-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                var files = PipelineConnectorFiles.Under(root, 1 << 20);
                Task<List<ConnectorRecord>> Files(string operation, Dictionary<string, string> parameters) =>
                    Contexts.DrainAsync(new SshConnector(), Contexts.Extract(Egress, Address, operation, parameters, Settings(), Credentials, timeoutSeconds: 15) with { Files = files });
                Assert.Equal(17L, Assert.Single(await Files("upload", new() { ["path"] = remote + "/kassa/hej.txt", ["text"] = "Kassan är öppen" })).Fields["size"]);
                Assert.Equal("Kassan är öppen", (await Run(new() { ["command"] = "cat kassa/hej.txt", ["cwd"] = remote }))["stdout"]);
                Assert.Single(await Files("download_file", new() { ["path"] = remote + "/kassa/hej.txt", ["file"] = "in/hej.txt" }));
                Assert.Equal("Kassan är öppen", await File.ReadAllTextAsync(Path.Combine(root, "in", "hej.txt")));
            }
            finally
            {
                await Run(new() { ["command"] = "rm -rf " + SshCommandConnectorBase.Command.Quote(remote) });
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
