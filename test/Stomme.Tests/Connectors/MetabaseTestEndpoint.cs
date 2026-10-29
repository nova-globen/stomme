#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Metabase connector's live tests' server: METABASE_TEST_HOST / PORT / USER / PASSWORD / API_KEY where set,
    /// otherwise this machine's throwaway Metabase 0.63 that <c>scripts/metabase-test-server.ps1</c> makes on 127.0.0.1:13000 - set
    /// up over its own API, its admin kafe@kafe.example's password and an API key in the files that script writes under the
    /// user's temp folder. Neither is a FAILURE that says how to get one, never a skip.
    /// </summary>
    internal static class MetabaseTestEndpoint
    {
        public static string Host => Environment.GetEnvironmentVariable("METABASE_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("METABASE_TEST_PORT"), out var port) ? port : 13000;

        public static string Endpoint => $"http://{Host}:{Port}";

        public static string User => Environment.GetEnvironmentVariable("METABASE_TEST_USER") is { Length: > 0 } user ? user : "kafe@kafe.example";

        public static string Password => Environment.GetEnvironmentVariable("METABASE_TEST_PASSWORD") is { Length: > 0 } given ? given : Read("password");

        public static string ApiKey => Environment.GetEnvironmentVariable("METABASE_TEST_API_KEY") is { Length: > 0 } given ? given : Read("api-key");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-metabase-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Metabase test server: run 'pwsh scripts/metabase-test-server.ps1' (a throwaway Metabase 0.63 on 127.0.0.1:13000, set up over its API, its admin's password and an API key under {Path.GetDirectoryName(file)}) or set METABASE_TEST_HOST, METABASE_TEST_PORT, METABASE_TEST_USER, METABASE_TEST_PASSWORD and METABASE_TEST_API_KEY.");
        }
    }
}
