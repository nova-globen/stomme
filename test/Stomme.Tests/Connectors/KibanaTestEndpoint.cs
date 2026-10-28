#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Elastic Security connector's live tests' server (BL-229): KIBANA_TEST_URL / USER / PASSWORD where set, otherwise this
    /// machine's throwaway Kibana 9.5 over its own Elasticsearch that <c>scripts/kibana-test-server.ps1</c> makes on
    /// 127.0.0.1:15601 - its elastic user's throwaway password in the file that script writes under the user's temp folder.
    /// Neither is a FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class KibanaTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("KIBANA_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:15601";

        public static string User => Environment.GetEnvironmentVariable("KIBANA_TEST_USER") is { Length: > 0 } user ? user : "elastic";

        public static string Password => Environment.GetEnvironmentVariable("KIBANA_TEST_PASSWORD") is { Length: > 0 } given ? given : Read("password");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-kibana-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Kibana test server: run 'pwsh scripts/kibana-test-server.ps1' (a throwaway Kibana 9.5 over its own Elasticsearch on 127.0.0.1:15601, its elastic user's password under {Path.GetDirectoryName(file)}) or set KIBANA_TEST_URL, KIBANA_TEST_USER and KIBANA_TEST_PASSWORD.");
        }
    }
}
