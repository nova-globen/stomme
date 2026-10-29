#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Monica CRM connector's live tests' server: MONICA_TEST_URL / MONICA_TEST_TOKEN where set, otherwise this machine's
    /// throwaway Monica 4.1 that <c>scripts/monica-test-server.ps1</c> makes on 127.0.0.1:18094 - its user's API token, made by
    /// Monica's own application, in the file that script writes under the user's temp folder. Neither is a FAILURE that says how to
    /// get one, never a skip.
    /// </summary>
    internal static class MonicaCrmTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("MONICA_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18094";

        public static string Token => Environment.GetEnvironmentVariable("MONICA_TEST_TOKEN") is { Length: > 0 } given ? given : Read("token");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-monica-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Monica test server: run 'pwsh scripts/monica-test-server.ps1' (a throwaway Monica 4.1 on 127.0.0.1:18094, its API token under {Path.GetDirectoryName(file)}) or set MONICA_TEST_URL and MONICA_TEST_TOKEN.");
        }
    }
}
