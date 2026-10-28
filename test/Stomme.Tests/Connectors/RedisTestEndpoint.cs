#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Redis connector's live tests' server (BL-229): REDIS_TEST_HOST / PORT / PASSWORD where set (a CI job's service
    /// container), otherwise this machine's throwaway Valkey that <c>scripts/redis-test-server.ps1</c> makes on
    /// 127.0.0.1:16379, its password read from the file that script writes under the user's temp folder. No password is a
    /// FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class RedisTestEndpoint
    {
        public static string Host => Environment.GetEnvironmentVariable("REDIS_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("REDIS_TEST_PORT"), out var port) ? port : 16379;

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("REDIS_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-redis-test", "password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No Redis test server: run 'pwsh scripts/redis-test-server.ps1' (a throwaway Valkey on 127.0.0.1:16379, its password in {file}) or set REDIS_TEST_HOST, REDIS_TEST_PORT and REDIS_TEST_PASSWORD.");
            }
        }
    }
}
