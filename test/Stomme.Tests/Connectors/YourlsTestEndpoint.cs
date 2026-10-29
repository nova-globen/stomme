#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The YOURLS connector's live tests' server: YOURLS_TEST_URL / YOURLS_TEST_SIGNATURE where set, otherwise this
    /// machine's throwaway YOURLS 1.10 that <c>scripts/yourls-test-server.ps1</c> makes on 127.0.0.1:18089 - its signature token,
    /// read from YOURLS's own Tools page, in the file that script writes under the user's temp folder. Neither is a FAILURE that
    /// says how to get one, never a skip.
    /// </summary>
    internal static class YourlsTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("YOURLS_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18089";

        public static string Signature => Environment.GetEnvironmentVariable("YOURLS_TEST_SIGNATURE") is { Length: > 0 } given ? given : Read("signature");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-yourls-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No YOURLS test server: run 'pwsh scripts/yourls-test-server.ps1' (a throwaway YOURLS 1.10 on 127.0.0.1:18089, its signature token under {Path.GetDirectoryName(file)}) or set YOURLS_TEST_URL and YOURLS_TEST_SIGNATURE.");
        }
    }
}
