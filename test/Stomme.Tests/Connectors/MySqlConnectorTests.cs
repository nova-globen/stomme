#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.MySql;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - the MySQL connector without a server: the endpoint carries no credential and the scheme's fields are applied;
    /// a malformed connection string fails with the connector's message; what could reach past the egress - a Unix socket, a
    /// named pipe, shared memory, the server's redirection, more than one host - and LOCAL INFILE are refused before anything
    /// is dialled, and the safe values are forced whatever the string says; the host is resolved and pinned through the
    /// egress, a refused host the guard's own denial; a value bound as a number only where it reads as one.
    /// </summary>
    public sealed class MySqlConnectorTests
    {
        private static readonly Dictionary<string, string> NoSettings = new();

        [Fact]
        public async Task A_malformed_connection_string_fails_before_any_pin()
        {
            var egress = new StubEgress();
            foreach (var endpoint in new[] { "this is not a connection string ;;=", "Database=kassa", "Server= ;Database=kassa" })
            {
                var result = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(egress, endpoint));
                Assert.False(result.Success);
                Assert.StartsWith("Invalid MySQL connection string", result.Message, StringComparison.Ordinal);
            }

            var unknown = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(egress, "Server=db.internal;Colour=blue"));
            Assert.False(unknown.Success, unknown.Message); // a keyword the driver does not know
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task What_could_reach_past_the_egress_and_LOCAL_INFILE_are_refused_before_any_pin()
        {
            var egress = new StubEgress();
            var infile = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(egress, "Server=db.internal;AllowLoadLocalInfile=true"));
            Assert.Equal((false, "Invalid MySQL connection string: AllowLoadLocalInfile is never allowed - through LOAD DATA LOCAL INFILE the server could read any file of this host."), (infile.Success, infile.Message));
            foreach (var (endpoint, refusal) in new[]
            {
                ("Server=db.internal;Protocol=Unix", "a Unix socket, a named pipe or shared memory"),
                ("Server=db.internal;Protocol=Pipe", "a Unix socket, a named pipe or shared memory"),
                ("Server=db.internal;Protocol=Memory", "a Unix socket, a named pipe or shared memory"),
                ("Server=db.internal;ServerRedirectionMode=Preferred", "does not follow the server's redirection"),
                ("Server=a.internal,b.internal", "names ONE host"),
                ("Server=/var/run/mysqld/mysqld.sock", "is not a host to dial over TCP"),
                ("Server=.", "is not a host to dial over TCP"),
            })
            {
                var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(egress, endpoint)));
                Assert.Contains(refusal, denied.Message, StringComparison.Ordinal);
                Assert.True(OutboundConnectGuard.IsDenial(denied)); // the runner says DeniedByPolicy
            }

            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_through_the_egress_and_a_refused_host_is_the_guards_denial()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(refused, "Server=db.internal;Port=3307;Database=kassa")));
            Assert.Contains("db.internal", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("db.internal", 3307), refused.Pins.Single());

            var standard = new StubEgress { Pinned = null };
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(standard, "Server=db.internal;Database=kassa")));
            Assert.Equal(3306, standard.Pins.Single().Port); // MySQL's own port when the string names none

            var closed = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), "Server=db.internal;Port=1;Database=kassa", credentials: new Dictionary<string, string> { ["username"] = "x", ["password"] = "x" }, timeoutSeconds: 3));
            Assert.False(closed.Success); // the pinned loopback's closed port: the driver's failure, said, not thrown
        }

        [Fact]
        public void The_schemes_credentials_are_applied_and_the_safe_values_forced()
        {
            var builder = MySqlDatabaseConnector.Build("Server=db.internal;Database=kassa;AllowLoadLocalInfile=false", new Dictionary<string, string> { ["application_name"] = "Kassa" }, new Dictionary<string, string> { ["username"] = "kassa", ["password"] = "pw-1" }, TimeSpan.FromSeconds(10));
            Assert.Equal(("kassa", "pw-1", "Kassa", 10u), (builder.UserID, builder.Password, builder.ApplicationName, builder.ConnectionTimeout));
            Assert.Equal((false, global::MySqlConnector.MySqlConnectionProtocol.Sockets, global::MySqlConnector.MySqlServerRedirectionMode.Disabled), (builder.AllowLoadLocalInfile, builder.ConnectionProtocol, builder.ServerRedirectionMode));
            Assert.Equal(1u, MySqlDatabaseConnector.Build("Server=db.internal", NoSettings, NoSettings, TimeSpan.FromMilliseconds(200)).ConnectionTimeout);
            Assert.Equal(1024u, MySqlDatabaseConnector.Build("Server=db.internal", NoSettings, NoSettings, TimeSpan.FromHours(2)).ConnectionTimeout);
        }

        [Fact]
        public void A_value_is_bound_as_a_number_only_where_it_reads_as_one()
        {
            Assert.Equal(new object[] { 10L, -3L, 0L, 99.50m, -0.5m }, new[] { "10", "-3", "0", "99.50", "-0.5" }.Select(MySqlDatabaseConnector.Typed));
            Assert.All(new[] { "0701", "1e5", "1,5", " 10", "10 ", "+1", "Kafé", "", "99999999999999999999", "1.", ".5" }, value => Assert.Equal(value, MySqlDatabaseConnector.Typed(value)));
        }

        [Fact]
        public async Task Extract_without_a_query_or_with_a_value_named_wrong_throws_before_any_pin()
        {
            var egress = new StubEgress();
            Assert.Contains("query", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MySqlDatabaseConnector(), Contexts.Extract(egress, "Server=db.internal", "query")))).Message, StringComparison.Ordinal);
            Assert.Contains("'@not ok'", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MySqlDatabaseConnector(), Contexts.Extract(egress, "Server=db.internal", "query", new Dictionary<string, string> { ["query"] = "SELECT @ok", ["@ok"] = "1", ["@not ok"] = "2" })))).Message, StringComparison.Ordinal);
            Assert.Empty(egress.Pins);
        }
    }

    /// <summary>
    /// BL-229 - the MySQL connector against a real MySQL 8.4 (this machine's throwaway container on loopback, or a CI job's
    /// service container - <see cref="MySqlTestEndpoint"/>) through the HOST's egress: loopback refused until the site
    /// allowlists it; a test naming the server; a wrong password failing; a table made, rows written with their values bound
    /// - a hostile one stored as the text it is, a leading zero kept - each write answering the rows it changed and the id it
    /// inserted, rows read with their types, LIMIT bound as a number, an upsert by MySQL's ON DUPLICATE KEY UPDATE; and LOAD
    /// DATA LOCAL INFILE refused by the connector even where the server allows it. Fails, never skips, without a server (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MySqlConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"Server={MySqlTestEndpoint.Host};Port={MySqlTestEndpoint.Port};Database=kassa";

        private static Dictionary<string, string> Credentials => new() { ["username"] = MySqlTestEndpoint.User, ["password"] = MySqlTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { MySqlTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string query, Dictionary<string, string>? values = null)
        {
            var parameters = new Dictionary<string, string>(values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) { ["query"] = query };
            return Contexts.DrainAsync(new MySqlDatabaseConnector(), Contexts.Extract(Egress, Endpoint, "query", parameters, credentials: Credentials, timeoutSeconds: 15));
        }

        private static (object? Affected, object? Id) Wrote(List<ConnectorRecord> records)
        {
            var record = Assert.Single(records).Fields;
            return (record["affected_rows"], record["last_insert_id"]);
        }

        [Fact]
        public async Task A_test_names_the_server_and_a_wrong_password_fails()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 15))); // loopback, not allowlisted

            var result = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {result.Success}: {result.Message}");
            Assert.True(result.Success, result.Message);
            Assert.StartsWith("Connected to MySQL 8.", result.Message, StringComparison.Ordinal);

            var wrong = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["username"] = MySqlTestEndpoint.User, ["password"] = "not-the-password" }, timeoutSeconds: 15));
            Assert.False(wrong.Success);
        }

        [Fact]
        public async Task The_driver_dials_the_address_the_egress_pinned_never_the_name()
        {
            // A name that resolves nowhere, pinned by the egress to the server's address: only the pinned address can answer.
            var pinned = new StubEgress { Pinned = System.Net.IPAddress.Parse(MySqlTestEndpoint.Host) };
            var result = await new MySqlDatabaseConnector().TestConnectionAsync(Contexts.Test(pinned, $"Server=kassa-db.invalid;Port={MySqlTestEndpoint.Port};Database=kassa", credentials: Credentials, timeoutSeconds: 15));
            Assert.True(result.Success, result.Message);
            Assert.Equal(("kassa-db.invalid", MySqlTestEndpoint.Port), pinned.Pins.Single());
        }

        [Fact]
        public async Task Rows_are_written_with_their_values_bound_and_read_back_with_their_types()
        {
            var table = "orders_" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Assert.Equal((0L, 0L), Wrote(await Run($"CREATE TABLE {table} (id INT AUTO_INCREMENT PRIMARY KEY, kund VARCHAR(60) NOT NULL, summa DECIMAL(8,2), betald BOOLEAN, kod VARCHAR(10))")));
                Assert.Equal((1L, 1L), Wrote(await Run($"INSERT INTO {table} (kund, summa, betald, kod) VALUES (@kund, @summa, @betald, @kod)", new() { ["@kund"] = "Kafé Ada", ["@summa"] = "99.50", ["@betald"] = "1", ["@kod"] = "0701" })));
                const string Hostile = "x'); DROP TABLE nothing; --";
                Assert.Equal((1L, 2L), Wrote(await Run($"INSERT INTO {table} (kund, summa, betald) VALUES (@kund, @summa, @betald)", new() { ["@kund"] = Hostile, ["@summa"] = "12", ["@betald"] = "0" })));

                var rows = await Run($"SELECT id, kund, summa, betald, kod FROM {table} ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.Equal((1, "Kafé Ada", 99.50m, true, "0701"), (rows[0].Fields["id"], rows[0].Fields["kund"], rows[0].Fields["summa"], rows[0].Fields["betald"], rows[0].Fields["kod"])); // a leading zero kept as text
                Assert.Equal((Hostile, 12.00m, false, (object?)null), (rows[1].Fields["kund"], rows[1].Fields["summa"], rows[1].Fields["betald"], rows[1].Fields["kod"])); // the hostile value the text it is
                Assert.Equal("Kafé Ada", Assert.Single(await Run($"SELECT kund FROM {table} ORDER BY id LIMIT @n", new() { ["@n"] = "1" })).Fields["kund"]); // LIMIT bound as a number

                Assert.Equal(2L, Wrote(await Run($"INSERT INTO {table} (id, kund) VALUES (@id, @kund) ON DUPLICATE KEY UPDATE kund = VALUES(kund)", new() { ["@id"] = "1", ["@kund"] = "Kafé Bo" })).Affected); // MySQL counts an upsert's update as two
                Assert.Equal("Kafé Bo", Assert.Single(await Run($"SELECT kund FROM {table} WHERE id = @id", new() { ["@id"] = "1" })).Fields["kund"]);
                Assert.Equal((1L, 0L), Wrote(await Run($"DELETE FROM {table} WHERE kund = @kund", new() { ["@kund"] = Hostile })));
            }
            finally
            {
                await Run($"DROP TABLE IF EXISTS {table}");
            }
        }

        [Fact]
        public async Task LOAD_DATA_LOCAL_INFILE_is_refused_by_the_connector_even_where_the_server_allows_it()
        {
            var table = "infile_" + Guid.NewGuid().ToString("N")[..8];
            await Run("SET GLOBAL local_infile = 1"); // the server would let a client send a file: only the connector stands in the way
            try
            {
                Assert.Equal(1L, Convert.ToInt64(Assert.Single(await Run("SELECT @@GLOBAL.local_infile AS allowed")).Fields["allowed"], System.Globalization.CultureInfo.InvariantCulture));
                await Run($"CREATE TABLE {table} (line TEXT)");
                var refused = await Assert.ThrowsAnyAsync<Exception>(() => Run($"LOAD DATA LOCAL INFILE 'C:/Windows/win.ini' INTO TABLE {table}"));
                output.WriteLine($"[live] {refused.GetType().Name}: {refused.Message}");
                Assert.Contains("must be enabled on both the client and server sides", refused.Message, StringComparison.Ordinal); // the client never offered to send a file
                Assert.Empty(await Run($"SELECT line FROM {table}"));
            }
            finally
            {
                await Run($"DROP TABLE IF EXISTS {table}");
                await Run("SET GLOBAL local_infile = 0");
            }
        }
    }
}
