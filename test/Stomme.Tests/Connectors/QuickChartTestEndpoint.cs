#nullable enable
using System;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The QuickChart connector's live tests' server (BL-229): QUICKCHART_TEST_URL where set, otherwise this machine's throwaway
    /// QuickChart 1.8 that <c>scripts/quickchart-test-server.ps1</c> makes on 127.0.0.1:18093 - never quickchart.io. It takes no
    /// sign-in. Absent, the live tests FAIL with what to run, never skip (D46).
    /// </summary>
    internal static class QuickChartTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("QUICKCHART_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18093";

        public const string Missing = "No QuickChart test server: run 'pwsh scripts/quickchart-test-server.ps1' (a throwaway QuickChart 1.8 on 127.0.0.1:18093) or set QUICKCHART_TEST_URL.";
    }
}
