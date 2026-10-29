using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Stomme.Tests
{
    /// <summary>
    /// Connection strings for the FILE-BACKED scratch SQLite databases the host-installing suites use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file-backed SQLite database admits ONE writer, and a composed Stomme host writes from two places at once:
    /// the request being driven, and the D34 log drain flushing from a background thread. When they collide the loser
    /// gets <c>SQLite Error 5: 'database is locked'</c>. Measured on 2026-09-17: the definition-PUT suites failed this
    /// way in roughly one full run in three, a different case each time, and always passed when run alone; the
    /// captured server error was an <c>EntityFrameworkCore.Update</c> failure on <c>MasterDBContext</c> whose inner
    /// exception was exactly that lock.
    /// </para>
    /// <para>
    /// <b>CORRECTION 2026-09-20.</b> This used to read "two settings, and BOTH are needed", crediting
    /// <c>Default Timeout</c> with supplying a busy wait. Measured: <c>SqliteConnectionStringBuilder.DefaultTimeout</c>
    /// is <b>already 30 seconds</b> with nothing set (<c>SqliteWalJournalTests</c>), so the explicit setting below
    /// restates the default and changes nothing. That explains the original measurement rather than contradicting it —
    /// "a busy timeout alone still failed one run in three" was a run with no effective change at all. <b>WAL is the
    /// setting that does the work</b>, because a reader no longer blocks a writer that has already begun its
    /// transaction. The explicit timeout is kept only because it states the intent at the call site. The journal mode
    /// is a property of the FILE, so it is set once, here, when the connection string is handed out.
    /// </para>
    /// <para>
    /// This no longer stands in for the product, either. BL-104's product half put the same journal mode behind
    /// <c>SqliteDatabase.EnsureWalJournal</c>, on the <c>IDatabase.UseDatabase</c> seam every framework context on a
    /// SQLite install is composed through, so a real install gets it too. This helper still applies it to scratch
    /// databases that never go through that seam.
    /// </para>
    /// </remarks>
    internal static class ScratchSqlite
    {
        /// <summary>Seconds a writer waits for the write lock before failing. Long enough for a log-drain flush.</summary>
        public const int BusyTimeoutSeconds = 30;

        /// <summary>
        /// A connection string for <paramref name="path"/> that waits for the write lock, on a database put into WAL
        /// mode. Creates the (empty) database file if it does not exist, which no installer minds: "installed" is
        /// decided by the master tables, never by the file existing.
        /// </summary>
        public static string ConnectionString(string path)
        {
            var connectionString = $"Data Source={path};Default Timeout={BusyTimeoutSeconds};";
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={BusyTimeoutSeconds * 1000};";
                pragma.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Best effort: a database that cannot be opened here will fail loudly where it is actually used.
            }

            return connectionString;
        }

        /// <summary>A connection string for <paramref name="fileName"/> inside <paramref name="directory"/>.</summary>
        public static string ConnectionString(string directory, string fileName) =>
            ConnectionString(Path.Combine(directory, fileName));

        /// <summary>
        /// Releases the pooled connections of ONE scratch database so its file can be deleted, and leaves every
        /// other suite's pool alone. The teardown counterpart of <see cref="ConnectionString(string)"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This exists because <c>SqliteConnection.ClearAllPools()</c> is PROCESS-WIDE</b>. About
        /// thirty fixtures used to call it at teardown, and test classes in this assembly run in parallel
        /// collections, so one class finishing tore the pooled connections out from under whatever else was
        /// mid-request. Measured: a probe calling <c>ClearAllPools</c> every 25 ms while a definition PUT ran
        /// produced 3 non-200 in 60 — a flake with no bug behind it and nothing in the failure naming its cause.
        /// </para>
        /// <para>
        /// <b><c>Pooling=False</c> is NOT the alternative — it is worse, and that was measured too.</b> Unpooled,
        /// every operation opens a NEW connection, which multiplies the writers competing for SQLite's single
        /// write lock: 2 failures in 4 runs unpooled against 0 in 5 pooled, same binary, same machine, same
        /// minute.
        /// </para>
        /// <para>
        /// <b>Pass the connection string the fixture actually used.</b> The pool is keyed by the connection
        /// string, so a reconstructed one that differs from the real one — a missing <c>Default Timeout</c>, a
        /// different path spelling — addresses a DIFFERENT pool, releases nothing, and says nothing about it.
        /// The file delete that follows then fails into a <c>catch</c> and the teardown looks like it worked.
        /// <c>ScratchSqliteReleaseTests</c> pins both directions of that.
        /// </para>
        /// </remarks>
        public static void Release(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            try
            {
                using var connection = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(connection);
            }
            catch (ArgumentException)
            {
                // Not a SQLite connection string (a suite that also runs on PostgreSQL passes whatever it has).
            }
            catch (SqliteException)
            {
                // Best effort, exactly like the scratch-file delete that follows it.
            }
        }

        /// <summary>
        /// Releases every SQLite pool a scratch INSTALL opened, by reading the connection strings back out of the
        /// settings files in <paramref name="contentRoot"/>. Call it before deleting the content root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A fixture that composes a real host cannot simply release the string it wrote, because <b>the app
        /// MUTATES its settings at boot</b> (D32/D-4): <c>DefaultDBType</c>, the connection string and an
        /// <c>InstallationId</c> are written back to the writable settings file, which for these fixtures is
        /// <c>appsettings.override.json</c> under the content root (<c>ConfigManager.GetAppSettingsOverrideFilename</c>).
        /// The host therefore pools under whatever ended up THERE. Since the pool is keyed by the connection
        /// string, releasing the pre-install spelling can miss — so this reads the post-install files and releases
        /// what it finds, which also covers a second connection string such as
        /// <c>ConnectionStrings:ModuleAggregates</c> without this having to know the fixture's shape.
        /// </para>
        /// <para>
        /// Everything here is best effort: a value that is not a SQLite connection string is a no-op, and the
        /// content-root delete that follows was already best effort.
        /// </para>
        /// </remarks>
        public static void ReleaseInstall(string contentRoot)
        {
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                return;
            }

            foreach (var fileName in new[] { "appsettings.override.json", "appsettings.json" })
            {
                var path = Path.Combine(contentRoot, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (var connectionString in ConnectionStringsIn(path))
                {
                    Release(connectionString);
                }
            }

            foreach (var sidecar in SidecarDatabasesUnder(contentRoot))
            {
                Release(sidecar);
            }
        }

        /// <summary>
        /// Every SQLite sidecar under the content root, found by LOOKING rather than by knowing their names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An installed host opens more SQLite files than its settings name, and hard-coding the list is how
        /// you miss one.</b> The render guard's comment said there were two — the tenant store and
        /// FeatureManagement's <c>Stomme.Features.db</c>. A first version of this helper believed that comment
        /// and released exactly those two; the A/B measurement that followed showed the scratch directories
        /// surviving anyway, and the file still held was a THIRD sidecar neither the comment nor this helper knew
        /// about — <c>Stomme.ModuleAggregates.db</c>, the shared reflective module-aggregate store.
        /// </para>
        /// <para>
        /// So this enumerates instead. Both sidecars compose their connection string by expanding
        /// <c>|DataDirectory|</c> into <c>{ContentRoot}/Data</c> plus a separator
        /// (<c>ModuleAggregatePersistenceExtensions.ExpandDataDirectory</c>, and FeatureManagement's
        /// <c>ServerStartup.ResolveConnectionString</c>), which is exactly <c>Data Source={full path of the
        /// file}</c> — so the file on disk yields the pool key, and a sidecar added by a future module is covered
        /// with no edit here. That matters because the pool is keyed by the RAW string
        /// (<c>The_pool_key_is_the_raw_string_so_even_cosmetic_differences_miss</c>), so a name this helper did
        /// not know would be missed in silence.
        /// </para>
        /// <para>
        /// A store whose connection string carries extra options — the tenant store's <c>Default Timeout</c>, say —
        /// is released by the settings-file pass instead, which has the string verbatim. Releasing a pool that was
        /// never opened is a no-op, so the two passes overlapping costs nothing.
        /// </para>
        /// </remarks>
        private static string[] SidecarDatabasesUnder(string contentRoot)
        {
            try
            {
                return Directory
                    .EnumerateFiles(contentRoot, "*.db", SearchOption.AllDirectories)
                    // BOTH spellings, because the pool key is the raw string and this repository uses two
                    // conventions: the |DataDirectory| sidecars expand to no trailing semicolon, while the tenant
                    // store (and the _OpenIddict sibling derived from it) carry one. Measured - releasing only
                    // the bare form left tenant.db and tenant_OpenIddict.db held and their scratch trees behind.
                    // A pool that was never opened under a given spelling is a no-op, so guessing wide is free.
                    .SelectMany(path => new[] { $"Data Source={path}", $"Data Source={path};" })
                    .ToArray();
            }
            catch (Exception)
            {
                // The content root may already be gone, or unreadable; the delete that follows is best effort too.
                return Array.Empty<string>();
            }
        }

        /// <summary>Every value under the <c>ConnectionStrings</c> object of a settings file.</summary>
        private static string[] ConnectionStringsIn(string settingsFile)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(settingsFile))?.AsObject();
                if (root?["ConnectionStrings"] is not JsonObject connectionStrings)
                {
                    return Array.Empty<string>();
                }

                return connectionStrings
                    .Select(entry => entry.Value?.GetValue<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();
            }
            catch (Exception)
            {
                // An unreadable or half-written settings file is not worth failing a teardown over.
                return Array.Empty<string>();
            }
        }
    }
}