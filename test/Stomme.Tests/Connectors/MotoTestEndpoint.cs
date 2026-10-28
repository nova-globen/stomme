#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The AWS connectors' live tests' server (BL-229, Amazon Cognito first): MOTO_TEST_HOST / PORT / ACCESS_KEY / SECRET_KEY where
    /// set, otherwise this machine's throwaway moto that <c>scripts/moto-test-server.ps1</c> makes on 127.0.0.1:15005 - which
    /// checks SigV4 signatures against the IAM key that script had it issue, its id and secret read from the file the script
    /// writes under the user's temp folder. No key is a FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class MotoTestEndpoint
    {
        public const string Region = "eu-west-1";

        public static string Host => Environment.GetEnvironmentVariable("MOTO_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("MOTO_TEST_PORT"), out var port) ? port : 15005;

        public static string Endpoint => $"http://{Host}:{Port}";

        public static string AccessKey => Environment.GetEnvironmentVariable("MOTO_TEST_ACCESS_KEY") is { Length: > 0 } given ? given : Key()[0];

        public static string SecretKey => Environment.GetEnvironmentVariable("MOTO_TEST_SECRET_KEY") is { Length: > 0 } given ? given : Key()[1];

        private static string[] Key()
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-moto-test", "key");
            var lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();
            return lines.Length >= 2 && lines[0].Trim().Length > 0 && lines[1].Trim().Length > 0
                ? new[] { lines[0].Trim(), lines[1].Trim() }
                : throw new InvalidOperationException($"No moto test server: run 'pwsh scripts/moto-test-server.ps1' (a throwaway moto on 127.0.0.1:15005 that checks signatures, its IAM key in {file}) or set MOTO_TEST_HOST, MOTO_TEST_PORT, MOTO_TEST_ACCESS_KEY and MOTO_TEST_SECRET_KEY.");
        }
    }
}
