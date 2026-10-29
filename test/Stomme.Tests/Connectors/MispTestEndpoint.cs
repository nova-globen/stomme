#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The MISP connector's live tests' server: MISP_TEST_URL / MISP_TEST_API_KEY where set, otherwise this machine's
    /// throwaway MISP 2.5 that <c>scripts/misp-test-server.ps1</c> makes on 127.0.0.1:18085 - its administrator's API key, of
    /// MISP's own making, in the file that script writes under the user's temp folder. Neither is a FAILURE that says how to get
    /// one, never a skip.
    /// </summary>
    internal static class MispTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("MISP_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18085";

        public static string ApiKey => Environment.GetEnvironmentVariable("MISP_TEST_API_KEY") is { Length: > 0 } given ? given : Read("api-key");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-misp-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No MISP test server: run 'pwsh scripts/misp-test-server.ps1' (a throwaway MISP 2.5 on 127.0.0.1:18085, its administrator's API key under {Path.GetDirectoryName(file)}) or set MISP_TEST_URL and MISP_TEST_API_KEY.");
        }
    }
}
