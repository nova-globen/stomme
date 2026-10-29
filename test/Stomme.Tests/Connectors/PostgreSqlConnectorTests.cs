#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The PostgreSQL connector without a server: the endpoint carries no credential and the scheme's
    /// fields are applied; a malformed connection string fails with the connector's message; the host is handed to the
    /// egress to resolve and pin BEFORE the driver dials, so a refused host is the guard's own denial and no socket opens;
    /// a multi-host string is refused; a missing query parameter throws.
    /// </summary>
    public sealed class PostgreSqlConnectorTests
    {
        [Fact]
        public async Task A_malformed_connection_string_fails_before_any_pin()
        {
            var egress = new StubEgress();
            var result = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(egress, "this is not a connection string ;;="));
            Assert.False(result.Success);
            Assert.Contains("Invalid PostgreSQL connection string", result.Message);
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_through_the_egress_and_a_refused_host_is_the_guards_denial()
        {
            var refused = new StubEgress { Pinned = null };
            var ex = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(refused, "Host=db.internal;Port=5433;Database=demo;Username=demo")));
            Assert.Contains("db.internal", ex.Message);
            Assert.Equal(("db.internal", 5433), refused.Pins.Single());
            Assert.True(OutboundConnectGuard.IsDenial(ex)); // the host's guard recognises the contract's denial: the runner says DeniedByPolicy
            Assert.True(((IConnectorEgress)refused).IsDenial(ex));

            // The default port is pinned when the string names none.
            var pinned = new StubEgress { Pinned = null };
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(pinned, "Host=db.internal;Database=demo")));
            Assert.Equal(5432, pinned.Pins.Single().Port);
        }

        [Fact]
        public async Task A_multi_host_connection_string_is_refused_without_a_pin()
        {
            var egress = new StubEgress();
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(egress, "Host=a.internal,b.internal;Database=demo")));
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task Extract_without_a_query_parameter_throws_before_any_pin()
        {
            var egress = new StubEgress();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new PostgreSqlConnector(), Contexts.Extract(egress, "Host=db.internal;Database=demo", "query")));
            Assert.Contains("query", ex.Message);
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task A_value_whose_name_is_not_a_SQL_parameter_name_is_refused_before_any_pin()
        {
            // '@name = value' lines are bound values; a name the query could not write is refused before anything is dialled.
            var egress = new StubEgress();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new PostgreSqlConnector(), Contexts.Extract(egress, "Host=db.internal;Database=demo", "query",
                new Dictionary<string, string> { ["query"] = "SELECT @ok", ["@ok"] = "1", ["@not ok"] = "2" })));
            Assert.Contains("'@not ok'", ex.Message);
            Assert.Empty(egress.Pins);
            Assert.Equal(new[] { "a", "b_2" }, global::Stomme.Modules.Connectors.Shared.SqlValues.Of(new Dictionary<string, string> { ["@b_2"] = "", ["query"] = "q", ["@a"] = "" }).Select(value => value.Key));
        }

        [Fact]
        public async Task An_unreachable_pinned_address_is_the_drivers_failure_not_a_throw()
        {
            // The pin answers loopback on a closed port: the driver fails, the connector says so, the runner categorises it.
            var result = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(new StubEgress { Pinned = IPAddress.Loopback }, "Host=db.internal;Port=1;Database=nope", credentials: new Dictionary<string, string> { ["username"] = "x", ["password"] = "x" }, timeoutSeconds: 3));
            Assert.False(result.Success);
        }
    }

    /// <summary>
    /// The predecessor platform's live PostgreSqlConnectorTests (3, from its Aspire suite) by name against the infra
    /// UAT PostgreSQL through the HOST's egress: the private address is refused until the site allowlists it, and with it
    /// allowlisted the test succeeds with the credentials applied from the scheme and the query operation streams real rows
    /// that serialise to the wire shape. Fails, never skips, when the server is unreachable.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgreSqlConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"Host={PostgresTestEndpoint.Host};Port={PostgresTestEndpoint.Port};Database=postgres";

        private static Dictionary<string, string> Credentials => new() { ["username"] = PostgresTestEndpoint.User, ["password"] = PostgresTestEndpoint.Password };

        [Fact]
        public async Task TestConnection_Succeeds_AgainstLivePostgres_WithCredentialAppliedAsPassword()
        {
            // Without the allowlist the private infra address is refused before any socket (the egress's job, not the driver's).
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 15)));

            var result = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(new[] { PostgresTestEndpoint.Host }), Endpoint, credentials: Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {result.Success}: {result.Message}");
            Assert.True(result.Success, result.Message);
        }

        [Fact]
        public async Task TestConnection_Fails_ForUnreachableEndpoint()
        {
            var result = await new PostgreSqlConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(new[] { PostgresTestEndpoint.Host }), $"Host={PostgresTestEndpoint.Host};Port=1;Database=nope", credentials: Credentials, timeoutSeconds: 3));
            Assert.False(result.Success);
        }

        [Fact]
        public async Task Extract_QueryOperation_StreamsRealRows_AsCanonicalRecords_AndSerializesToJson()
        {
            var context = Contexts.Extract(new HostConnectorEgress(new[] { PostgresTestEndpoint.Host }), Endpoint, "query",
                new Dictionary<string, string> { ["query"] = "SELECT 1 AS id, 'alpha' AS name UNION ALL SELECT 2, 'beta' ORDER BY id" }, credentials: Credentials, timeoutSeconds: 15);
            var records = await Contexts.DrainAsync(new PostgreSqlConnector(), context);

            Assert.Equal(2, records.Count);
            Assert.Equal((1, "alpha", "beta"), (records[0].Fields["id"], records[0].Fields["name"], records[1].Fields["name"]));
            Assert.Equal("[{\"id\":1,\"name\":\"alpha\"},{\"id\":2,\"name\":\"beta\"}]", ConnectorRecordJson.Serialize(records));
        }

        [Fact]
        public async Task Extract_binds_each_at_name_value_as_a_parameter_never_as_SQL()
        {
            // A value that, spliced in, would end the string and run a statement of its own comes back as the text it is;
            // a value sent with no stated type is read as the type its place needs (an integer, added to).
            const string hostile = "x'); DROP TABLE nothing; --";
            var context = Contexts.Extract(new HostConnectorEgress(new[] { PostgresTestEndpoint.Host }), Endpoint, "query",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["query"] = "SELECT @name AS name, @count + 1 AS next", ["@name"] = hostile, ["@count"] = "41" }, credentials: Credentials, timeoutSeconds: 15);
            var record = Assert.Single(await Contexts.DrainAsync(new PostgreSqlConnector(), context));
            Assert.Equal(hostile, record.Fields["name"]);
            Assert.Equal(42, record.Fields["next"]);
        }
    }
}
