#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Mattermost connector's live tests' server: MATTERMOST_TEST_URL / MATTERMOST_TEST_TOKEN where set, otherwise this
    /// machine's throwaway Mattermost Team Edition 10.11 that <c>scripts/mattermost-test-server.ps1</c> makes on 127.0.0.1:18096 -
    /// its first user's personal access token in the file that script writes under the user's temp folder. Neither is a FAILURE
    /// that says how to get one, never a skip.
    /// </summary>
    internal static class MattermostTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("MATTERMOST_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18096";

        public static string Token => Environment.GetEnvironmentVariable("MATTERMOST_TEST_TOKEN") is { Length: > 0 } given ? given : Read("token");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-mattermost-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Mattermost test server: run 'pwsh scripts/mattermost-test-server.ps1' (a throwaway Mattermost 10.11 on 127.0.0.1:18096, its token under {Path.GetDirectoryName(file)}) or set MATTERMOST_TEST_URL and MATTERMOST_TEST_TOKEN.");
        }
    }
}
