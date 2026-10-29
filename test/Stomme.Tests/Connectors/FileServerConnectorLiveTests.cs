#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Ftp;
using Stomme.Modules.Connectors.Sftp;
using Stomme.Modules.Pipelines.Steps;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The file operations against a real server, the same scenario over FTP, FTP with explicit TLS and SFTP: text
    /// uploaded where its folders do not exist yet (made on the way), a file of the run's folder uploaded, the folder listed -
    /// its folders too, its subfolders' entries too - one file downloaded into the run's folder byte for byte, files stored
    /// into a File Manager folder by a pattern, an entry moved where its folders are made, a file deleted, a folder that is not
    /// empty refused until its content is asked for too, the server's root never. Fails, never skips, without a server.
    /// </summary>
    internal static class FileServerScenario
    {
        public static async Task RunAsync(IConnectorExtractor connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, ITestOutputHelper output)
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-fileserver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var files = PipelineConnectorFiles.Under(root, 1 << 20);
                var sink = new CapturingSink();
                var run = "/files/run-" + Guid.NewGuid().ToString("N")[..8];
                var egress = new HostConnectorEgress(new[] { FileServerTestEndpoint.Host });
                Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
                    Contexts.DrainAsync(connector, Contexts.Extract(egress, endpoint, operation, parameters, settings, credentials, timeoutSeconds: 20) with { Files = files, FileSink = sink });

                var text = await Run("upload", new() { ["path"] = run + "/kvitton/2026/hej.txt", ["text"] = "Hej från kassan" });
                Assert.Equal((long)Encoding.UTF8.GetByteCount("Hej från kassan"), Assert.Single(text).Fields["size"]);

                var csv = "nummer;kund;belopp\n1042;Kafé Ada;99.50\n";
                await File.WriteAllTextAsync(Path.Combine(root, "export.csv"), csv);
                var sent = Assert.Single(await Run("upload", new() { ["path"] = run + "/kvitton/1042.csv", ["file"] = "export.csv" }));
                Assert.Equal((long)Encoding.UTF8.GetByteCount(csv), sent.Fields["size"]);

                var top = await Run("list", new() { ["path"] = run + "/kvitton" });
                Assert.Equal(new[] { "1042.csv" }, top.Select(r => (string)r.Fields["name"]!).ToArray()); // files only, by default
                Assert.Equal("file", top[0].Fields["type"]);
                var tree = await Run("list", new() { ["path"] = run, ["recursive"] = "true", ["folders"] = "true" });
                output.WriteLine("[live] " + string.Join(", ", tree.Select(r => r.Fields["type"] + ":" + r.Fields["path"])));
                Assert.Equal(new[] { "kvitton", "1042.csv", "2026", "hej.txt" }, tree.Select(r => (string)r.Fields["name"]!).ToArray());
                Assert.Equal(new[] { "folder", "file", "folder", "file" }, tree.Select(r => (string)r.Fields["type"]!).ToArray());
                Assert.EndsWith("/kvitton/2026/hej.txt", (string)tree[3].Fields["path"]!, StringComparison.Ordinal);
                Assert.All(tree.Where(r => (string)r.Fields["type"]! == "folder"), r => Assert.Null(r.Fields["size"])); // a folder has no size
                var loop = Assert.Single(await Run("list", new() { ["path"] = "/files", ["recursive"] = "true", ["folders"] = "true", ["pattern"] = "loop" }));
                Assert.Equal(("/files/loop", "link"), ((string)loop.Fields["path"]!, (string)loop.Fields["type"]!)); // a link to its own folder, listed and never followed
                Assert.Single(await Run("list", new() { ["path"] = run, ["recursive"] = "true", ["pattern"] = "*.txt" }));
                Assert.Single(await Run("list", new() { ["path"] = run, ["recursive"] = "true", ["folders"] = "true", ["max"] = "1" }));

                var got = Assert.Single(await Run("download_file", new() { ["path"] = run + "/kvitton/1042.csv", ["file"] = "downloads/1042.csv" }));
                Assert.Equal(csv, await File.ReadAllTextAsync(Path.Combine(root, "downloads", "1042.csv")));
                Assert.Single(await Run("download_file", new() { ["path"] = run[1..] + "/kvitton/1042.csv", ["file"] = "downloads/relative.csv" })); // a path from the login folder
                Assert.Equal(csv, await File.ReadAllTextAsync(Path.Combine(root, "downloads", "relative.csv")));
                Assert.Equal((long)Encoding.UTF8.GetByteCount(csv), got.Fields["size"]);
                Assert.Contains("is a folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("download_file", new() { ["path"] = run + "/kvitton", ["file"] = "x.csv" }))).Message);
                Assert.Contains("is not on the", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("download_file", new() { ["path"] = run + "/saknas.csv", ["file"] = "x.csv" }))).Message);

                var stored = Assert.Single(await Run("download", new() { ["path"] = run + "/kvitton", ["pattern"] = "*.csv", ["target_folder"] = "imports" }));
                Assert.Equal("imports/1042.csv", stored.Fields["stored_path"]);
                Assert.Equal(csv, Encoding.UTF8.GetString(sink.Stored.Single(s => s.Name == "1042.csv").Content));

                var moved = Assert.Single(await Run("rename", new() { ["from"] = run + "/kvitton/1042.csv", ["to"] = run + "/arkiv/juni/1042.csv", ["create_folders"] = "true" }));
                Assert.Equal(true, moved.Fields["renamed"]);
                Assert.Equal(new[] { "1042.csv" }, (await Run("list", new() { ["path"] = run + "/arkiv/juni" })).Select(r => (string)r.Fields["name"]!).ToArray());
                Assert.Empty(await Run("list", new() { ["path"] = run + "/kvitton" })); // its folder 2026 is no file

                var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("rename", new() { ["from"] = run + "/saknas.csv", ["to"] = run + "/x.csv" }));
                Assert.Contains(run + "/saknas.csv", missing.Message, StringComparison.Ordinal); // the server's refusal, said
                var gone = Assert.Single(await Run("delete", new() { ["path"] = run + "/arkiv/juni/1042.csv" }));
                Assert.Equal(true, gone.Fields["deleted"]);
                Assert.Contains("is a folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("delete", new() { ["path"] = run + "/kvitton" }))).Message);
                Assert.Contains("is not empty", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("delete", new() { ["path"] = run, ["folder"] = "true" }))).Message);
                Assert.Contains("root", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("delete", new() { ["path"] = "/", ["folder"] = "true", ["recursive"] = "true" }))).Message);
                var all = Assert.Single(await Run("delete", new() { ["path"] = run, ["folder"] = "true", ["recursive"] = "true" }));
                Assert.Equal(5, all.Fields["entries_deleted"]); // kvitton, 2026, hej.txt, arkiv, juni
                Assert.DoesNotContain(await Run("list", new() { ["path"] = "/files", ["folders"] = "true" }), r => (string)r.Fields["name"]! == run["/files/".Length..]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>The File Manager's sink, kept in memory: what was stored, under which name and folder.</summary>
        private sealed class CapturingSink : IConnectorFileSink
        {
            public List<(string Name, string Folder, byte[] Content)> Stored { get; } = new();

            public long MaxBytes => 1 << 20;

            public async Task<int> StoreAsync(string fileName, string folderPath, Stream content, long length, CancellationToken cancellationToken = default)
            {
                using var copy = new MemoryStream();
                await content.CopyToAsync(copy, cancellationToken);
                Stored.Add((fileName, folderPath, copy.ToArray()));
                return Stored.Count;
            }
        }
    }

    /// <summary>
    /// The FTP connector against a real vsftpd (this machine's throwaway container, <see cref="FileServerTestEndpoint"/>)
    /// through the HOST's egress: loopback refused until allowlisted, a test signing in, a wrong password failing; its
    /// self-signed certificate refused under explicit TLS until the connection accepts any; every file operation over plain FTP
    /// and over TLS, its data connections EPSV to the pinned address. Fails, never skips, without a server.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class FtpConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Address => $"ftp://{FileServerTestEndpoint.Host}:{FileServerTestEndpoint.FtpPort}";

        private static Dictionary<string, string> Settings(string encryption = "none", bool anyCertificate = false) => new()
        {
            ["encryption_mode"] = encryption,
            ["danger_accept_any_certificate"] = anyCertificate ? "true" : "false",
        };

        private static Dictionary<string, string> Credentials => new() { ["username"] = FileServerTestEndpoint.User, ["password"] = FileServerTestEndpoint.FtpPassword };

        private static HostConnectorEgress Egress => new(new[] { FileServerTestEndpoint.Host });

        [Fact]
        public async Task A_test_signs_in_a_wrong_password_fails_and_an_untrusted_certificate_is_refused()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new FtpConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Address, Settings(), Credentials, timeoutSeconds: 15)));

            var plain = await new FtpConnector().TestConnectionAsync(Contexts.Test(Egress, Address, Settings(), Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {plain.Success}: {plain.Message}");
            Assert.True(plain.Success, plain.Message);

            var wrong = await new FtpConnector().TestConnectionAsync(Contexts.Test(Egress, Address, Settings(), new Dictionary<string, string> { ["username"] = FileServerTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 15));
            Assert.False(wrong.Success);

            var untrusted = await new FtpConnector().TestConnectionAsync(Contexts.Test(Egress, Address, Settings("explicit"), Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {untrusted.Success}: {untrusted.Message}");
            Assert.False(untrusted.Success); // self-signed, and for ftp.kafe.test

            var accepted = await new FtpConnector().TestConnectionAsync(Contexts.Test(Egress, Address, Settings("explicit", anyCertificate: true), Credentials, timeoutSeconds: 15));
            Assert.True(accepted.Success, accepted.Message);

            var pinned = new StubEgress { Pinned = IPAddress.Parse(FileServerTestEndpoint.Host) };
            var named = await new FtpConnector().TestConnectionAsync(Contexts.Test(pinned, $"ftp://kafe-ftp.invalid:{FileServerTestEndpoint.FtpPort}", Settings(), Credentials, timeoutSeconds: 15));
            Assert.True(named.Success, named.Message); // a name that resolves nowhere: only the pinned address could answer
            Assert.Equal(("kafe-ftp.invalid", FileServerTestEndpoint.FtpPort), pinned.Pins.Single());
        }

        [Fact]
        public Task Every_file_operation_over_plain_ftp() => FileServerScenario.RunAsync(new FtpConnector(), Address, Settings(), Credentials, output);

        [Fact]
        public Task Every_file_operation_over_explicit_tls() => FileServerScenario.RunAsync(new FtpConnector(), Address, Settings("explicit", anyCertificate: true), Credentials, output);

        [Fact]
        public Task Every_file_operation_on_an_older_connections_host_setting() =>
            FileServerScenario.RunAsync(new FtpConnector(), "https://unread.example", new Dictionary<string, string>(Settings())
            {
                ["host"] = FileServerTestEndpoint.Host,
                ["port"] = FileServerTestEndpoint.FtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, Credentials, output); // the host and port a connection made before the address named the server holds
    }

    /// <summary>
    /// The SFTP connectors against a real OpenSSH (this machine's throwaway container, <see cref="FileServerTestEndpoint"/>)
    /// through the HOST's egress: loopback refused until allowlisted; a connection naming no host key refused with the key the
    /// server offered named, one naming another key refused, one naming the server's - by its fingerprint or its public key's
    /// line - signing in; a wrong password failing; the pinned address dialled and never the name; a key signing in - OpenSSH
    /// ed25519 under its passphrase, PEM RSA without one, a key pasted with its line breaks dropped or made spaces - its
    /// passphrase missing or wrong said; every file operation. Fails, never skips, without a server.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SftpConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Fingerprint
        {
            get
            {
                var blob = Convert.FromBase64String(FileServerTestEndpoint.HostKeyLine.Split(' ')[1]);
                return "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(blob)).TrimEnd('=');
            }
        }

        private static string Address => $"sftp://{FileServerTestEndpoint.Host}:{FileServerTestEndpoint.SftpPort}";

        private static Dictionary<string, string> Settings(string? hostKey = null, string? username = null)
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

        private static Dictionary<string, string> Credentials => new() { ["username"] = FileServerTestEndpoint.User, ["password"] = FileServerTestEndpoint.SftpPassword };

        private static HostConnectorEgress Egress => new(new[] { FileServerTestEndpoint.Host });

        private static Task<ConnectionTestResult> Test(IConnector connector, Dictionary<string, string> settings, Dictionary<string, string> credentials, IConnectorEgress? egress = null, string? address = null) =>
            connector.TestConnectionAsync(Contexts.Test(egress ?? Egress, address ?? Address, settings, credentials, timeoutSeconds: 15));

        [Fact]
        public async Task The_server_proves_its_host_key_before_anything_is_sent_and_a_password_signs_in()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => Test(new SftpConnector(), Settings(Fingerprint), Credentials, new HostConnectorEgress(Array.Empty<string>())));

            // Refused as the contract's own exception, which the runner makes the HostKeyUntrusted category; its message - the
            // key the server offered - stays out of every page.
            var unset = await Assert.ThrowsAsync<ConnectorHostKeyUntrustedException>(() => Test(new SftpConnector(), Settings(), Credentials));
            output.WriteLine($"[live] {unset.Message}");
            Assert.Contains("No host key is set", unset.Message, StringComparison.Ordinal);
            Assert.Contains("ssh-ed25519 " + Fingerprint, unset.Message, StringComparison.Ordinal);

            var other = await Assert.ThrowsAsync<ConnectorHostKeyUntrustedException>(() => Test(new SftpConnector(), Settings("SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=')), Credentials));
            Assert.Contains("not one this connection trusts", other.Message, StringComparison.Ordinal);
            var listed = Contexts.Extract(Egress, Address, "list", new Dictionary<string, string> { ["path"] = "/files" }, Settings(), Credentials, timeoutSeconds: 15);
            await Assert.ThrowsAsync<ConnectorHostKeyUntrustedException>(() => Contexts.DrainAsync(new SftpConnector(), listed)); // an operation too

            var byFingerprint = await Test(new SftpConnector(), Settings(Fingerprint), Credentials);
            output.WriteLine($"[live] {byFingerprint.Success}: {byFingerprint.Message}");
            Assert.True(byFingerprint.Success, byFingerprint.Message);
            Assert.True((await Test(new SftpConnector(), Settings(FileServerTestEndpoint.HostKeyLine), Credentials)).Success);
            Assert.True((await Test(new SftpConnector(), Settings("SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=') + ", " + Fingerprint), Credentials)).Success); // one of several

            var wrong = await Test(new SftpConnector(), Settings(Fingerprint), new Dictionary<string, string> { ["username"] = FileServerTestEndpoint.User, ["password"] = "not-the-password" });
            Assert.False(wrong.Success);
            Assert.Contains("refused the sign-in", wrong.Message, StringComparison.Ordinal);

            var pinned = new StubEgress { Pinned = IPAddress.Parse(FileServerTestEndpoint.Host) };
            var named = await Test(new SftpConnector(), Settings(Fingerprint), Credentials, pinned, $"sftp://kafe-sftp.invalid:{FileServerTestEndpoint.SftpPort}");
            Assert.True(named.Success, named.Message); // a name that resolves nowhere: only the pinned address could answer
            Assert.Equal(("kafe-sftp.invalid", FileServerTestEndpoint.SftpPort), pinned.Pins.Single());
        }

        [Fact]
        public async Task A_key_signs_in_with_its_passphrase_and_pasted_without_its_line_breaks()
        {
            var settings = Settings(Fingerprint, FileServerTestEndpoint.User);
            var ed25519 = await Test(new SftpKeyConnector(), settings, new() { ["certificate"] = FileServerTestEndpoint.Ed25519Key, ["passphrase"] = FileServerTestEndpoint.KeyPassphrase });
            output.WriteLine($"[live] {ed25519.Success}: {ed25519.Message}");
            Assert.True(ed25519.Success, ed25519.Message);
            Assert.True((await Test(new SftpKeyConnector(), settings, new() { ["certificate"] = FileServerTestEndpoint.RsaPemKey })).Success);

            var dropped = FileServerTestEndpoint.Ed25519Key.Replace("\r", string.Empty).Replace("\n", string.Empty); // Chrome's paste
            var spaced = FileServerTestEndpoint.RsaPemKey.Replace("\r", string.Empty).Replace("\n", " "); // Firefox's
            Assert.True((await Test(new SftpKeyConnector(), settings, new() { ["certificate"] = dropped, ["passphrase"] = FileServerTestEndpoint.KeyPassphrase })).Success);
            Assert.True((await Test(new SftpKeyConnector(), settings, new() { ["certificate"] = spaced })).Success);

            var missing = await Test(new SftpKeyConnector(), settings, new() { ["certificate"] = FileServerTestEndpoint.Ed25519Key });
            Assert.False(missing.Success);
            Assert.Contains("give its passphrase", missing.Message, StringComparison.Ordinal);
            var wrongPassphrase = await Test(new SftpKeyConnector(), settings, new() { ["certificate"] = FileServerTestEndpoint.Ed25519Key, ["passphrase"] = "not-the-passphrase" });
            output.WriteLine($"[live] {wrongPassphrase.Success}: {wrongPassphrase.Message}");
            Assert.False(wrongPassphrase.Success);
        }

        [Fact]
        public Task Every_file_operation_signed_in_by_a_password() => FileServerScenario.RunAsync(new SftpConnector(), Address, Settings(Fingerprint), Credentials, output);

        [Fact]
        public Task Every_file_operation_signed_in_by_a_key() =>
            FileServerScenario.RunAsync(new SftpKeyConnector(), Address, Settings(Fingerprint, FileServerTestEndpoint.User), new Dictionary<string, string> { ["certificate"] = FileServerTestEndpoint.RsaPemKey }, output);
    }
}
