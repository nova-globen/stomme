using System.Net.Sockets;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The honesty guard for this suite: fails when the RavenDB half cannot run, so a parity suite that is
/// only testing ONE provider reads as FAILURE rather than success.
/// </summary>
/// <remarks>
/// <b>Why this exists even though the fixtures already fail.</b> Measured, not assumed: with RavenDB
/// unreachable this project reports <c>Failed: 48, Passed: 45</c> — the Raven fixtures throw out of
/// <c>InitializeAsync</c>, they do not skip. So the suite is already structurally honest, and the csproj's
/// former claim that the Raven half "is skipped when it is unreachable" was simply wrong.
/// <para>
/// What those 48 failures are NOT is actionable: each one is a bare <c>SocketException</c> stack, and none
/// of them says which container to start. This test converts that pile into one named diagnosis. It is the
/// same role <c>Deadlock_class_is_covered_by_a_real_async_provider</c> plays for the render guard, with one
/// difference worth stating: there, an absent container silently DROPPED the PostgreSQL case and the suite
/// went green, so the assertion was the only thing standing between a gap and a false pass. Here it is a
/// diagnostic, not the last line of defence — the parity failure is already loud.
/// </para>
/// <para>
/// <b>Deliberately not a skip.</b> An EF-only run of a provider-parity suite certifies nothing: its entire
/// purpose is the comparison. D45 is the evidence — a harvested <c>siteId == -1</c> sentinel query that EF
/// folds at translation time and RavenDB's LINQ provider does not, measured at 0 of 2 rows on RavenDB while
/// green on EF. Only the Raven half could see that. Parity untested must read as failure.
/// </para>
/// </remarks>
public sealed class RavenParityCoverageTests
{
    [Fact]
    public async Task Provider_parity_is_covered_by_a_live_ravendb_server()
    {
        var url = RavenTestServer.Url;
        var reachable = await RavenTestServer.IsReachableAsync();

        Assert.True(reachable,
            $"RavenDB is not reachable at {url}, so the RavenDB half of this provider-parity suite cannot " +
            $"run — and a conformance suite that exercises only EF Core proves nothing about parity, which " +
            $"is its entire purpose. This is a FAILURE and not a skip on purpose: D45 caught a `siteId == -1` " +
            $"sentinel query returning 0 of 2 rows on RavenDB while passing on EF, and only the Raven half " +
            $"could see it. Start the local RavenDB instance (default http://127.0.0.1:7000) or point " +
            $"RAVENDB_TEST_URL at a reachable server.");
    }
}

/// <summary>
/// The address of the live RavenDB server the Raven fixtures bind to, and a cheap reachability probe.
/// </summary>
internal static class RavenTestServer
{
    /// <summary>
    /// The repo's local RavenDB install listens on 7000 (both test stores are now local Windows services:
    /// RavenDB here, and the PostgreSQL render-guard service postgresql-x64-18 on 127.0.0.1:5432 — Docker is
    /// gone). Override with RAVENDB_TEST_URL to point at another server.
    /// </summary>
    private const string DefaultUrl = "http://127.0.0.1:7000";

    internal static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? DefaultUrl;

    /// <summary>
    /// A TCP connect against the configured URL's host/port — the same probe shape the render guard's
    /// <c>PostgresProvider.IsReachable</c> uses. An open port is not proof the server is RavenDB; it does
    /// not need to be. A wrong-but-open port still fails the fixtures loudly, so this probe only has to
    /// answer the question the diagnosis needs: "is anything listening where the tests are pointed?"
    /// </summary>
    internal static async Task<bool> IsReachableAsync()
    {
        try
        {
            var uri = new Uri(Url);
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}
