using System.Collections.Concurrent;
using System.Net.Sockets;
using Npgsql;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// PostgreSQL test-endpoint resolution for the conformance fixtures — a local equivalent of
/// <c>Stomme.Tests.PostgresTestEndpoint</c> (this project cannot reference that one), following the same
/// convention: env-overridable via <c>POSTGRES_TEST_HOST</c>/<c>POSTGRES_TEST_PORT</c>/<c>POSTGRES_TEST_USER</c>/
/// <c>POSTGRES_TEST_PASSWORD</c>, defaulting to the shared INFRA TIER UAT instance (<c>192.168.10.20:5433</c>)
/// with the password from <c>DEFAULT_PASSWORD</c>. Set <c>POSTGRES_TEST_*</c> to point the suites at your own server.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is SHARED infrastructure</b>, so scratch-database hygiene is a mechanism, not a politeness:
/// every fixture database is created under a fixture-owned <c>stomme_conformance_*</c> prefix, dropped in
/// teardown with <c>DROP DATABASE ... WITH (FORCE)</c>, and — because a KILLED run never reaches teardown —
/// each fixture sweeps its own prefix's stale leftovers at initialization, the
/// <c>RavenTestDatabaseJanitor</c> pattern from Stomme.Tests. Prefixes are per-fixture-class because xUnit
/// runs different test classes in parallel: a class must never sweep a prefix another live class is writing
/// under. Like the Raven janitor, the sweep assumes one test process at a time per server.
/// </para>
/// <para>
/// The account must be able to <c>CREATE DATABASE</c>; the infra <c>postgres</c> superuser satisfies this.
/// </para>
/// </remarks>
internal static class PostgresConformanceEndpoint
{
    private static readonly ConcurrentDictionary<string, bool> SweptPrefixes = new();

    public static string Host =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_HOST") ?? "localhost";

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_TEST_PORT"), out var port) ? port : 5433;

    public static string User =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_USER") ?? "postgres";

    // No password literal (2026-10-28, the public-release review): without POSTGRES_TEST_PASSWORD or DEFAULT_PASSWORD
    // the server refuses the sign-in (28P01), which names the cause. Mirrors Stomme.Tests.PostgresTestEndpoint.
    public static string Password =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_PASSWORD")
        ?? Environment.GetEnvironmentVariable("DEFAULT_PASSWORD")
        ?? string.Empty;

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
    /// unreachable PostgreSQL means the PG half of the suite cannot run, which per D46 is a FAILURE, never
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
/// <b>Deliberately not a skip.</b> Both EF fixtures in this suite are SQLite, and D46's own stated limit is
/// that the suite therefore structurally cannot see PostgreSQL's answers — measured, not hypothetical:
/// SQLite's <c>LIKE</c> is case-INSENSITIVE for ASCII while PostgreSQL's is case-SENSITIVE, so
/// <c>A_prefix_filter_is_expressible_on_every_provider</c> answered <c>false</c> on the EF half while the
/// live product on PostgreSQL answers <c>true</c>. The PG fixtures exist to put that answer IN the suite;
/// a run without them proves nothing about it, so PG untested must read as failure (the D46 principle).
/// </remarks>
public sealed class PostgresParityCoverageTests
{
    [Fact]
    public void Provider_conformance_is_covered_by_a_live_postgresql_server() =>
        Assert.True(PostgresConformanceEndpoint.IsReachable(),
            $"PostgreSQL is not reachable at {PostgresConformanceEndpoint.Host}:{PostgresConformanceEndpoint.Port}, " +
            $"so the PostgreSQL half of this conformance suite cannot run — and both EF fixtures are SQLite, " +
            $"which structurally cannot represent PostgreSQL's answers (PG's LIKE is case-SENSITIVE where " +
            $"SQLite's is not, the divergence BL-16 exists to represent). This is a FAILURE and not a skip on " +
            $"purpose, per D46. Make the shared infra UAT instance reachable (192.168.10.20:5433, password in " +
            $"DEFAULT_PASSWORD) or point POSTGRES_TEST_HOST/PORT/USER/PASSWORD at another PostgreSQL. " +
            $"Set POSTGRES_TEST_HOST/PORT/USER/PASSWORD to a reachable PostgreSQL.");
}
