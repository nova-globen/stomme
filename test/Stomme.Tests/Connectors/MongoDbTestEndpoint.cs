#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The MongoDB connector's live tests' server: MONGODB_TEST_HOST / PORT / USER / PASSWORD where set (a CI job's
    /// service container), otherwise this machine's throwaway MongoDB that <c>scripts/mongodb-test-server.ps1</c> makes on
    /// 127.0.0.1:27018, its root user's password read from the file that script writes under the user's temp folder. No
    /// password is a FAILURE that says how to get one, never a skip.
    /// </summary>
    internal static class MongoDbTestEndpoint
    {
        public static string Host => Environment.GetEnvironmentVariable("MONGODB_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("MONGODB_TEST_PORT"), out var port) ? port : 27018;

        public static string User => Environment.GetEnvironmentVariable("MONGODB_TEST_USER") is { Length: > 0 } user ? user : "root";

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("MONGODB_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-mongodb-test", "root-password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No MongoDB test server: run 'pwsh scripts/mongodb-test-server.ps1' (a throwaway MongoDB on 127.0.0.1:27018, its password in {file}) or set MONGODB_TEST_HOST, MONGODB_TEST_PORT, MONGODB_TEST_USER and MONGODB_TEST_PASSWORD.");
            }
        }
    }
}
