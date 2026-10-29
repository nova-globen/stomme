#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Rundeck connector's live tests' server: RUNDECK_TEST_URL / RUNDECK_TEST_TOKEN where set, otherwise this machine's
    /// throwaway Rundeck 6.2 that <c>scripts/rundeck-test-server.ps1</c> makes on 127.0.0.1:18091 - its user kafe's API token in the
    /// file that script writes under the user's temp folder, its project kafe and jobs made by it. Neither is a FAILURE that says
    /// how to get one, never a skip.
    /// </summary>
    internal static class RundeckTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("RUNDECK_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18091";

        public static string Token => Environment.GetEnvironmentVariable("RUNDECK_TEST_TOKEN") is { Length: > 0 } given ? given : Read("token");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-rundeck-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Rundeck test server: run 'pwsh scripts/rundeck-test-server.ps1' (a throwaway Rundeck 6.2 on 127.0.0.1:18091, its API token under {Path.GetDirectoryName(file)}) or set RUNDECK_TEST_URL and RUNDECK_TEST_TOKEN.");
        }
    }
}
