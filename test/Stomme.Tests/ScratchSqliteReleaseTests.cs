#nullable enable
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What <see cref="ScratchSqlite.Release"/> actually promises, measured in both directions (BL-104).
    /// </summary>
    /// <remarks>
    /// The point of the row is that a targeted release is enough, so "targeted is enough" has to be MEASURED
    /// rather than asserted: a pooled connection really does hold the file, the targeted clear really does let
    /// it go, and a clear aimed at a DIFFERENT connection string really does leave it held. That last one is the
    /// reason every converted fixture passes the connection string it actually used.
    /// </remarks>
    public class ScratchSqliteReleaseTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _directory;

        public ScratchSqliteReleaseTests(ITestOutputHelper output)
        {
            _output = output;
            _directory = Path.Combine(Path.GetTempPath(), "stomme-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [Fact]
        public void A_pooled_connection_holds_the_file_until_its_own_pool_is_released()
        {
            var path = Path.Combine(_directory, "held.db");
            var connectionString = ScratchSqlite.ConnectionString(path);

            Touch(connectionString);

            // Direction 1: the pooled connection is CLOSED but not gone, and it still owns the handle.
            Assert.False(CanDelete(path), "a pooled SQLite connection was expected to still hold the file");
            _output.WriteLine("[held] closed-but-pooled connection holds the file, as expected");

            // Direction 2: the targeted release lets it go - no ClearAllPools needed.
            ScratchSqlite.Release(connectionString);
            Assert.True(CanDelete(path), "ScratchSqlite.Release did not release this database's pool");
            _output.WriteLine("[released] the targeted ClearPool freed the file");
        }

        [Fact]
        public void Releasing_one_database_leaves_another_ones_pool_alone()
        {
            // The whole reason for the row: teardown in one test class must not reach into a class that is still
            // running. ClearAllPools does exactly that; this does not.
            var mine = ScratchSqlite.ConnectionString(Path.Combine(_directory, "mine.db"));
            var theirs = ScratchSqlite.ConnectionString(Path.Combine(_directory, "theirs.db"));

            Touch(mine);
            Touch(theirs);

            ScratchSqlite.Release(mine);

            Assert.True(CanDelete(Path.Combine(_directory, "mine.db")), "my own pool should have been released");
            Assert.False(CanDelete(Path.Combine(_directory, "theirs.db")),
                "releasing one database must not release another's pool - that is the process-wide behaviour " +
                "this replaces");

            _output.WriteLine("[isolation] mine released, theirs untouched");

            ScratchSqlite.Release(theirs);
        }

        [Fact]
        public void A_release_aimed_at_a_different_connection_string_releases_nothing()
        {
            // Why every converted fixture passes the string it actually used, rather than rebuilding one that
            // looks the same. The pool is keyed by the connection string; a near-miss addresses another pool,
            // frees nothing, and reports nothing - and the file delete that follows fails into a catch.
            var path = Path.Combine(_directory, "keyed.db");
            var real = ScratchSqlite.ConnectionString(path);
            var nearMiss = $"Data Source={path}";     // the same database, a DIFFERENT pool key

            Touch(real);

            ScratchSqlite.Release(nearMiss);
            Assert.False(CanDelete(path),
                "a clear aimed at a different connection string was expected to release nothing");
            _output.WriteLine($"[near miss] '{nearMiss}' did not release the pool for '{real}'");

            ScratchSqlite.Release(real);
            Assert.True(CanDelete(path), "the fixture's own connection string should release its own pool");
        }

        [Theory]
        [InlineData("Data Source={0}", "Data Source={0};")]           // a trailing semicolon
        [InlineData("Data Source={0}", "data source={0}")]            // keyword casing
        [InlineData("Data Source={0}", "Data Source = {0}")]          // spacing round the equals
        public void The_pool_key_is_the_raw_string_so_even_cosmetic_differences_miss(
            string writtenFormat, string releasedFormat)
        {
            // MEASURED, 2026-09-20, and sharper than expected: the pool is keyed by the connection string as
            // WRITTEN, not by its parsed meaning. A trailing semicolon, a lower-cased keyword and a space either
            // side of the equals each address a DIFFERENT pool and release nothing - silently, because ClearPool
            // on an unknown key is not an error.
            //
            // This is why nothing in this repository rebuilds a connection string at teardown. A fixture passes
            // the string it used; a scratch install reads the post-install settings files back (ReleaseInstall);
            // the OpenIddict provider asks the PRODUCT to derive the sibling store's string rather than
            // spelling out the "_OpenIddict" suffix itself. Each of those exists because of this test.
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db");
            var written = string.Format(writtenFormat, path);
            var released = string.Format(releasedFormat, path);

            Touch(written);
            ScratchSqlite.Release(released);

            _output.WriteLine($"[spelling] wrote '{writtenFormat}' / released '{releasedFormat}' -> still held");
            Assert.False(CanDelete(path),
                $"'{releasedFormat}' released the pool opened as '{writtenFormat}'. Microsoft.Data.Sqlite has " +
                "started normalising the pool key. That is GOOD news, but the comments and helpers that assume " +
                "byte-exact matching are now over-strict - re-measure before relying on the new behaviour.");

            // And the exact string still works, so the miss above is about the spelling, not about the pool.
            ScratchSqlite.Release(written);
            Assert.True(CanDelete(path), "the exact connection string should release its own pool");
        }

        [Fact]
        public void ReleaseInstall_frees_every_database_under_the_content_root_however_it_was_spelled()
        {
            // The shape of a scratch install, and the shape of the defect this caught. ReleaseInstall used to
            // release the store named in the settings files plus a hard-coded list of sidecars it "knew" about -
            // and the A/B measurement showed the scratch trees surviving anyway, because an installed host opens
            // MORE SQLite files than either the settings or that list mention. Two things were missed: the
            // shared module-aggregate sidecar (nobody's list had it), and the tenant store's trailing-semicolon
            // spelling (a different pool key entirely).
            //
            // So the content root is built here the way a real one ends up: a settings file naming the tenant
            // store WITH options, a sidecar under Data/ opened with the bare spelling, and a second store opened
            // with a trailing semicolon. If ReleaseInstall misses any one of them, the tree cannot be deleted -
            // which is exactly how the regression showed itself.
            var contentRoot = Path.Combine(_directory, "install");
            Directory.CreateDirectory(Path.Combine(contentRoot, "Data"));

            var tenant = Path.Combine(contentRoot, "tenant.db");
            var tenantConnectionString = ScratchSqlite.ConnectionString(tenant);   // carries Default Timeout
            var sidecar = Path.Combine(contentRoot, "Data", "Stomme.ModuleAggregates.db");
            var semicoloned = Path.Combine(contentRoot, "Data", "Stomme.Features.db");

            File.WriteAllText(
                Path.Combine(contentRoot, "appsettings.override.json"),
                "{\"ConnectionStrings\":{\"DefaultConnection\":" +
                System.Text.Json.JsonSerializer.Serialize(tenantConnectionString) + "}}");

            Touch(tenantConnectionString);
            Touch($"Data Source={sidecar}");        // the |DataDirectory| spelling: no trailing semicolon
            Touch($"Data Source={semicoloned};");   // the other convention in this repository

            // Direction 1, checked NON-destructively on one file. Probing with a tree delete here would be the
            // wrong instrument and cost an hour to see: Directory.Delete(recursive) removes what it can before
            // it throws, which takes appsettings.override.json with it - so the release that followed would find
            // no settings to read and miss the tenant store, failing for a reason the helper does not have. The
            // real call order is release first, delete second.
            Assert.False(CanDelete(tenant), "the pooled connection was expected to still hold the tenant store");

            ScratchSqlite.ReleaseInstall(contentRoot);

            Assert.True(CanDeleteTree(contentRoot),
                "ReleaseInstall left a database held. It must cover the stores named in the POST-install settings " +
                "files AND every .db under the content root, in both the bare and trailing-semicolon spellings - " +
                "the pool is keyed by the raw string, so a spelling it does not try is a silent miss.");

            _output.WriteLine("[install] settings-named store, bare sidecar and semicoloned store all released");
        }

        [Fact]
        public void Release_is_silent_on_input_it_cannot_use()
        {
            // Suites that also run against PostgreSQL hand it whatever connection string they happen to hold.
            ScratchSqlite.Release(null!);
            ScratchSqlite.Release("");
            ScratchSqlite.Release("   ");
            ScratchSqlite.Release("Host=192.0.2.1;Database=not-sqlite;Username=u;Password=p");

            _output.WriteLine("[tolerant] empty and non-SQLite connection strings are no-ops, not throws");
        }

        /// <summary>Opens and closes a pooled connection, so the pool holds one for this database.</summary>
        private static void Touch(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS t (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        /// <summary>Whether the whole tree can go — which a single held database file is enough to prevent.</summary>
        private static bool CanDeleteTree(string directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool CanDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch { /* best effort */ }
        }
    }
}
