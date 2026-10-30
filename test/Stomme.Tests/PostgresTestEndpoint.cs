using System;
using System.Net.Sockets;

namespace Stomme.Tests
{
    /// <summary>
    /// Central PostgreSQL test-endpoint resolution for every fixture that installs against a live PG — the
    /// counterpart to the <c>RAVENDB_TEST_URL</c> convention on the RavenDB side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Env-overridable, defaulting to this machine's throwaway PostgreSQL that <c>scripts/postgres-test-server.ps1</c> makes
    /// on <c>127.0.0.1:15433</c> (<c>scripts/gate.ps1 -Full</c> starts it), its password read from the file that script
    /// writes under the user's temp folder. The shared test servers are gone (2026-10-29); a run never leaves this machine.
    /// </para>
    /// <para>
    /// Overrides (each optional): <c>POSTGRES_TEST_HOST</c>, <c>POSTGRES_TEST_PORT</c>,
    /// <c>POSTGRES_TEST_USER</c>, <c>POSTGRES_TEST_PASSWORD</c> (a CI job's service container). No password is a FAILURE
    /// that says how to get one, never a skip.
    /// </para>
    /// <para>
    /// The account must be able to <c>CREATE DATABASE</c> — the render guard and several drives create a fresh
    /// throwaway database per run (and drop it in teardown). The <c>postgres</c> superuser satisfies this.
    /// </para>
    /// </remarks>
    internal static class PostgresTestEndpoint
    {
        public static string Host =>
            Environment.GetEnvironmentVariable("POSTGRES_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port =>
            int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_TEST_PORT"), out var port) ? port : 15433;

        public static string User =>
            Environment.GetEnvironmentVariable("POSTGRES_TEST_USER") is { Length: > 0 } user ? user : "postgres";

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("POSTGRES_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-postgres-test", "password");
                return System.IO.File.Exists(file)
                    ? System.IO.File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No PostgreSQL test server: run 'pwsh scripts/postgres-test-server.ps1' (a throwaway PostgreSQL on 127.0.0.1:15433, its password in {file}) or set POSTGRES_TEST_HOST, POSTGRES_TEST_PORT, POSTGRES_TEST_USER and POSTGRES_TEST_PASSWORD.");
            }
        }
        /// <summary>A connection string to the given database on the resolved endpoint.</summary>
        public static string ConnectionString(string database) =>
            $"Server={Host};Port={Port};Database={database};User Id={User};Password={Password};";

        /// <summary>A TCP-reachability probe gating PG-only cases. An open port is not proof it is PostgreSQL.</summary>
        public static bool IsReachable()
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Best-effort force-drop of a throwaway database created by a test. Connects to the maintenance
        /// <c>postgres</c> database (you cannot drop the database you are connected to) and issues
        /// <c>DROP DATABASE ... WITH (FORCE)</c> (terminates lingering sessions; PostgreSQL 13+). A leftover
        /// database is harmless, so any failure here is swallowed — never fail a test on cleanup (the gate removes
        /// the whole container after a full run).
        /// </summary>
        public static void DropDatabase(string database)
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                return;
            }
            try
            {
                using var connection = new Npgsql.NpgsqlConnection(ConnectionString("postgres"));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                command.ExecuteNonQuery();
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
