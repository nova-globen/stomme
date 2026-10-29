using System;
using System.Net.Sockets;

namespace Stomme.Tests
{
    /// <summary>
    /// Central SQL Server test-endpoint resolution - the counterpart of <see cref="PostgresTestEndpoint"/> (which
    /// needed a module-aggregate store measured on SQL Server and found no suite that could reach one).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Env-overridable, defaulting to the <b>shared INFRA TIER UAT</b> instance (<c>192.168.10.20:1434</c>, SQL Server 2025
    /// Developer) as <c>sa</c>, with the password from <c>DEFAULT_PASSWORD</c>, set in the environment of the test run (the
    /// shared server keeps the SA password with PostgreSQL's).
    /// </para>
    /// <para>
    /// Overrides (each optional): <c>SQLSERVER_TEST_HOST</c>, <c>SQLSERVER_TEST_PORT</c>, <c>SQLSERVER_TEST_USER</c>,
    /// <c>SQLSERVER_TEST_PASSWORD</c>. The account must be able to <c>CREATE DATABASE</c>; <c>sa</c> can.
    /// </para>
    /// </remarks>
    internal static class SqlServerTestEndpoint
    {
        public static string Host =>
            Environment.GetEnvironmentVariable("SQLSERVER_TEST_HOST") ?? "localhost";

        public static int Port =>
            int.TryParse(Environment.GetEnvironmentVariable("SQLSERVER_TEST_PORT"), out var port) ? port : 1434;

        public static string User =>
            Environment.GetEnvironmentVariable("SQLSERVER_TEST_USER") ?? "sa";

        public static string Password =>
            Environment.GetEnvironmentVariable("SQLSERVER_TEST_PASSWORD")
            ?? Environment.GetEnvironmentVariable("DEFAULT_PASSWORD")
            ?? "";

        /// <summary>A connection string to the given database on the resolved endpoint.</summary>
        public static string ConnectionString(string database) =>
            $"Server={Host},{Port};Database={database};User Id={User};Password={Password};TrustServerCertificate=True;";

        /// <summary>A TCP-reachability probe. An open port is not proof it is SQL Server.</summary>
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
        /// Best-effort drop of a throwaway database: single-user with rollback first, because SQL Server refuses to drop a
        /// database with open sessions (pooled ones included). A leftover is harmless but visible on a SHARED instance, so
        /// the caller clears its pool before calling this.
        /// </summary>
        public static void DropDatabase(string database)
        {
            if (string.IsNullOrWhiteSpace(database))
            {
                return;
            }

            try
            {
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString("master"));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
                command.ExecuteNonQuery();
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
