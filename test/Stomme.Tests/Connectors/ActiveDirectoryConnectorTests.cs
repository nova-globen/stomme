#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.ActiveDirectory;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The ActiveDirectoryConnectorTests cases (14), kept by name, over the port through the client seam,
    /// plus the pin: the connection carries the host as NAMED (the certificate's name, every row's ADTarget) and the
    /// ADDRESS the host pinned (what the socket dials); a refused host is the contract's denial.
    /// </summary>
    public sealed class ActiveDirectoryConnectorTests
    {
        private const string DefaultNamingContext = "DC=corp,DC=example,DC=com";
        private const string ConfigNamingContext = "CN=Configuration,DC=corp,DC=example,DC=com";

        private sealed class FakeAdClient : IActiveDirectoryClient
        {
            public List<(string BaseDn, string Filter, AdSearchScope Scope, IReadOnlyList<string> Attributes, int PageSize)> Searches { get; } = new();

            public Dictionary<string, IReadOnlyList<string>> RootDse { get; } = new(StringComparer.OrdinalIgnoreCase);

            public Func<string, IEnumerable<AdEntry>> EntriesFor { get; set; } = _ => Array.Empty<AdEntry>();

            public bool Disposed { get; private set; }

            public IReadOnlyList<string> ReadRootDse(string attribute) => RootDse.TryGetValue(attribute, out var values) ? values : Array.Empty<string>();

            public IEnumerable<AdEntry> Search(string baseDn, string filter, AdSearchScope scope, IReadOnlyList<string> attributes, int pageSize, CancellationToken cancellationToken)
            {
                Searches.Add((baseDn, filter, scope, attributes, pageSize));
                return EntriesFor(filter);
            }

            public void Dispose() => Disposed = true;
        }

        private static AdEntry Entry(string dn, params (string Name, string[] Values)[] attributes) =>
            new(dn, attributes.ToDictionary(a => a.Name, a => (IReadOnlyList<string>)a.Values, StringComparer.OrdinalIgnoreCase));

        private static FakeAdClient ClientWithDefaultContext()
        {
            var client = new FakeAdClient();
            client.RootDse["defaultNamingContext"] = new[] { DefaultNamingContext };
            client.RootDse["configurationNamingContext"] = new[] { ConfigNamingContext };
            return client;
        }

        private static (ActiveDirectoryConnector Connector, Func<ActiveDirectoryConnection?> Captured) Build(FakeAdClient client)
        {
            ActiveDirectoryConnection? captured = null;
            var connector = new ActiveDirectoryConnector(connection =>
            {
                captured = connection;
                return client;
            });
            return (connector, () => captured);
        }

        private static ConnectorExtractContext ExtractContext(string operation, string endpoint = "ldaps://dc.corp.example.com", Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, StubEgress? egress = null) =>
            Contexts.Extract(egress ?? new StubEgress(), endpoint, operation, parameters, settings, timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesThePort()
        {
            IConnector connector = new ActiveDirectoryConnector();
            Assert.Equal(("activedirectory", "Microsoft Active Directory", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(ConnectorEndpointKind.ConnectionString, connector.EndpointKind); // a directory host, ldap:// or ldaps:// - never HTTP's
            foreach (var endpoint in new[] { "dc.corp.example.com", "ldap://dc.corp.example.com:389", "ldaps://dc.corp.example.com" })
            {
                Assert.Equal(endpoint, Stomme.Modules.Connectivity.Domain.ConnectionEndpoint.For(connector.EndpointKind, endpoint).Value); // each one a connection accepts
            }

            Assert.Contains(ConnectorCategory.MicrosoftCloudDirectory, connector.Categories);
            Assert.NotNull(connector.WebsiteUrl);
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal(new[] { "computers", "users", "subnets", "custom" }, ((IConnectorExtractor)connector).Operations.Select(o => o.Name));
        }

        [Fact]
        public async Task Extract_Computers_UsesComputerFilterSubtreeAndFlattensRow()
        {
            const long lastLogon = 132539040000000000L;
            var client = ClientWithDefaultContext();
            client.EntriesFor = _ => new[]
            {
                Entry("CN=HOST1,OU=Servers,DC=corp,DC=example,DC=com", ("name", new[] { "HOST1" }), ("operatingSystem", new[] { "Windows Server 2022" }),
                    ("lastLogonTimestamp", new[] { lastLogon.ToString(CultureInfo.InvariantCulture) }), ("accountExpires", new[] { "0" }), ("lastLogoff", new[] { "0" })),
            };
            var (connector, captured) = Build(client);

            var records = await Contexts.DrainAsync(connector, ExtractContext("computers"));

            var search = Assert.Single(client.Searches);
            Assert.Equal((DefaultNamingContext, "(objectCategory=computer)", AdSearchScope.Subtree, 500), (search.BaseDn, search.Filter, search.Scope, search.PageSize));
            Assert.Contains("operatingSystem", search.Attributes);
            var record = Assert.Single(records);
            Assert.Equal(("HOST1", "Windows Server 2022", "corp", "corp.example.com", "dc.corp.example.com"), (record.Fields["name"], record.Fields["operatingSystem"], record.Fields["FlatDomain"], record.Fields["QualifiedDomain"], record.Fields["ADTarget"]));
            Assert.Equal("CN=HOST1,OU=Servers,DC=corp,DC=example,DC=com", record.Fields["distinguishedName"]);
            Assert.Equal(DateTime.FromFileTimeUtc(lastLogon).ToString("o", CultureInfo.InvariantCulture), record.Fields["lastLogonTimestamp"]);
            Assert.Equal((string.Empty, string.Empty), (record.Fields["accountExpires"], record.Fields["lastLogoff"]));

            // The LDAPS endpoint parsed to host + port 636 + SSL; the socket dials the PINNED address; the client was disposed.
            Assert.Equal(("dc.corp.example.com", 636, true, IPAddress.Loopback), (captured()!.Host, captured()!.Port, captured()!.UseSsl, captured()!.Address));
            Assert.True(client.Disposed);
        }

        [Fact]
        public async Task Extract_Users_UsesPersonUserFilter()
        {
            var client = ClientWithDefaultContext();
            var (connector, _) = Build(client);
            await Contexts.DrainAsync(connector, ExtractContext("users"));
            var search = Assert.Single(client.Searches);
            Assert.Equal(("(&(objectCategory=person)(objectClass=user))", AdSearchScope.Subtree), (search.Filter, search.Scope));
            Assert.Contains("mail", search.Attributes);
        }

        [Fact]
        public async Task Extract_Subnets_ReadsSitesAndExpandsBacklinksIntoOneRowPerSubnet()
        {
            var client = ClientWithDefaultContext();
            client.EntriesFor = filter => filter == "(objectClass=site)"
                ? new[]
                {
                    Entry("CN=HQ,CN=Sites,CN=Configuration,DC=corp,DC=example,DC=com", ("name", new[] { "HQ" }), ("location", new[] { "London" }), ("description", new[] { "Head office" }),
                        ("siteObjectBL", new[] { "CN=10.0.0.0/24,CN=Subnets,CN=Sites,CN=Configuration,DC=corp,DC=example,DC=com", "CN=10.0.1.0/24,CN=Subnets,CN=Sites,CN=Configuration,DC=corp,DC=example,DC=com" })),
                }
                : Array.Empty<AdEntry>();
            var (connector, _) = Build(client);

            var records = await Contexts.DrainAsync(connector, ExtractContext("subnets"));

            var search = Assert.Single(client.Searches);
            Assert.Equal(("CN=Sites,CN=Configuration,DC=corp,DC=example,DC=com", AdSearchScope.OneLevel), (search.BaseDn, search.Scope));
            Assert.Equal(2, records.Count);
            Assert.Equal(("10.0.0.0/24", "10.0.1.0/24", "HQ", "London", "corp", "corp.example.com", "dc.corp.example.com"),
                (records[0].Fields["Subnets"], records[1].Fields["Subnets"], records[0].Fields["Name"], records[0].Fields["Location"], records[0].Fields["FlatDomain"], records[0].Fields["QualifiedDomain"], records[0].Fields["ADTarget"]));
        }

        [Fact]
        public async Task Extract_Subnets_SiteWithoutBacklinks_StillEmitsOneRow()
        {
            var client = ClientWithDefaultContext();
            client.EntriesFor = filter => filter == "(objectClass=site)" ? new[] { Entry("CN=Empty,CN=Sites,CN=Configuration,DC=corp,DC=example,DC=com", ("name", new[] { "Empty" })) } : Array.Empty<AdEntry>();
            var (connector, _) = Build(client);
            var record = Assert.Single(await Contexts.DrainAsync(connector, ExtractContext("subnets")));
            Assert.Equal(("Empty", string.Empty), (record.Fields["Name"], record.Fields["Subnets"]));
        }

        [Fact]
        public async Task Extract_Custom_UsesSuppliedFilterScopeAndAttributes()
        {
            var client = ClientWithDefaultContext();
            client.EntriesFor = _ => new[] { Entry("CN=Admins,OU=Groups,DC=corp,DC=example,DC=com", ("cn", new[] { "Admins" }), ("member", new[] { "CN=a,DC=corp,DC=example,DC=com", "CN=b,DC=corp,DC=example,DC=com" })) };
            var (connector, _) = Build(client);
            var parameters = new Dictionary<string, string> { ["filter"] = "(objectClass=group)", ["scope"] = "onelevel", ["attributes"] = "cn, member", ["base_dn"] = "OU=Groups,DC=corp,DC=example,DC=com" };

            var record = Assert.Single(await Contexts.DrainAsync(connector, ExtractContext("custom", parameters: parameters)));

            var search = Assert.Single(client.Searches);
            Assert.Equal(("OU=Groups,DC=corp,DC=example,DC=com", "(objectClass=group)", AdSearchScope.OneLevel), (search.BaseDn, search.Filter, search.Scope));
            Assert.Equal(new[] { "cn", "member" }, search.Attributes);
            Assert.Equal("CN=a,DC=corp,DC=example,DC=com, CN=b,DC=corp,DC=example,DC=com", record.Fields["member"]); // multi-valued joins
        }

        [Fact]
        public async Task Extract_Custom_WithoutFilter_Throws()
        {
            var (connector, _) = Build(ClientWithDefaultContext());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, ExtractContext("custom")));
            Assert.Contains("filter", ex.Message);
        }

        [Fact]
        public async Task Extract_UsesConfiguredBaseDnOverRootDse()
        {
            var client = ClientWithDefaultContext();
            var (connector, _) = Build(client);
            await Contexts.DrainAsync(connector, ExtractContext("computers", settings: new Dictionary<string, string> { ["base_dn"] = "OU=Managed,DC=corp,DC=example,DC=com" }));
            Assert.Equal("OU=Managed,DC=corp,DC=example,DC=com", Assert.Single(client.Searches).BaseDn);
        }

        [Fact]
        public async Task Extract_PageSizeSetting_IsClampedToTheLdapCeiling()
        {
            var client = ClientWithDefaultContext();
            var (connector, _) = Build(client);
            await Contexts.DrainAsync(connector, ExtractContext("computers", settings: new Dictionary<string, string> { ["page_size"] = "5000" }));
            Assert.Equal(1000, Assert.Single(client.Searches).PageSize);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var (connector, _) = Build(ClientWithDefaultContext());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, ExtractContext("bogus")));
            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task Extract_PlainHostEndpoint_DefaultsToPort389NoSsl()
        {
            var client = ClientWithDefaultContext();
            var (connector, captured) = Build(client);
            var egress = new StubEgress();
            await Contexts.DrainAsync(connector, ExtractContext("computers", endpoint: "dc.corp.example.com", egress: egress));
            Assert.Equal((389, false), (captured()!.Port, captured()!.UseSsl));
            Assert.Equal(("dc.corp.example.com", 389), egress.Pins.Single());
        }

        [Fact]
        public async Task TestConnection_Ok_WhenBindSucceeds()
        {
            var client = ClientWithDefaultContext();
            var (connector, _) = Build(client);
            var result = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(), "ldap://dc.corp.example.com", timeoutSeconds: 30));
            Assert.True(result.Success, result.Message);
            Assert.Contains(DefaultNamingContext, result.Message);
            Assert.True(client.Disposed);
        }

        [Fact]
        public async Task TestConnection_Fail_WhenBindThrows()
        {
            var connector = new ActiveDirectoryConnector(_ => throw new InvalidOperationException("The directory rejected the credentials."));
            var result = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(), "dc.corp.example.com", timeoutSeconds: 30));
            Assert.False(result.Success);
            Assert.Contains("rejected the credentials", result.Message);
        }

        [Fact]
        public async Task Extract_MissingEndpoint_Throws()
        {
            var (connector, _) = Build(ClientWithDefaultContext());
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(connector, ExtractContext("computers", endpoint: "")));
        }

        // ---- the port's additions ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_refused_host_is_the_contracts_denial_before_any_bind()
        {
            var bound = false;
            var connector = new ActiveDirectoryConnector(_ => { bound = true; return ClientWithDefaultContext(); });
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => connector.TestConnectionAsync(Contexts.Test(new StubEgress { Pinned = null }, "ldaps://dc.corp.example.com")));
            Assert.False(bound);
        }

        [Fact]
        public void The_endpoint_settings_override_what_the_endpoint_implied()
        {
            Assert.Equal(("dc.corp.example.com", 3269, true), ActiveDirectoryConnector.ParseEndpoint("ldap://dc.corp.example.com:389", new Dictionary<string, string> { ["use_ssl"] = "true", ["port"] = "3269" }));
            Assert.Equal(("dc.corp.example.com", 636, true), ActiveDirectoryConnector.ParseEndpoint("ldaps://dc.corp.example.com/", new Dictionary<string, string>()));
        }
    }
}
