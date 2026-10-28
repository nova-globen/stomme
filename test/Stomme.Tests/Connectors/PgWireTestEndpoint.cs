#nullable enable
using System;
using System.IO;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The live tests' QuestDB and CrateDB (BL-229) - databases that speak PostgreSQL's protocol, which n8n's QuestDB and
    /// CrateDB nodes come over as the PostgreSQL connector's: this machine's throwaway servers that
    /// <c>scripts/questdb-test-server.ps1</c> and <c>scripts/cratedb-test-server.ps1</c> make on 127.0.0.1:18812 and :15432
    /// (or PGWIRE_TEST_HOST, QUESTDB_TEST_PORT and CRATEDB_TEST_PORT where set), their user <c>kafe</c>; the passwords read
    /// from the files those scripts write under the user's temp folder, or QUESTDB_TEST_PASSWORD and CRATEDB_TEST_PASSWORD.
    /// No password is a FAILURE that says how to get one, never a skip (D46).
    /// </summary>
    internal static class PgWireTestEndpoint
    {
        public const string User = "kafe";

        public static string Host => Environment.GetEnvironmentVariable("PGWIRE_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int QuestDbPort => int.TryParse(Environment.GetEnvironmentVariable("QUESTDB_TEST_PORT"), out var port) ? port : 18812;

        public static int CrateDbPort => int.TryParse(Environment.GetEnvironmentVariable("CRATEDB_TEST_PORT"), out var port) ? port : 15432;

        public static string QuestDbPassword => Read("QUESTDB_TEST_PASSWORD", "stomme-questdb-test", "questdb-test-server.ps1");

        public static string CrateDbPassword => Read("CRATEDB_TEST_PASSWORD", "stomme-cratedb-test", "cratedb-test-server.ps1");

        private static string Read(string variable, string folder, string script)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } given)
            {
                return given;
            }

            var file = Path.Combine(Path.GetTempPath(), folder, "password");
            return File.Exists(file)
                ? File.ReadAllText(file).Trim()
                : throw new InvalidOperationException($"No test server: run 'pwsh scripts/{script}' (a throwaway server on 127.0.0.1, its password in {file}) or set {variable}.");
        }
    }
}
