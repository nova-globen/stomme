using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Stomme.Databases;

namespace Stomme.Database.Sqlite
{
    public class SqliteDatabase : DatabaseBase
    {
        private static string _friendlyName => "SQLite";

        private static string _name => "Sqlite";

        static SqliteDatabase()
        {
            Initialize(typeof(SqliteDatabase));
        }

        public SqliteDatabase() :base(_name, _friendlyName) { }

        public override string Provider => "Microsoft.EntityFrameworkCore.Sqlite";

        public override OperationBuilder<AddColumnOperation> AddAutoIncrementColumn(ColumnsBuilder table, string name)
        {
            return table.Column<int>(name: name, nullable: false).Annotation("Sqlite:Autoincrement", true);
        }

        public override void DropColumn(MigrationBuilder builder, string name, string table)
        {
            // SQLite supports dropping columns starting with version 3.35.0 but EF Core does not implement it yet
            // note that a column cannot be dropped if it has a UNIQUE constraint, is part of a PRIMARY KEY, is indexed, or is referenced by other parts of the schema
            builder.Sql($"ALTER TABLE {table} DROP COLUMN {name};");
        }

        public override void AlterStringColumn(MigrationBuilder builder, string name, string table, int length, bool nullable, bool unicode, string index)
        {
            // not implemented as SQLite does not support altering columns
            // note that column length does not need to be modified as SQLite uses a TEXT type which utilizes variable length strings
        }

        public override string ConcatenateSql(params string[] values)
        {
            var returnValue = String.Empty;
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    returnValue += " || ";
                }
                returnValue += values[i];
            }

