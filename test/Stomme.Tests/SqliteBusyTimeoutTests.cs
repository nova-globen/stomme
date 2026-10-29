#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stomme.Database.Sqlite;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The SQLite busy timeout is explicit and configurable: a command waits <see cref="SqliteDatabase.DefaultBusyTimeoutSeconds"/>
    /// on a file another connection has locked, or the connection string's own <c>Default Timeout</c>.
    /// </summary>
    /// <remarks>
    /// Measured against a REAL lock: one connection holds the write lock (<c>BEGIN IMMEDIATE</c>) while the raw ADO path and
    /// the EF path try to write. A short configured timeout gives up in about that time; the default outlasts a lock held
    /// for a few seconds and the write then succeeds.
    /// </remarks>
    public class SqliteBusyTimeoutTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stomme-busy-" + Guid.NewGuid().ToString("N"));
        private readonly string _file;

        public SqliteBusyTimeoutTests()
        {
            Directory.CreateDirectory(_directory);
            _file = Path.Combine(_directory, "busy.db");
            using var setup = new SqliteConnection($"Data Source={_file}");
            setup.Open();
            using var create = setup.CreateCommand();
            create.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE item (id INTEGER PRIMARY KEY, name TEXT);";
            create.ExecuteNonQuery();
        }

        [Theory]
        [InlineData("Data Source=x.db;", SqliteDatabase.DefaultBusyTimeoutSeconds)]
        [InlineData("Data Source=x.db;Default Timeout=5;", 5)]
        [InlineData("Data Source=x.db;Command Timeout=7;", 7)]
        [InlineData("Data Source=x.db;default timeout=45", 45)]
        [InlineData("not a connection string", SqliteDatabase.DefaultBusyTimeoutSeconds)]
        public void The_timeout_is_the_connection_strings_or_the_default(string connectionString, int expected)
        {
            Assert.Equal(30, SqliteDatabase.DefaultBusyTimeoutSeconds);
            Assert.Equal(expected, SqliteDatabase.BusyTimeoutSeconds(connectionString));
        }

        [Fact]
        public async Task A_short_configured_timeout_gives_up_on_a_locked_file_in_about_that_time()
        {
            var connectionString = $"Data Source={_file};Default Timeout=2;";
            using var holder = HoldWriteLock();

            // The raw path swallows the error and answers -1 (its contract); the EF path throws.
            var raw = Stopwatch.StartNew();
            var rows = await new SqliteDatabase().ExecuteNonQueryAsync(connectionString, "INSERT INTO item (name) VALUES ('raw');");
            raw.Stop();
            Assert.Equal(-1, rows);
            AssertAbout(2, raw.Elapsed, "raw");

            var ef = Stopwatch.StartNew();
            await using var context = NewContext(connectionString);
            var error = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync("INSERT INTO item (name) VALUES ('ef');"));
            ef.Stop();
            Assert.Equal(5, error.SqliteErrorCode); // SQLITE_BUSY
            AssertAbout(2, ef.Elapsed, "EF");
        }

        [Fact]
        public async Task The_default_timeout_outlasts_a_lock_held_for_a_few_seconds()
        {
            var connectionString = $"Data Source={_file};";
            var holder = HoldWriteLock();
            var release = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => holder.Dispose());

            var waited = Stopwatch.StartNew();
            await using (var context = NewContext(connectionString))
            {
                Assert.Equal(1, await context.Database.ExecuteSqlRawAsync("INSERT INTO item (name) VALUES ('ef');"));
            }
            waited.Stop();
            await release;

            // It waited for the lock rather than failing at once - and succeeded when the lock went.
            Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(2.5), $"the write did not wait for the lock: {waited.Elapsed}");
            Assert.Equal(1, await new SqliteDatabase().ExecuteNonQueryAsync(connectionString, "INSERT INTO item (name) VALUES ('raw');"));
        }

        private SqliteConnection HoldWriteLock()
        {
            var holder = new SqliteConnection($"Data Source={_file};Pooling=False");
            holder.Open();
            using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE; INSERT INTO item (name) VALUES ('holder');";
            begin.ExecuteNonQuery();
            return holder; // disposing it rolls the transaction back and releases the lock
        }

        private static DbContext NewContext(string connectionString) =>
            new(new SqliteDatabase().UseDatabase(new DbContextOptionsBuilder(), connectionString).Options);

        private static void AssertAbout(int seconds, TimeSpan elapsed, string path)
        {
            // Not instantly (the wait happened) and not the 30 s default (the configured value was used).
            Assert.True(elapsed >= TimeSpan.FromSeconds(seconds - 0.5), $"{path}: gave up after {elapsed}, before the {seconds} s timeout");
            Assert.True(elapsed < TimeSpan.FromSeconds(seconds + 10), $"{path}: waited {elapsed}, far past the {seconds} s timeout");
        }

        public void Dispose()
        {
            ScratchSqlite.Release($"Data Source={_file};Default Timeout=2;");
            ScratchSqlite.Release($"Data Source={_file};");
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
