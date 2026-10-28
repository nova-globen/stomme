#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The ERPNext connector's live tests' server (BL-229): ERPNEXT_TEST_URL / ERPNEXT_TEST_KEY / ERPNEXT_TEST_SECRET where set,
    /// otherwise this machine's throwaway ERPNext 16 that <c>scripts/erpnext-test-server.ps1</c> makes on 127.0.0.1:18097 - its
    /// Administrator's API key and secret, made by Frappe's own code, in the files that script writes under the user's temp
    /// folder. Neither is a FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class ErpNextTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("ERPNEXT_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18097";

        public static string Key => Environment.GetEnvironmentVariable("ERPNEXT_TEST_KEY") is { Length: > 0 } given ? given : Read("key");

        public static string Secret => Environment.GetEnvironmentVariable("ERPNEXT_TEST_SECRET") is { Length: > 0 } given ? given : Read("secret");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-erpnext-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No ERPNext test server: run 'pwsh scripts/erpnext-test-server.ps1' (a throwaway ERPNext 16 on 127.0.0.1:18097, its API key and secret under {Path.GetDirectoryName(file)}) or set ERPNEXT_TEST_URL, ERPNEXT_TEST_KEY and ERPNEXT_TEST_SECRET.");
        }
    }
}
