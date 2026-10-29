#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.TestInfrastructure;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The start-of-run RavenDB sweep's two rules, in both directions: which names are test databases, and which server is
    /// this machine's own. Names taken from what a developer machine's local server and a shared server actually held.
    /// </summary>
    public sealed class RavenTestDatabaseSweepTests
    {
        [Theory]
        [InlineData("stomme_authz_gate_4810c8b2151e")]
        [InlineData("stomme_authz_gate_4810c8b2151e_OpenIddict")]
        [InlineData("stomme_authz_gate_4810c8b2151e_second")]
        [InlineData("stomme_gate_tmpl_2a4903fdf17b")]
        [InlineData("stomme_bl55_1782be9d198b_OpenIddict")]
        [InlineData("stomme_idpui_3cb252e438e4")]
        [InlineData("stomme-conformance-0f8e2a6c4b1d49e3a7c5b2d1e0f9a8c7")]
        [InlineData("stomme_engine_conformance_4b1d49e3a7c5")]
        public void A_test_database_is_one(string name) => Assert.True(RavenTestDatabaseSweep.IsTestDatabase(name));

        [Theory]
        [InlineData("mediadb")]
        [InlineData("stomme_uat")]
        [InlineData("stomme_uat_OpenIddict")]
        [InlineData("Stomme")]
        [InlineData("Stomme_OpenIddict")]
        [InlineData("stomme")]
        [InlineData("stomme_drive")]
        [InlineData("otherapp_4810c8b2151e")]
        [InlineData("")]
        public void Anything_else_is_not(string name) => Assert.False(RavenTestDatabaseSweep.IsTestDatabase(name));

        [Theory]
        [InlineData(null, "127.0.0.1")]
        [InlineData("", "127.0.0.1")]
        [InlineData("http://127.0.0.1:7000", "127.0.0.1")]
        [InlineData("http://localhost:7000", "localhost")]
        [InlineData("http://[::1]:7000", "[::1]")]
        public void The_local_server_is_swept(string? configured, string host) =>
            Assert.Equal(host, RavenTestDatabaseSweep.LocalServer(configured)?.Host);

        [Theory]
        [InlineData("http://192.168.10.20:8081")]   // a shared server: other machines' runs live there
        [InlineData("http://ravendb:8080")]        // a CI service container
        [InlineData("not a url")]
        public void A_server_other_machines_can_use_is_not(string configured) =>
            Assert.Null(RavenTestDatabaseSweep.LocalServer(configured));
    }

    /// <summary>
    /// The sweep driven against the real server the suites use: it deletes test-shaped databases, siblings included, and
    /// leaves a database that is not test-shaped. Narrowed to this drive's own probe names, because the sweep proper runs
    /// before any test and would here take databases that concurrently running classes still use.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenTestDatabaseSweepDriveTests
    {
        private const string ProbePrefix = "stomme_sweepprobe_";

        [Fact]
        public async Task The_sweep_deletes_test_shaped_databases_on_the_local_server_and_leaves_every_other_name()
        {
            var url = RavenGateStore.Url;
            var token = Guid.NewGuid().ToString("N").Substring(0, 12);
            var probe = ProbePrefix + token;
            var sibling = probe + "_OpenIddict";
            // Unique but NOT test-shaped: the token's hex digits moved to letters past 'f'.
            var keep = ProbePrefix + "keep_" + new string(token.Select(digit => (char)('g' + Convert.ToInt32(digit.ToString(), 16))).ToArray());
            Assert.False(RavenTestDatabaseSweep.IsTestDatabase(keep));

            using var store = new DocumentStore { Urls = new[] { url } }.Initialize();
            foreach (var name in new[] { probe, sibling, keep })
            {
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(name)));
            }

            try
            {
                var swept = RavenTestDatabaseSweep.Sweep(url, name => name.StartsWith(ProbePrefix, StringComparison.Ordinal));
                var left = await NamesSettledAsync(store, expectAbsent: RavenTestDatabaseSweep.LocalServer(url) != null ? new[] { probe, sibling } : Array.Empty<string>());

                if (RavenTestDatabaseSweep.LocalServer(url) != null)
                {
                    Assert.Equal(2, swept);
                    Assert.DoesNotContain(probe, left);
                    Assert.DoesNotContain(sibling, left);
                }
                else
                {
                    // A server other machines use is never swept - the other direction of the same rule.
                    Assert.Equal(0, swept);
                    Assert.Contains(probe, left);
                    Assert.Contains(sibling, left);
                }

                Assert.Contains(keep, left);
            }
            finally
            {
                await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters
                {
                    DatabaseNames = new[] { probe, sibling, keep },
                    HardDelete = true,
                }));
            }
        }

        [Fact]
        public async Task A_teardown_deletes_the_installs_database_and_every_sibling_and_nothing_that_only_shares_its_prefix()
        {
            var database = "stomme_siblingprobe_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var siblings = new[] { database + "_OpenIddict", database + "_second" };
            var lookalike = database + "9";   // shares the prefix, is not a sibling: no '_' after the name

            using var store = new DocumentStore { Urls = new[] { RavenGateStore.Url } }.Initialize();
            foreach (var name in siblings.Append(database).Append(lookalike))
            {
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(name)));
            }

            try
            {
                Assert.Equal(3, RavenTestDatabaseJanitor.DeleteWithSiblings(store, database));

                var left = await NamesSettledAsync(store, expectAbsent: siblings.Append(database).ToArray());
                Assert.DoesNotContain(database, left);
                Assert.All(siblings, sibling => Assert.DoesNotContain(sibling, left));
                Assert.Contains(lookalike, left);
            }
            finally
            {
                await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters
                {
                    DatabaseNames = siblings.Append(database).Append(lookalike).ToArray(),
                    HardDelete = true,
                }));
            }
        }

        // A hard delete is a cluster command; the database leaves the listing a moment after it is acknowledged.
        private static async Task<string[]> NamesSettledAsync(IDocumentStore store, string[] expectAbsent)
        {
            string[] names = Array.Empty<string>();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                names = await store.Maintenance.Server.SendAsync(new GetDatabaseNamesOperation(0, int.MaxValue));
                if (!expectAbsent.Any(names.Contains))
                {
                    break;
                }

                await Task.Delay(250);
            }

            return names;
        }
    }
}
