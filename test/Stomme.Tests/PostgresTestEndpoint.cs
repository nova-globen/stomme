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
    /// Env-overridable, defaulting to the <b>shared</b> instance
    /// (<c>192.168.10.20:5433</c>, password from <c>DEFAULT_PASSWORD</c>). The local <c>postgresql-x64-18</c>
    /// Windows service was retired.
    /// </para>
    /// <para>
    /// Overrides (each optional): <c>POSTGRES_TEST_HOST</c>, <c>POSTGRES_TEST_PORT</c>,
    /// <c>POSTGRES_TEST_USER</c>, <c>POSTGRES_TEST_PASSWORD</c>. The password falls back to <c>DEFAULT_PASSWORD</c>
    /// (a credential kept out of source) and finally to the retired local default, so a machine still
    /// running local PG keeps working with a single <c>POSTGRES_TEST_HOST=127.0.0.1</c> / <c>PORT=5432</c>.
    /// </para>
    /// <para>
    /// The account must be able to <c>CREATE DATABASE</c> — the render guard and several drives create a fresh
    /// throwaway database per run (and drop it in teardown). The <c>postgres</c> superuser satisfies this.
    /// </para>
    /// </remarks>
    internal static class PostgresTestEndpoint
    {
        public static string Host =>
            Environment.GetEnvironmentVariable("POSTGRES_TEST_HOST") ?? "localhost";

        public static int Port =>
            int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_TEST_PORT"), out var port) ? port : 5433;

        public static string User =>
            Environment.GetEnvironmentVariable("POSTGRES_TEST_USER") ?? "postgres";

        public static string Password =>
            Environment.GetEnvironmentVariable("POSTGRES_TEST_PASSWORD")
            ?? Environment.GetEnvironmentVariable("DEFAULT_PASSWORD")
            ?? string.Empty; // no password literal: without one the server refuses the sign-in (28P01), naming the cause

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
        /// database is harmless, so any failure here is swallowed — never fail a test on cleanup. Against a
        /// shared instance this matters more: a missed drop leaves a scratch DB others can see.
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
