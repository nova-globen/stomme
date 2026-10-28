#nullable enable
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What a real file-backed SQLite install gets for its single write lock (BL-104, product side).
    /// </summary>
    public class SqliteWalJournalTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _directory;

        public SqliteWalJournalTests(ITestOutputHelper output)
        {
            _output = output;
            _directory = Path.Combine(Path.GetTempPath(), "stomme-sqlite-wal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [Fact]
        public void The_library_default_busy_timeout_is_measured_not_assumed()
        {
            // Before adding a busy timeout to anything, find out what there already is. The scratch-test helper
            // sets "Default Timeout=30" explicitly and the note beside it reads as though that is what supplies
            // the busy wait - so if the library already defaults to the same value, that setting does nothing and
            // the product-side fix is WAL alone. Recorded here rather than believed either way.
            var stated = new SqliteConnectionStringBuilder("Data Source=x.db").DefaultTimeout;
            _output.WriteLine($"[default] SqliteConnectionStringBuilder.DefaultTimeout = {stated}s");

            Assert.True(stated > 0, "a non-positive default would mean every busy write fails immediately");
        }

        [Fact]
        public void A_fresh_scratch_database_is_in_the_rollback_journal_by_default()
        {
            // The baseline the product fix moves off: SQLite's default journal mode is `delete`, under which a
            // READER blocks a writer that has already begun its transaction. That is the class WAL removes, and
            // it is why a busy timeout alone was not enough when this was measured on the test harness.
            var path = Path.Combine(_directory, "plain.db");
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();

            var mode = JournalMode(connection);
            _output.WriteLine($"[baseline] a fresh database reports journal_mode={mode}");

            Assert.NotEqual("wal", mode);
        }

        [Fact]
        public void Composing_a_context_through_the_provider_puts_the_database_into_WAL()
        {
            // The product path: every framework DbContext on a SQLite install is configured through
            // IDatabase.UseDatabase, so that is where the journal mode has to be settled - not in a test helper.
            var path = Path.Combine(_directory, "composed.db");
            var connectionString = $"Data Source={path}";

            new Stomme.Database.Sqlite.SqliteDatabase()
                .UseDatabase(new DbContextOptionsBuilder(), connectionString);

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var mode = JournalMode(connection);
            _output.WriteLine($"[composed] after UseDatabase, journal_mode={mode}");
            Assert.Equal("wal", mode);
        }

        [Fact]
        public void The_connection_string_is_handed_on_UNCHANGED()
        {
            // The reason WAL is a pragma here and not a connection-string option. Microsoft.Data.Sqlite keys its
            // connection POOL on the raw string (ScratchSqliteReleaseTests measures how literally), so rewriting
            // it would split one database file across two pools - two writers competing for the single write
            // lock, which is the opposite of the intent - and would silently defeat every teardown that releases
            // a pool by the string it was given.
            var path = Path.Combine(_directory, "unchanged.db");
            var original = $"Data Source={path}";

            var builder = new DbContextOptionsBuilder();
            new Stomme.Database.Sqlite.SqliteDatabase().UseDatabase(builder, original);

            var extension = builder.Options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure
                .Internal.SqliteOptionsExtension>();
            Assert.NotNull(extension);

            _output.WriteLine($"[unchanged] in '{original}' -> out '{extension!.ConnectionString}'");
            Assert.Equal(original, extension.ConnectionString);
        }

        [Fact]
        public void WAL_persists_for_later_connections_which_is_why_it_is_set_once()
        {
            // WAL is recorded in the database header, so it survives the connection that set it. That is what
            // makes the once-per-file guard correct rather than a shortcut, and it is asserted rather than
            // assumed because the guard would be a silent defect if the mode did NOT persist.
            var path = Path.Combine(_directory, "persists.db");
            var connectionString = $"Data Source={path}";

            Stomme.Database.Sqlite.SqliteDatabase.EnsureWalJournal(connectionString);

            // A completely separate connection, opened after the first has gone.
            using (var first = new SqliteConnection(connectionString))
            {
                first.Open();
                Assert.Equal("wal", JournalMode(first));
            }

            ScratchSqlite.Release(connectionString);

            using var later = new SqliteConnection(connectionString);
            later.Open();
            Assert.Equal("wal", JournalMode(later));
            _output.WriteLine("[persists] a later, unrelated connection still reports wal");
        }

        [Fact]
        public void An_in_memory_database_is_left_alone_and_nothing_throws()
        {
            // There is no file, so there is no single-writer file hazard and nothing to set. The suites that use
            // Data Source=:memory: must not start paying for a pragma - or an exception - they do not need.
            Stomme.Database.Sqlite.SqliteDatabase.EnsureWalJournal("Data Source=:memory:");
            Stomme.Database.Sqlite.SqliteDatabase.EnsureWalJournal("");
            Stomme.Database.Sqlite.SqliteDatabase.EnsureWalJournal("Host=192.0.2.1;Database=not-sqlite");

            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            _output.WriteLine($"[memory] journal_mode={JournalMode(connection)}, no exception");
        }

        internal static string JournalMode(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return Convert.ToString(command.ExecuteScalar())?.ToLowerInvariant() ?? string.Empty;
        }

        public void Dispose()
        {
            ScratchSqlite.ReleaseInstall(_directory);
            try { Directory.Delete(_directory, true); } catch { /* best effort */ }
        }
    }
}
