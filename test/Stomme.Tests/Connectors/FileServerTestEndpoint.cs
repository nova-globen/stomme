#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The FTP and SFTP connectors' live tests' servers: this machine's throwaway vsftpd and OpenSSH that
    /// <c>scripts/ftp-test-server.ps1</c> and <c>scripts/sftp-test-server.ps1</c> make on 127.0.0.1:12121 and :12222 (or
    /// FILESERVER_TEST_HOST, FTP_TEST_PORT and SFTP_TEST_PORT where set), their user <c>kafe</c>; the passwords, the client keys
    /// and the server's host key read from the files those scripts write under the user's temp folder. No file is a FAILURE that
    /// says how to get one, never a skip.
    /// </summary>
    internal static class FileServerTestEndpoint
    {
        public const string User = "kafe";

        /// <summary>The SSH server's user with a shell of its own, for the SSH connector's commands - <see cref="User"/> runs none (SFTP alone); the same password and keys.</summary>
        public const string ShellUser = "ops";

        public static string Host => Environment.GetEnvironmentVariable("FILESERVER_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int FtpPort => int.TryParse(Environment.GetEnvironmentVariable("FTP_TEST_PORT"), out var port) ? port : 12121;

        public static int SftpPort => int.TryParse(Environment.GetEnvironmentVariable("SFTP_TEST_PORT"), out var port) ? port : 12222;

        public static string FtpPassword => Read("stomme-ftp-test", "password", "ftp-test-server.ps1");

        public static string SftpPassword => Read("stomme-sftp-test", "password", "sftp-test-server.ps1");

        /// <summary>The passphrase of the OpenSSH ed25519 client key.</summary>
        public static string KeyPassphrase => Read("stomme-sftp-test", "passphrase", "sftp-test-server.ps1");

        /// <summary>The OpenSSH ed25519 client key, under <see cref="KeyPassphrase"/>.</summary>
        public static string Ed25519Key => Read("stomme-sftp-test", Path.Combine("keys", "client_ed25519"), "sftp-test-server.ps1");

        /// <summary>The unencrypted PEM RSA client key.</summary>
        public static string RsaPemKey => Read("stomme-sftp-test", Path.Combine("keys", "client_rsa.pem"), "sftp-test-server.ps1");

        /// <summary>The server's host key's public line, as its .pub file holds it.</summary>
        public static string HostKeyLine => Read("stomme-sftp-test", Path.Combine("keys", "host_ed25519.pub"), "sftp-test-server.ps1");

        private static string Read(string folder, string name, string script)
        {
            var file = Path.Combine(Path.GetTempPath(), folder, name);
            return File.Exists(file)
                ? File.ReadAllText(file).Trim()
                : throw new InvalidOperationException($"No file server for the live tests: run 'pwsh scripts/{script}' (a throwaway server on 127.0.0.1, its secrets under {Path.GetDirectoryName(file)}).");
        }
    }
}
