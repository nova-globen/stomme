#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Invoice Ninja connector's live tests' server: INVOICENINJA_TEST_URL / INVOICENINJA_TEST_TOKEN where set,
    /// otherwise this machine's throwaway Invoice Ninja 5.13 that <c>scripts/invoiceninja-test-server.ps1</c> makes on
    /// 127.0.0.1:18095 - its user's API token, from one sign-in over Invoice Ninja's API, in the file that script writes under the
    /// user's temp folder. Neither is a FAILURE that says how to get one, never a skip.
    /// </summary>
    internal static class InvoiceNinjaTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("INVOICENINJA_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18095";

        public static string Token => Environment.GetEnvironmentVariable("INVOICENINJA_TEST_TOKEN") is { Length: > 0 } given ? given : Read("token");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-invoiceninja-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Invoice Ninja test server: run 'pwsh scripts/invoiceninja-test-server.ps1' (a throwaway Invoice Ninja 5.13 on 127.0.0.1:18095, its API token under {Path.GetDirectoryName(file)}) or set INVOICENINJA_TEST_URL and INVOICENINJA_TEST_TOKEN.");
        }
    }
}
