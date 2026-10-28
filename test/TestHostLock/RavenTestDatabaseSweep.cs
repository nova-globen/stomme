// Compiled into every test project by the root Directory.Build.targets, beside the machine-wide test-host throttle.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stomme.TestInfrastructure
{
    /// <summary>
    /// Deletes the test databases earlier runs left on THIS machine's RavenDB, when a test host starts. Measured
    /// 2026-10-02: a developer machine's local RavenDB held 217 of them (6.4 GB of <c>stomme_authz_gate_*</c> and their
    /// <c>_OpenIddict</c> siblings, plus four others) - every killed run, every teardown that threw into its swallowing
    /// catch, and every sibling database a teardown never named stayed for good, and the server's memory grew with them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which server.</b> The one the suites default to - <c>RAVENDB_TEST_URL</c>, else <c>http://127.0.0.1:7000</c> -
    /// and ONLY when it is a loopback address. That is what makes the sweep safe without an age rule: it runs once the
    /// test host HOLDS the machine-wide lock, so no other test host on this machine is live, so every test-shaped database
    /// on this machine's own server is a leftover. A server other machines share (the infra UAT RavenDB) is never swept
    /// here: a database on it may belong to a live run elsewhere, and RavenDB records no creation time to age it by
    /// (measured: the database listing carries an up-time since load, nothing older).
    /// </para>
    /// <para>
    /// <b>Which databases.</b> Only test-SHAPED names (<see cref="IsTestDatabase"/>): <c>stomme</c>, words, then the random
    /// hex token every fixture cuts from a Guid, then optional sibling suffixes. <c>mediadb</c>, <c>stomme_uat</c> and a
    /// product's <c>Stomme</c> do not have that shape. A database you create by hand on the local server for a drive must
    /// not end in a hex token either, or the next test host takes it.
    /// </para>
    /// <para>
    /// Never fails a test host: an unreachable server (no local RavenDB, CI) costs one two-second connect attempt.
    /// </para>
    /// </remarks>
    internal static class RavenTestDatabaseSweep
    {
        internal const string DefaultUrl = "http://127.0.0.1:7000";

        // stomme + words + a random hex token cut from a Guid (12 or 32 characters in today's fixtures, 8 at the least) +
        // optional sibling suffixes such as _OpenIddict or _second.
        private static readonly Regex TestShape = new(
            "^(?i:stomme)[A-Za-z0-9_-]*?[_-][0-9a-f]{8,32}([_-][A-Za-z0-9]+)*$",
            RegexOptions.CultureInvariant);

        internal static bool IsTestDatabase(string name) => !string.IsNullOrEmpty(name) && TestShape.IsMatch(name);

        /// <summary>The server this machine's suites use, when it is this machine's own; otherwise null.</summary>
        internal static Uri? LocalServer(string? configuredUrl)
        {
            if (!Uri.TryCreate(string.IsNullOrWhiteSpace(configuredUrl) ? DefaultUrl : configuredUrl, UriKind.Absolute, out var url))
            {
                return null;
            }

            return url.IsLoopback ? url : null;
        }

        /// <summary>
        /// Deletes every test-shaped database on the local server - narrowed further by <paramref name="only"/>, which the
        /// sweep's own drive passes so that it never takes a database a concurrently running test class still uses.
        /// Returns how many, or 0 when there is nothing to reach.
        /// </summary>
        internal static int Sweep(string? configuredUrl, Func<string, bool>? only = null)
        {
            var server = LocalServer(configuredUrl);
            if (server == null)
            {
                return 0;
            }

            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })
                {
                    BaseAddress = server,
                    Timeout = TimeSpan.FromSeconds(30),
                };

                var doomed = ListDatabases(client).Where(name => IsTestDatabase(name) && (only == null || only(name))).ToList();

                // Small batches: one delete is one cluster command, and a large one has timed out on a loaded server.
                foreach (var batch in doomed.Chunk(25))
                {
                    var body = JsonSerializer.Serialize(new { DatabaseNames = batch, HardDelete = true });
                    using var request = new HttpRequestMessage(HttpMethod.Delete, "admin/databases")
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    using var response = client.Send(request);
                    response.EnsureSuccessStatusCode();
                }

                return doomed.Count;
            }
            catch (Exception)
            {
                // Called from the lock's module initializer: anything thrown here would fail every test in the process.
                // A missed sweep only means the next test host tries again.
                return 0;
            }
        }

        private static IEnumerable<string> ListDatabases(HttpClient client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "databases?namesOnly=true&pageSize=2147483647");
            using var response = client.Send(request);
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.GetProperty("Databases").EnumerateArray()
                .Select(name => name.GetString() ?? "")
                .ToList();
        }
    }
}
