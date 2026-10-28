#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The TheHive connector's live tests' server (BL-229): THEHIVE_TEST_URL / THEHIVE_TEST_API_KEY where set, otherwise this
    /// machine's throwaway TheHive 5.5 that <c>scripts/thehive-test-server.ps1</c> makes on 127.0.0.1:19000 - its analyst's API
    /// key, of TheHive's own making, in the file that script writes under the user's temp folder. Neither is a FAILURE that says
    /// how to get one, never a skip (D46).
    /// </summary>
    internal static class TheHiveTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("THEHIVE_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:19000";

        public static string ApiKey => Environment.GetEnvironmentVariable("THEHIVE_TEST_API_KEY") is { Length: > 0 } given ? given : Read("api-key");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-thehive-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No TheHive test server: run 'pwsh scripts/thehive-test-server.ps1' (a throwaway TheHive 5.5 on 127.0.0.1:19000, its analyst's API key under {Path.GetDirectoryName(file)}) or set THEHIVE_TEST_URL and THEHIVE_TEST_API_KEY.");
        }
    }
}
