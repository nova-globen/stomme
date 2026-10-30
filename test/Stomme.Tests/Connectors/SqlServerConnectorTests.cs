#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The SQL Server connector cases (10), carried over by name over the port, plus the pin: the data source is
    /// parsed (a tcp: prefix, host,port), a named instance without a port is refused before any pin, the driver dials the
    /// pinned address with the certificate still checked against the original name. CHANGED: there is no legacy
    /// single-credential fallback (the scheme's fields are the only credentials), so
    /// <c>BuildConnectionString_FallsBackToTheLegacySingleCredential_AsThePassword</c> asserts the endpoint's own user id survives with no password.
    /// </summary>
    public sealed class SqlServerConnectorTests
    {
        private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

        [Fact]
        public void Declarations_ExposeTheSqlServerConnectorType_WithBasicAuthAndExtract()
        {
            IConnector connector = new SqlServerConnector();
            Assert.Equal(("sqlserver", "Microsoft SQL Server", AuthenticationType.Basic, ConnectorEndpointKind.ConnectionString), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.EndpointKind));
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.TestConnection));
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
        }

        [Fact]
        public void ConfigSchema_DeclaresTheOptionalConnectionFields()
        {
            var fields = new SqlServerConnector().ConfigSchema.Fields;
            Assert.Equal(new[] { "application_name", "trust_server_certificate" }, fields.Select(f => f.Name).ToArray());
            Assert.All(fields, f => Assert.False(f.Required));
            var trust = fields.Single(f => f.Name == "trust_server_certificate");
            Assert.Equal((ConnectorConfigFieldType.Boolean, "false"), (trust.Type, trust.Default));
        }

        [Fact]
        public void ConfigSchema_RejectsANonBooleanTrustServerCertificate() =>
            Assert.Contains(new SqlServerConnector().ConfigSchema.Validate(new Dictionary<string, string> { ["trust_server_certificate"] = "not-a-bool" }), e => e.Contains("trust_server_certificate"));

        [Fact]
        public void Operations_DeclareASingleQueryOperation_WithARequiredQueryParameter()
        {
            var operation = Assert.Single(new SqlServerConnector().Operations);
            Assert.Equal("query", operation.Name);
            Assert.Contains(operation.Parameters.Fields, f => f.Name == "query" && f.Required && f.Type == ConnectorConfigFieldType.Multiline);
        }

        [Fact]
        public async Task TestConnection_InvalidEndpoint_ReturnsFail_WithAnActionableMessage()
        {
            var egress = new StubEgress();
            var result = await new SqlServerConnector().TestConnectionAsync(Contexts.Test(egress, "not-a-connection-string"));
            Assert.False(result.Success);
            Assert.StartsWith("Invalid SQL Server connection string", result.Message);
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task TestConnection_Fails_ForUnreachableServer()
        {
            var result = await new SqlServerConnector().TestConnectionAsync(Contexts.Test(new StubEgress { Pinned = IPAddress.Loopback }, "Server=db.internal,1;Database=nope;TrustServerCertificate=true", credentials: new Dictionary<string, string> { ["username"] = "x", ["password"] = "x" }, timeoutSeconds: 3));
            Assert.False(result.Success);
            Assert.NotEmpty(result.Message);
        }

        [Fact]
        public async Task Extract_WithoutAQueryParameter_Throws()
        {
            var egress = new StubEgress();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SqlServerConnector(), Contexts.Extract(egress, "Server=localhost;Database=db", "query")));
            Assert.Empty(egress.Pins);
        }

        [Fact]
        public void BuildConnectionString_AppliesTheNamedCredentials_SettingsAndClampedTimeout()
        {
            var builder = SqlServerConnector.BuildConnectionString("Server=db.example.com;Database=stomme",
                new Dictionary<string, string> { ["application_name"] = "Stomme", ["trust_server_certificate"] = "true" },
                new Dictionary<string, string> { ["username"] = "alice", ["password"] = "s3cret" }, TimeSpan.FromSeconds(15));
            Assert.Equal(("alice", "s3cret", "Stomme", true, 15), (builder.UserID, builder.Password, builder.ApplicationName, builder.TrustServerCertificate, builder.ConnectTimeout));
        }

        [Fact]
        public void BuildConnectionString_FallsBackToTheLegacySingleCredential_AsThePassword()
        {
            // CHANGED: there is no single credential any more; the endpoint's own user id survives and the password stays empty without the scheme's field.
            var builder = SqlServerConnector.BuildConnectionString("Server=db.example.com;Database=stomme;User ID=sa", NoValues, NoValues, TimeSpan.FromSeconds(5));
            Assert.Equal(("sa", string.Empty, false), (builder.UserID, builder.Password, builder.TrustServerCertificate));
        }

        [Fact]
        public void BuildConnectionString_NeverReadsThePasswordFromSettings_AndLeavesItEmptyWithoutACredential()
        {
            var builder = SqlServerConnector.BuildConnectionString("Server=db.example.com;Database=stomme", new Dictionary<string, string> { ["password"] = "smuggled" }, NoValues, TimeSpan.FromSeconds(5));
            Assert.Equal(string.Empty, builder.Password);
        }

        // ---- the port's additions: the data source and the pin ---------------------------------------------------------

        [Theory]
        [InlineData("db.example.com", "db.example.com", 1433)]
        [InlineData("tcp:db.example.com,1434", "db.example.com", 1434)]
        [InlineData("db.example.com,1434", "db.example.com", 1434)]
        [InlineData("db.example.com\\SQLEXPRESS,1435", "db.example.com", 1435)]
        [InlineData("[::1],1433", "::1", 1433)]
        [InlineData(".", "localhost", 1433)]
        public void The_data_source_parses_to_a_host_and_a_port(string dataSource, string host, int port) => Assert.Equal((host, port), SqlServerConnector.ParseDataSource(dataSource));

        [Fact]
        public async Task A_named_instance_without_a_port_and_a_refused_host_are_the_contracts_denial()
        {
            var egress = new StubEgress();
            var ex = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new SqlServerConnector().TestConnectionAsync(Contexts.Test(egress, "Server=db.internal\\SQLEXPRESS;Database=demo")));
            Assert.Contains("SQL Browser", ex.Message);
            Assert.Empty(egress.Pins); // refused before any pin

            var refused = new StubEgress { Pinned = null };
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new SqlServerConnector().TestConnectionAsync(Contexts.Test(refused, "Server=tcp:db.internal,1434;Database=demo")));
            Assert.Equal(("db.internal", 1434), refused.Pins.Single());
        }
    }

    /// <summary>
    /// The SQL Server connector against a shared SQL Server through the HOST's egress (fails, never
    /// skips): refused until the site allowlists the private address; then connected with the scheme's credentials
    /// and the certificate accepted through the connection's own setting (the test server's certificate is self-signed);
    /// the query operation streams real rows.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"Server={SqlServerTestEndpoint.Host},{SqlServerTestEndpoint.Port};Database=master;Encrypt=True";

        private static Dictionary<string, string> Credentials => new() { ["username"] = SqlServerTestEndpoint.User, ["password"] = SqlServerTestEndpoint.Password };

        private static Dictionary<string, string> Settings => new() { ["trust_server_certificate"] = "true" };

        [Fact]
        public async Task TestConnection_Succeeds_AgainstLiveSqlServer_AndIsRefusedWithoutTheAllowlist()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new SqlServerConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, Settings, Credentials, timeoutSeconds: 15)));

            var result = await new SqlServerConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(new[] { SqlServerTestEndpoint.Host }), Endpoint, Settings, Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {result.Success}: {result.Message}");
            Assert.True(result.Success, result.Message);
        }

        [Fact]
        public async Task Extract_QueryOperation_StreamsRealRows()
        {
            var context = Contexts.Extract(new HostConnectorEgress(new[] { SqlServerTestEndpoint.Host }), Endpoint, "query",
                new Dictionary<string, string> { ["query"] = "SELECT 1 AS id, 'alpha' AS name UNION ALL SELECT 2, 'beta' ORDER BY id" }, Settings, Credentials, timeoutSeconds: 15);
            var records = await Contexts.DrainAsync(new SqlServerConnector(), context);
            Assert.Equal(2, records.Count);
            Assert.Equal((1, "alpha", "beta"), (records[0].Fields["id"], records[0].Fields["name"], records[1].Fields["name"]));
        }

        [Fact]
        public async Task Extract_binds_each_at_name_value_as_a_parameter_never_as_SQL()
        {
            // A value that, spliced in, would end the string and run a statement of its own comes back as the text it is;
            // the nvarchar it is sent as converts to the type its place needs (an int, added to).
            const string hostile = "x'); DROP TABLE nothing; --";
            var context = Contexts.Extract(new HostConnectorEgress(new[] { SqlServerTestEndpoint.Host }), Endpoint, "query",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["query"] = "SELECT @name AS name, @count + 1 AS next", ["@name"] = hostile, ["@count"] = "41" }, Settings, Credentials, timeoutSeconds: 15);
            var record = Assert.Single(await Contexts.DrainAsync(new SqlServerConnector(), context));
            Assert.Equal(hostile, record.Fields["name"]);
            Assert.Equal(42, record.Fields["next"]);
        }
    }
}
