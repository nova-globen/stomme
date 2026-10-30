using System.Collections.Concurrent;
using System.Net.Sockets;
using Npgsql;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// PostgreSQL test-endpoint resolution for the conformance fixtures — a local equivalent of
/// <c>Stomme.Tests.PostgresTestEndpoint</c> (this project cannot reference that one), following the same
/// convention: env-overridable via <c>POSTGRES_TEST_HOST</c>/<c>POSTGRES_TEST_PORT</c>/<c>POSTGRES_TEST_USER</c>/
/// <c>POSTGRES_TEST_PASSWORD</c>, defaulting to this machine's throwaway PostgreSQL that
/// <c>scripts/postgres-test-server.ps1</c> makes on <c>127.0.0.1:15433</c>, its password read from the file that script
/// writes under the user's temp folder (the shared test servers are gone, 2026-10-29). Set <c>POSTGRES_TEST_*</c> to point
/// the suites at another server (a CI job's service container).
/// </summary>
/// <remarks>
/// <para>
/// The server outlives a run (the gate removes it after a full run, a developer may keep it), so scratch-database hygiene is
/// a mechanism, not a politeness:
/// every fixture database is created under a fixture-owned <c>stomme_conformance_*</c> prefix, dropped in
/// teardown with <c>DROP DATABASE ... WITH (FORCE)</c>, and — because a KILLED run never reaches teardown —
/// each fixture sweeps its own prefix's stale leftovers at initialization, the
/// <c>RavenTestDatabaseJanitor</c> pattern from Stomme.Tests. Prefixes are per-fixture-class because xUnit
/// runs different test classes in parallel: a class must never sweep a prefix another live class is writing
/// under. Like the Raven janitor, the sweep assumes one test process at a time per server.
/// </para>
/// <para>
/// The account must be able to <c>CREATE DATABASE</c>; the <c>postgres</c> superuser of the test server satisfies this.
/// </para>
/// </remarks>
internal static class PostgresConformanceEndpoint
{
    private static readonly ConcurrentDictionary<string, bool> SweptPrefixes = new();

    public static string Host =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_TEST_PORT"), out var port) ? port : 15433;

    public static string User =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_USER") is { Length: > 0 } user ? user : "postgres";

    // No password literal is kept in the source: POSTGRES_TEST_PASSWORD, else the local server's password file, else a
    // failure that says how to get one. Mirrors Stomme.Tests.PostgresTestEndpoint.
    public static string Password
    {
        get
        {
            if (Environment.GetEnvironmentVariable("POSTGRES_TEST_PASSWORD") is { Length: > 0 } given)
            {
                return given;
            }

            var file = Path.Combine(Path.GetTempPath(), "stomme-postgres-test", "password");
            return System.IO.File.Exists(file)
                ? System.IO.File.ReadAllText(file).Trim()
                : throw new InvalidOperationException($"No PostgreSQL test server: run 'pwsh scripts/postgres-test-server.ps1' (a throwaway PostgreSQL on 127.0.0.1:15433, its password in {file}) or set POSTGRES_TEST_HOST, POSTGRES_TEST_PORT, POSTGRES_TEST_USER and POSTGRES_TEST_PASSWORD.");
        }
    }

    public static string ConnectionString(string database) =>
        $"Server={Host};Port={Port};Database={database};User Id={User};Password={Password};";

    /// <summary>A TCP-reachability probe. An open port is not proof it is PostgreSQL; it does not need to be.</summary>
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
    /// Creates a scratch database. NOT best-effort: a failure here must fail the fixture loudly — an
    /// unreachable PostgreSQL means the PG half of the suite cannot run, which is a FAILURE, never
    /// a skip (see <see cref="PostgresParityCoverageTests"/> for the named diagnosis).
    /// </summary>
    public static async Task CreateDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {Quote(database)}";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Best-effort force-drop of a scratch database (connects to the maintenance <c>postgres</c> database —
    /// you cannot drop the one you are connected to). <c>WITH (FORCE)</c> terminates lingering sessions,
    /// including this process's own pooled connections, whose client-side pool is cleared first so no dead
    /// physical connection lingers. A failed drop never fails a test; the init-time sweep is the backstop.
    /// </summary>
    public static async Task DropDatabaseAsync(string database)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return;
        }

        try
        {
            await using (var doomed = new NpgsqlConnection(ConnectionString(database)))
            {
                NpgsqlConnection.ClearPool(doomed);
            }

            await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {Quote(database)} WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // best-effort cleanup — the next run's SweepStaleAsync gets another chance
        }
    }

    /// <summary>
    /// Drops every database whose name starts with <paramref name="prefix"/> — leftovers of a killed prior
    /// run. Swept once per prefix per process; a sweep failure never fails the run (leftovers are a shared-
    /// instance hygiene problem, not a correctness problem, and the next run gets another chance). Returns
    /// the number of databases dropped.
    /// </summary>
    public static async Task<int> SweepStaleAsync(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (!SweptPrefixes.TryAdd(prefix, true))
        {
            return 0;
        }

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
            await connection.OpenAsync();

            var stale = new List<string>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT datname FROM pg_database WHERE datname LIKE @prefix || '%'";
                query.Parameters.AddWithValue("prefix", prefix);
                await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    stale.Add(reader.GetString(0));
                }
            }

            foreach (var database in stale)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS {Quote(database)} WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
            }

            return stale.Count;
        }
        catch
        {
            return 0;
        }
    }

    // The names here are fixture-generated (prefix + Guid "N"), so this is defence in depth, not an
    // injection surface.
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// The honesty guard for the PostgreSQL half — the same role <see cref="RavenParityCoverageTests"/>
/// plays for the RavenDB half: with PostgreSQL unreachable the two PG fixtures already FAIL loudly out of
/// <c>InitializeAsync</c> (they do not skip), but each failure is a bare socket/Npgsql stack that does not
/// say what to start. This converts the pile into one named diagnosis.
/// </summary>
/// <remarks>
/// <b>Deliberately not a skip.</b> Both EF fixtures in this suite are SQLite, and the stated limit of the SQLite fixtures is
/// that the suite therefore structurally cannot see PostgreSQL's answers — measured, not hypothetical:
/// SQLite's <c>LIKE</c> is case-INSENSITIVE for ASCII while PostgreSQL's is case-SENSITIVE, so
/// <c>A_prefix_filter_is_expressible_on_every_provider</c> answered <c>false</c> on the EF half while the
/// live product on PostgreSQL answers <c>true</c>. The PG fixtures exist to put that answer IN the suite;
/// a run without them proves nothing about it, so PG untested must read as failure (the fail-not-skip principle).
/// </remarks>
public sealed class PostgresParityCoverageTests
{
    [Fact]
    public void Provider_conformance_is_covered_by_a_live_postgresql_server() =>
        Assert.True(PostgresConformanceEndpoint.IsReachable(),
            $"PostgreSQL is not reachable at {PostgresConformanceEndpoint.Host}:{PostgresConformanceEndpoint.Port}, " +
            $"so the PostgreSQL half of this conformance suite cannot run — and both EF fixtures are SQLite, " +
            $"which structurally cannot represent PostgreSQL's answers (PG's LIKE is case-SENSITIVE where " +
            $"SQLite's is not, the divergence this suite exists to represent). This is a FAILURE and not a skip on " +
            $"purpose. Start the local test PostgreSQL ('pwsh scripts/postgres-test-server.ps1') " +
            $"or point POSTGRES_TEST_HOST/PORT/USER/PASSWORD at another PostgreSQL.");
}
