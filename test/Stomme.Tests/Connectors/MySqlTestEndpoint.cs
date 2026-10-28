#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The MySQL connector's live tests' server (BL-229): MYSQL_TEST_HOST / PORT / USER / PASSWORD where set (a CI job's
    /// service container), otherwise this machine's throwaway container that <c>scripts/mysql-test-server.ps1</c> makes on
    /// 127.0.0.1:13306, its root password read from the file that script writes under the user's temp folder. No password
    /// is a FAILURE that says how to get one, never a skip (D46: a suite that quietly shrinks proves nothing).
    /// </summary>
    internal static class MySqlTestEndpoint
    {
        public static string Host => Environment.GetEnvironmentVariable("MYSQL_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("MYSQL_TEST_PORT"), out var port) ? port : 13306;

        public static string User => Environment.GetEnvironmentVariable("MYSQL_TEST_USER") is { Length: > 0 } user ? user : "root";

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("MYSQL_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-mysql-test", "root-password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No MySQL test server: run 'pwsh scripts/mysql-test-server.ps1' (a throwaway MySQL on 127.0.0.1:13306, its password in {file}) or set MYSQL_TEST_HOST, MYSQL_TEST_PORT, MYSQL_TEST_USER and MYSQL_TEST_PASSWORD.");
            }
        }
    }
}