            return returnValue;
        }

        public override async Task<int> ExecuteNonQueryAsync(string connectionString, string query)
        {
            EnsureWalJournal(connectionString);

            var conn = new SqliteConnection(connectionString);
            var cmd = conn.CreateCommand();
            await using (conn)
            {
                await PrepareCommandAsync(conn, cmd, query);
                var val = -1;
                try
                {
                    val = await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // an error occurred executing the query
                }
                return val;
            }

        }

        public override async Task<IDataReader> ExecuteReaderAsync(string connectionString, string query)
        {
            EnsureWalJournal(connectionString);

            var conn = new SqliteConnection(connectionString);
            var cmd = conn.CreateCommand();
            await PrepareCommandAsync(conn, cmd, query);
            var dr = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
            return dr;
        }

        public override string DelimitName(string name)
        {
            return $"\"{name}\"";
        }

        public override string RewriteValue(object value)
        {
            var type = value.GetType().Name;
            if (type == "DateTime")
            {
                return $"'{value}'";
            }
            if (type == "Boolean")
            {
                return (bool)value ? "1" : "0"; // SQLite uses 1/0 for boolean values
            }
            return value.ToString();
        }

        public override DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder optionsBuilder, string connectionString)
        {
            EnsureWalJournal(connectionString);

            return optionsBuilder.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(BusyTimeoutSeconds(connectionString)))
                .ReplaceService<IHistoryRepository, HistoryRepository>();
        }

        /// <summary>The busy timeout when the connection string does not set one: how long a command waits on a locked file.</summary>
        public const int DefaultBusyTimeoutSeconds = 30;

        /// <summary>
        /// How long a command waits for a SQLite file another connection has locked before it fails with
        /// <c>SQLite Error 5: 'database is locked'</c>: the connection string's own <c>Default Timeout</c> when it sets one,
        /// otherwise <see cref="DefaultBusyTimeoutSeconds"/>.
        /// </summary>
        /// <remarks>
        /// <c>Microsoft.Data.Sqlite</c> retries a busy file for the COMMAND's timeout, so the wait is applied to every command
        /// - EF's <c>CommandTimeout</c> and the raw commands below - rather than by rewriting the connection string, which
        /// keys the connection pool (see <see cref="EnsureWalJournal"/>). Configure it in the connection string:
        /// <c>Data Source=...;Default Timeout=60;</c>. Every SQLite store the host opens (the tenant stores, the module
        /// aggregates, the feature store, the pipeline engine) uses it. Pinned by <c>SqliteBusyTimeoutTests</c>.
        /// </remarks>
        public static int BusyTimeoutSeconds(string connectionString)
        {
            try
            {
                // The plain builder holds only the keys the string WRITES (SqliteConnectionStringBuilder.ContainsKey answers
                // true for every keyword it knows); "Command Timeout" is the same setting's other name.
                var written = new DbConnectionStringBuilder { ConnectionString = connectionString };
                if (!written.ContainsKey("Default Timeout") && !written.ContainsKey("Command Timeout"))
                {
                    return DefaultBusyTimeoutSeconds;
                }
                return new SqliteConnectionStringBuilder(connectionString).DefaultTimeout;
            }
            catch (ArgumentException)
            {
                return DefaultBusyTimeoutSeconds;
            }
        }

        /// <summary>
        /// Puts a file-backed SQLite install into WAL, once per database file per process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The hazard is real on a supported install, not just in tests.</b> A file-backed SQLite database
        /// admits ONE writer, and a composed Stomme host writes from two places at once: the request being
        /// served, and the D34 log drain flushing from a background thread. Under SQLite's default journal mode
        /// a READER also blocks a writer that has already begun its transaction, so the loser gets
        /// <c>SQLite Error 5: 'database is locked'</c>. Measured on the test harness: the host-installing suites
        /// failed this way about one full run in three, a different case each time. Nothing about that is
        /// peculiar to tests — it is what a single-writer file does under concurrency.
        /// </para>
        /// <para>
        /// <b>WAL is the setting that removes the class</b>, because readers no longer block the writer. It is a
        /// property of the FILE, recorded in the database header and persisting across connections and restarts,
        /// which is why this is a one-off pragma rather than a connection-string change. That distinction is
        /// load-bearing: <c>Microsoft.Data.Sqlite</c> keys its connection POOL on the raw connection string, so
        /// rewriting the string here would split one database across two pools — two competing writers, the
        /// opposite of the intent — and would quietly defeat every teardown that releases a pool by the string it
        /// was given.
        /// </para>
        /// <para>
        /// <b>A busy timeout is NOT added, because there already is one</b> — measured 2026-09-20:
        /// <c>SqliteConnectionStringBuilder.DefaultTimeout</c> is 30 seconds with nothing set. An earlier note
        /// beside the test harness read as though its explicit <c>Default Timeout=30</c> supplied that wait; it
        /// restates the default and changes nothing. Recorded so the next reader does not add a third copy of it.
        /// <b>CORRECTION 2026-10-28:</b> the wait is now EXPLICIT - <see cref="BusyTimeoutSeconds"/> applies it to every
        /// command, 30 seconds unless the connection string's <c>Default Timeout</c> says otherwise - and pinned by a test,
        /// so it no longer rests on the library's default staying 30.
        /// </para>
        /// <para>
        /// Best effort by construction: an in-memory database, a read-only connection, or a file that cannot be
        /// opened yet simply stays as it is, and the failure surfaces where the database is actually used rather
        /// than here.
        /// </para>
        /// </remarks>
        internal static void EnsureWalJournal(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            string dataSource;
            try
            {
                dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            }
            catch (ArgumentException)
            {
                return; // not a SQLite connection string; whoever passed it will fail louder elsewhere
            }

            // An in-memory database has no file to put into WAL, and no single-writer file hazard either.
            if (string.IsNullOrEmpty(dataSource) ||
                dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Once per database file per process. UseDatabase runs on every OnConfiguring, which is far too often
            // to open a connection and issue a pragma each time.
            if (!_walEnsured.TryAdd(dataSource, true))
            {
                return;
            }

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Read-only, locked, or not yet creatable: leave the journal mode alone.
                _walEnsured.TryRemove(dataSource, out _);
            }
            catch (InvalidOperationException)
            {
                _walEnsured.TryRemove(dataSource, out _);
            }
        }

        /// <summary>Database files already put into WAL by this process, keyed by data source.</summary>
        private static readonly ConcurrentDictionary<string, bool> _walEnsured =
            new(StringComparer.OrdinalIgnoreCase);

        private async Task PrepareCommandAsync(SqliteConnection conn, SqliteCommand cmd, string query)
        {
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }
            cmd.Connection = conn;
            cmd.CommandText = query;
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = BusyTimeoutSeconds(conn.ConnectionString);
        }
    }
}
