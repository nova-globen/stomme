#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Wekan connector's live tests' server (BL-229): WEKAN_TEST_URL / WEKAN_TEST_USERNAME / WEKAN_TEST_PASSWORD where set,
    /// otherwise this machine's throwaway Wekan 12 that <c>scripts/wekan-test-server.ps1</c> makes on 127.0.0.1:18090 - its user
    /// kafe, registered by that script, and the password in the file it writes under the user's temp folder. Neither is a
    /// FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class WekanTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("WEKAN_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18090";

        public static string Username => Environment.GetEnvironmentVariable("WEKAN_TEST_USERNAME") is { Length: > 0 } given ? given : "kafe";

        public static string Password => Environment.GetEnvironmentVariable("WEKAN_TEST_PASSWORD") is { Length: > 0 } given ? given : Read("password");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-wekan-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Wekan test server: run 'pwsh scripts/wekan-test-server.ps1' (a throwaway Wekan 12 on 127.0.0.1:18090, its user's password under {Path.GetDirectoryName(file)}) or set WEKAN_TEST_URL, WEKAN_TEST_USERNAME and WEKAN_TEST_PASSWORD.");
        }
    }
}
