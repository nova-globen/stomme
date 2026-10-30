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
    /// Env-overridable, defaulting to this machine's throwaway SQL Server 2025 Developer that
    /// <c>scripts/sqlserver-test-server.ps1</c> makes on <c>127.0.0.1:11433</c> (<c>scripts/gate.ps1 -Full</c> starts it), as
    /// <c>sa</c>, its password read from the file that script writes under the user's temp folder. The shared test servers
    /// are gone (2026-10-29); a run never leaves this machine.
    /// </para>
    /// <para>
    /// Overrides (each optional): <c>SQLSERVER_TEST_HOST</c>, <c>SQLSERVER_TEST_PORT</c>, <c>SQLSERVER_TEST_USER</c>,
    /// <c>SQLSERVER_TEST_PASSWORD</c>. The account must be able to <c>CREATE DATABASE</c>; <c>sa</c> can. No password is a
    /// FAILURE that says how to get one, never a skip.
    /// </para>
    /// </remarks>
    internal static class SqlServerTestEndpoint
    {
        public static string Host =>
            Environment.GetEnvironmentVariable("SQLSERVER_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port =>
            int.TryParse(Environment.GetEnvironmentVariable("SQLSERVER_TEST_PORT"), out var port) ? port : 11433;

        public static string User =>
            Environment.GetEnvironmentVariable("SQLSERVER_TEST_USER") is { Length: > 0 } user ? user : "sa";

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("SQLSERVER_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-sqlserver-test", "sa-password");
                return System.IO.File.Exists(file)
                    ? System.IO.File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No SQL Server test server: run 'pwsh scripts/sqlserver-test-server.ps1' (a throwaway SQL Server on 127.0.0.1:11433, its password in {file}) or set SQLSERVER_TEST_HOST, SQLSERVER_TEST_PORT, SQLSERVER_TEST_USER and SQLSERVER_TEST_PASSWORD.");
            }
        }

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
        /// database with open sessions (pooled ones included). The caller clears its pool before calling this; a leftover is
        /// harmless (the gate removes the whole container after a full run).
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
