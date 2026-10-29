#nullable enable
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Ldap;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The LDAP connector's live tests' directory: LDAP_TEST_HOST / PORT / PASSWORD where set, otherwise this machine's
    /// throwaway OpenLDAP that <c>scripts/ldap-test-server.ps1</c> makes on 127.0.0.1:13389 - its tree dc=kafe,dc=example, its
    /// admin cn=admin and its password read from the file that script writes under the user's temp folder. No password is a
    /// FAILURE that says how to get one, never a skip.
    /// </summary>
    internal static class LdapTestEndpoint
    {
        public const string Root = "dc=kafe,dc=example";

        public const string Admin = "cn=admin," + Root;

        public static string Host => Environment.GetEnvironmentVariable("LDAP_TEST_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

        public static int Port => int.TryParse(Environment.GetEnvironmentVariable("LDAP_TEST_PORT"), out var port) ? port : 13389;

        public static string Password
        {
            get
            {
                if (Environment.GetEnvironmentVariable("LDAP_TEST_PASSWORD") is { Length: > 0 } given)
                {
                    return given;
                }

                var file = Path.Combine(Path.GetTempPath(), "stomme-ldap-test", "password");
                return File.Exists(file)
                    ? File.ReadAllText(file).Trim()
                    : throw new InvalidOperationException($"No LDAP test directory: run 'pwsh scripts/ldap-test-server.ps1' (a throwaway OpenLDAP on 127.0.0.1:13389, its password in {file}) or set LDAP_TEST_HOST, LDAP_TEST_PORT and LDAP_TEST_PASSWORD.");
            }
        }

        /// <summary>An admin session straight to the directory, for a test's own setting up and tearing down.</summary>
        public static LdapConnection AdminConnection()
        {
            var connection = new LdapConnection(new LdapDirectoryIdentifier(Host, Port)) { AuthType = AuthType.Basic, Credential = new NetworkCredential(Admin, Password), Timeout = TimeSpan.FromSeconds(15) };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Bind();
            return connection;
        }

        /// <summary>An entry and everything under it, deepest first; an entry already gone is nothing to do.</summary>
        public static void DeleteTree(string dn)
        {
            using var connection = AdminConnection();
            List<string> found;
            try
            {
                found = ((SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Subtree, "1.1"))).Entries.Cast<SearchResultEntry>().Select(entry => entry.DistinguishedName).ToList();
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                return;
            }

            foreach (var entry in found.OrderByDescending(entry => entry.Count(c => c == ',')))
            {
                connection.SendRequest(new DeleteRequest(entry));
            }
        }
    }

    /// <summary>
    /// The LDAP connector without a directory, through its client seam: an address that is not LDAP's, or carries a
    /// user, a path or a query, refused, and LDAPS with StartTLS; the host pinned on its port and LDAP's own 389 and 636, a
    /// refused host the guard's own denial, the session handed the host as named and the address pinned; every parameter held
    /// to its shape - a DN, a filter, a scope, an attribute's name, a JSON object of attributes, objectClass on a create - before
    /// anything is dialled; search, compare, create, update, rename and delete each sent as asked and answered as records; a
    /// directory's refusal said.
    /// </summary>
    public sealed class LdapConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "cn=n8n,ou=services,dc=kafe,dc=example", ["password"] = "pw-1" };

        private sealed class FakeClient : ILdapClient
        {
            public List<string> Calls { get; } = new();

            public List<(string Base, string Filter, SearchScope Scope, IReadOnlyList<string> Attributes, int PageSize, int Max)> Searches { get; } = new();

            public List<LdapEntry> Entries { get; } = new();

            public bool Matches { get; set; }

            public (string Dn, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Attributes)? Added { get; private set; }

            public (string Dn, IReadOnlyList<(DirectoryAttributeOperation Operation, string Name, IReadOnlyList<string> Values)> Changes)? Modified { get; private set; }

            public (string Dn, string Rdn, string? Parent)? Renamed { get; private set; }

            public Exception? Fails { get; set; }

            public bool Disposed { get; private set; }

            public IReadOnlyList<string> NamingContexts() => new[] { "dc=kafe,dc=example" };

            public IEnumerable<LdapEntry> Search(string baseDn, string filter, SearchScope scope, IReadOnlyList<string> attributes, int pageSize, int max, CancellationToken cancellationToken)
            {
                Searches.Add((baseDn, filter, scope, attributes, pageSize, max));
                Throw();
                return Entries; // more than the most, where a test holds more: the connector holds to it
            }

            public bool Compare(string dn, string attribute, string value)
            {
                Calls.Add($"compare {dn} {attribute} {value}");
                Throw();
                return Matches;
            }

            public void Add(string dn, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> attributes)
            {
                Throw();
                Added = (dn, attributes);
            }

            public void Modify(string dn, IReadOnlyList<(DirectoryAttributeOperation Operation, string Name, IReadOnlyList<string> Values)> changes)
            {
                Throw();
                Modified = (dn, changes);
            }

            public void Rename(string dn, string newRdn, string? newParent)
            {
                Throw();
                Renamed = (dn, newRdn, newParent);
            }

            public void Delete(string dn)
            {
                Throw();
                Calls.Add($"delete {dn}");
            }

            public void Dispose() => Disposed = true;

            private void Throw()
            {
                if (Fails is not null)
                {
                    throw Fails;
                }
            }
        }

        private static (LdapConnector Connector, Func<LdapSettings?> Settings) Build(FakeClient client)
        {
            LdapSettings? captured = null;
            return (new LdapConnector(settings =>
            {
                captured = settings;
                return client;
            }), () => captured);
        }

        private static Task<List<ConnectorRecord>> Run(FakeClient client, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "ldap://dir.kafe.example") =>
            Contexts.DrainAsync(Build(client).Connector, Contexts.Extract(new StubEgress(), endpoint, operation, parameters, settings, Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_ldap_node()
        {
            IConnector connector = new LdapConnector();
            Assert.Equal(("ldap", "LDAP", AuthenticationType.Basic, ConnectorEndpointKind.ConnectionString), (connector.ConnectorType, connector.DisplayName, connector.AuthType, connector.EndpointKind));
            Assert.Equal(new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.MicrosoftCloudDirectory }, connector.Categories);
            Assert.Equal(new[] { "search", "compare", "create", "update", "rename", "delete" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
            Assert.Equal(new[] { "start_tls", "base_dn", "page_size" }, connector.ConfigSchema.Fields.Select(field => field.Name));
        }

        [Fact]
        public async Task An_address_that_is_not_ldaps_is_refused_before_any_pin()
        {
            var egress = new StubEgress();
            foreach (var (endpoint, settings, refusal) in new[]
            {
                ("", null, "Give the directory's address"),
                ("http://dir.kafe.example", null, "is not a directory's address - ldap://"),
                ("ldap://ada:pw@dir.kafe.example", null, "is not a directory's address alone"),
                ("ldap://dir.kafe.example/dc=kafe,dc=example", null, "is not a directory's address alone"),
                ("ldap://dir.kafe.example?one", null, "is not a directory's address alone"),
                ("ldaps://dir.kafe.example", new Dictionary<string, string> { ["start_tls"] = "true" }, "Give ldaps:// or StartTLS, not both"),
            })
            {
                var result = await Build(new FakeClient()).Connector.TestConnectionAsync(Contexts.Test(egress, endpoint, settings, Credentials));
                Assert.False(result.Success);
                Assert.Contains(refusal, result.Message, StringComparison.Ordinal);
            }

            Assert.Empty(egress.Pins);
        }

        [Fact]
        public async Task The_host_is_pinned_and_the_session_dials_the_pin_under_the_name_it_was_given()
        {
            var refused = new StubEgress { Pinned = null };
            var denied = await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => Build(new FakeClient()).Connector.TestConnectionAsync(Contexts.Test(refused, "dir.kafe.example", credentials: Credentials)));
            Assert.Contains("dir.kafe.example", denied.Message, StringComparison.Ordinal);
            Assert.Equal(("dir.kafe.example", 389), refused.Pins.Single()); // no scheme: ldap://, LDAP's own port

            foreach (var (endpoint, settings, port, ssl, startTls) in new[]
            {
                ("ldap://dir.kafe.example", (Dictionary<string, string>?)null, 389, false, false),
                ("ldaps://dir.kafe.example", null, 636, true, false),
                ("ldap://dir.kafe.example:1389", new Dictionary<string, string> { ["start_tls"] = "true" }, 1389, false, true),
            })
            {
                var egress = new StubEgress { Pinned = IPAddress.Parse("10.0.0.7") };
                var client = new FakeClient();
                var (connector, captured) = Build(client);
                var tested = await connector.TestConnectionAsync(Contexts.Test(egress, endpoint, settings, Credentials, timeoutSeconds: 12));
                Assert.True(tested.Success, tested.Message);
                Assert.Equal("LDAP: bound to dir.kafe.example as cn=n8n,ou=services,dc=kafe,dc=example - dc=kafe,dc=example.", tested.Message);
                Assert.Equal(("dir.kafe.example", port), egress.Pins.Single());
                Assert.Equal(new LdapSettings("dir.kafe.example", IPAddress.Parse("10.0.0.7"), port, ssl, startTls, "cn=n8n,ou=services,dc=kafe,dc=example", "pw-1", TimeSpan.FromSeconds(12)), captured());
                Assert.True(client.Disposed);
            }

            var anonymous = Build(new FakeClient());
            var bound = await anonymous.Connector.TestConnectionAsync(Contexts.Test(new StubEgress(), "ldap://dir.kafe.example", credentials: new Dictionary<string, string> { ["password"] = "unused" }));
            Assert.Equal("LDAP: bound to dir.kafe.example anonymously - dc=kafe,dc=example.", bound.Message);
            Assert.Null(anonymous.Settings()!.Password); // no user: no password sent either
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_dialled()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null)
            {
                var egress = new StubEgress();
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new LdapConnector(_ => throw new InvalidOperationException("dialled")), Contexts.Extract(egress, "ldap://dir.kafe.example", operation, parameters, settings, Credentials)));
                Assert.Empty(egress.Pins);
                return refusal.Message;
            }

            const string Ada = "cn=Ada,ou=people,dc=kafe,dc=example";
            Assert.Equal("LDAP has no operation 'modify' - search, compare, create, update, rename or delete.", await Refused("modify", new()));
            Assert.Equal("Give where the search starts (base), or the connection's base DN.", await Refused("search", new()));
            Assert.Equal("'people' is not a distinguished name - cn=Ada,ou=people,dc=kafe,dc=example (base).", await Refused("search", new() { ["base"] = "people" }));
            Assert.Equal("'cn=Ada\nou=x' is not a distinguished name - cn=Ada,ou=people,dc=kafe,dc=example (dn).", await Refused("delete", new() { ["dn"] = "cn=Ada\nou=x" }));
            Assert.Equal("Give the distinguished name - cn=Ada,ou=people,dc=kafe,dc=example (dn).", await Refused("delete", new()));
            Assert.Equal("'mail=*' is not an LDAP filter - (objectClass=person), in parentheses (filter).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["filter"] = "mail=*" }));
            Assert.Equal("'(&(mail=*)' is not an LDAP filter - its parentheses do not pair (filter).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["filter"] = "(&(mail=*)" }));
            Assert.Equal("'(mail=*))(cn=*)' is not an LDAP filter - its parentheses do not pair (filter).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["filter"] = "(mail=*))(cn=*)" }));
            Assert.Equal("'(mail=*))((cn=*)' is not an LDAP filter - its parentheses do not pair (filter).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["filter"] = "(mail=*))((cn=*)" })); // balanced at its end, not on the way
            Assert.Equal("'tree' is not sub, one or base (scope).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["scope"] = "tree" }));
            Assert.Equal("'mail)(cn' is not an attribute's name - mail, cn, objectClass (attributes).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["attributes"] = "cn, mail)(cn" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example", ["max"] = "0" }));
            Assert.Equal("'1001' is not a number from 1 to 1000 (page_size).", await Refused("search", new() { ["base"] = "dc=kafe,dc=example" }, new() { ["page_size"] = "1001" }));
            Assert.Equal("Give the attribute's name (attribute).", await Refused("compare", new() { ["dn"] = Ada, ["value"] = "x" }));
            Assert.Equal("Give the value it is compared with (value).", await Refused("compare", new() { ["dn"] = Ada, ["attribute"] = "mail" }));
            Assert.Equal("Give its attributes - a JSON object, objectClass among them (attributes).", await Refused("create", new() { ["dn"] = Ada }));
            Assert.Equal("The attributes is not a JSON object of names and values - {\"mail\": \"ada@kafe.example\"}.", await Refused("create", new() { ["dn"] = Ada, ["attributes"] = "[\"cn\"]" }));
            Assert.Equal("Give its objectClass among its attributes - {\"objectClass\": [\"inetOrgPerson\"], ...}.", await Refused("create", new() { ["dn"] = Ada, ["attributes"] = "{\"cn\": \"Ada\"}" }));
            Assert.Equal("'sn' in the attributes has no value.", await Refused("create", new() { ["dn"] = Ada, ["attributes"] = "{\"objectClass\": \"person\", \"sn\": \"\"}" }));
            Assert.Equal("'manager' in the attributes is an object - give a value or a list of values.", await Refused("create", new() { ["dn"] = Ada, ["attributes"] = "{\"objectClass\": \"person\", \"manager\": {\"cn\": \"Bo\"}}" }));
            Assert.Equal("The attributes names no attribute.", await Refused("create", new() { ["dn"] = Ada, ["attributes"] = "{}" }));
            Assert.Equal("'tele phone' is not an attribute's name - mail, cn, objectClass (replace).", await Refused("update", new() { ["dn"] = Ada, ["replace"] = "{\"tele phone\": \"1\"}" }));
            Assert.Equal("'mail' in the add has no value.", await Refused("update", new() { ["dn"] = Ada, ["add"] = "{\"mail\": []}" }));
            Assert.Equal("Give what changes - add, replace or remove.", await Refused("update", new() { ["dn"] = Ada }));
            Assert.Equal("Give the distinguished name - cn=Ada,ou=people,dc=kafe,dc=example (to).", await Refused("rename", new() { ["dn"] = Ada }));
        }

        [Fact]
        public async Task A_search_is_sent_as_asked_and_each_entry_is_its_dn_and_attributes()
        {
            var client = new FakeClient();
            client.Entries.Add(new LdapEntry("cn=Ada,ou=people,dc=kafe,dc=example", new (string, IReadOnlyList<string>)[] { ("cn", new[] { "Ada" }), ("mail", new[] { "ada@kafe.example", "ada@kafe.example" }) }));
            client.Entries.Add(new LdapEntry("cn=Bo,ou=people,dc=kafe,dc=example", new (string, IReadOnlyList<string>)[] { ("cn", new[] { "Bo" }), ("description", new[] { "\"Café\" & bar" }) }));
            var records = await Run(client, "search", new() { ["filter"] = " (&(objectClass=person)(mail=*)) ", ["scope"] = "ONE", ["attributes"] = "cn, mail", ["max"] = "2" }, new() { ["base_dn"] = "ou=people,dc=kafe,dc=example", ["page_size"] = "100" });
            Assert.Equal(("ou=people,dc=kafe,dc=example", "(&(objectClass=person)(mail=*))", SearchScope.OneLevel, 100, 2), (client.Searches[0].Base, client.Searches[0].Filter, client.Searches[0].Scope, client.Searches[0].PageSize, client.Searches[0].Max));
            Assert.Equal(new[] { "cn", "mail" }, client.Searches[0].Attributes);
            Assert.Equal(new Dictionary<string, object?> { ["dn"] = "cn=Ada,ou=people,dc=kafe,dc=example", ["cn"] = "Ada", ["mail"] = "[\"ada@kafe.example\",\"ada@kafe.example\"]" }, records[0].Fields);
            Assert.Equal("\"Café\" & bar", records[1].Fields["description"]); // one value: itself, never JSON
            Assert.Equal("cn=Ada,ou=people,dc=kafe,dc=example", Assert.Single(await Run(client, "search", new() { ["max"] = "1" }, new() { ["base_dn"] = "ou=people,dc=kafe,dc=example" })).Fields["dn"]); // held to its most

            var defaults = new FakeClient();
            await Run(defaults, "search", new() { ["base"] = "dc=kafe,dc=example" });
            Assert.Equal(("dc=kafe,dc=example", "(objectClass=*)", SearchScope.Subtree, 500, LdapConnector.DefaultMax), (defaults.Searches[0].Base, defaults.Searches[0].Filter, defaults.Searches[0].Scope, defaults.Searches[0].PageSize, defaults.Searches[0].Max));
            Assert.Empty(defaults.Searches[0].Attributes); // every attribute
            await Run(defaults, "search", new() { ["base"] = "dc=kafe,dc=example", ["scope"] = "base" }, new() { ["base_dn"] = "ou=ignored,dc=kafe,dc=example" });
            Assert.Equal(("dc=kafe,dc=example", SearchScope.Base), (defaults.Searches[1].Base, defaults.Searches[1].Scope)); // the step's base before the connection's
        }

        [Fact]
        public async Task Compare_create_update_rename_and_delete_are_sent_as_asked()
        {
            const string Ada = "cn=Ada,ou=people,dc=kafe,dc=example";
            var client = new FakeClient { Matches = true };
            var compared = Assert.Single(await Run(client, "compare", new() { ["dn"] = Ada, ["attribute"] = "mail", ["value"] = " ada@kafe.example " })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["dn"] = Ada, ["attribute"] = "mail", ["value"] = " ada@kafe.example ", ["matches"] = true }, compared); // a value compared as given
            client.Matches = false;
            Assert.False((bool)Assert.Single(await Run(client, "compare", new() { ["dn"] = Ada, ["attribute"] = "mail", ["value"] = "bo@kafe.example" })).Fields["matches"]!);

            var created = Assert.Single(await Run(client, "create", new() { ["dn"] = Ada, ["attributes"] = "{\"objectClass\": [\"inetOrgPerson\", \"top\"], \"cn\": \"Ada\", \"sn\": \"Kafé\", \"employeeNumber\": 1042}" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["dn"] = Ada, ["created"] = true }, created);
            Assert.Equal(Ada, client.Added!.Value.Dn);
            Assert.Equal(new[] { "objectClass=inetOrgPerson|top", "cn=Ada", "sn=Kafé", "employeeNumber=1042" }, client.Added.Value.Attributes.Select(attribute => attribute.Name + "=" + string.Join("|", attribute.Values)));

            var updated = Assert.Single(await Run(client, "update", new() { ["dn"] = Ada, ["add"] = "{\"mail\": [\"ada@kafe.example\", \"ada@kafe.example\"]}", ["replace"] = "{\"title\": \"Barista\", \"description\": []}", ["remove"] = "{\"telephoneNumber\": \"\", \"mail\": \"old@kafe.example\"}" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["dn"] = Ada, ["added"] = 1, ["replaced"] = 2, ["removed"] = 2 }, updated);
            Assert.Equal(new[] { "Add mail=ada@kafe.example|ada@kafe.example", "Replace title=Barista", "Replace description=", "Delete telephoneNumber=", "Delete mail=old@kafe.example" },
                client.Modified!.Value.Changes.Select(change => $"{change.Operation} {change.Name}={string.Join("|", change.Values)}")); // an empty remove takes every value

            var renamed = Assert.Single(await Run(client, "rename", new() { ["dn"] = Ada, ["to"] = "cn=Ada Lovelace,OU=People,dc=kafe,dc=example" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["dn"] = "cn=Ada Lovelace,OU=People,dc=kafe,dc=example", ["from"] = Ada, ["moved"] = false }, renamed);
            Assert.Equal((Ada, "cn=Ada Lovelace", (string?)null), client.Renamed); // the same parent, whatever its case: renamed, not moved
            var moved = Assert.Single(await Run(client, "rename", new() { ["dn"] = Ada, ["to"] = "cn=Ada,ou=alumni,dc=kafe,dc=example" })).Fields;
            Assert.True((bool)moved["moved"]!);
            Assert.Equal((Ada, "cn=Ada", "ou=alumni,dc=kafe,dc=example"), client.Renamed);
            Assert.Equal(("cn=Kafé\\, Ada", "ou=people,dc=kafe,dc=example"), LdapConnector.Split("cn=Kafé\\, Ada,ou=people,dc=kafe,dc=example")); // an escaped comma is the RDN's own

            Assert.Equal(new Dictionary<string, object?> { ["dn"] = Ada, ["deleted"] = true }, Assert.Single(await Run(client, "delete", new() { ["dn"] = Ada })).Fields);
            Assert.Equal($"delete {Ada}", client.Calls[^1]);
        }

        [Fact]
        public async Task A_directorys_refusal_is_said_with_its_message()
        {
            var client = new FakeClient { Fails = new LdapException(49, "The supplied credential is invalid.", "data 52e") };
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(client, "delete", new() { ["dn"] = "cn=Ada,dc=kafe,dc=example" }));
            Assert.Equal("LDAP: The supplied credential is invalid. - data 52e", refusal.Message);

            var unbound = await new LdapConnector(_ => throw new LdapException(81, "The LDAP server is unavailable.")).TestConnectionAsync(Contexts.Test(new StubEgress(), "ldap://dir.kafe.example", credentials: Credentials));
            Assert.Equal((false, "LDAP: The LDAP server is unavailable."), (unbound.Success, unbound.Message));
            var missing = await new LdapConnector(_ => throw new DllNotFoundException("libldap.so.2")).TestConnectionAsync(Contexts.Test(new StubEgress(), "ldap://dir.kafe.example", credentials: Credentials));
            Assert.Equal((false, "This server cannot speak LDAP: its LDAP library did not load (on Linux the libldap package) - libldap.so.2"), (missing.Success, missing.Message));
        }
    }

    /// <summary>
    /// The LDAP connector against a real OpenLDAP 2.4 (<c>scripts/ldap-test-server.ps1</c>'s throwaway directory on
    /// loopback) through the HOST's egress: loopback refused until allowlisted; a test that binds and names the naming context, a
    /// wrong password refused; the session dialling the pinned address and never the name; an organisational unit and people
    /// created, searched by a filter in each scope with the attributes asked for, compared, updated - values added, replaced and
    /// removed - renamed and moved, and deleted, with the directory's own refusals said (an entry that exists, one that does not, a
    /// unit with people in it); a search paged past the page size and held to its most. Fails, never skips, when the directory is
    /// absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class LdapConnectorLiveTests(ITestOutputHelper output)
    {
        private static string Endpoint => $"ldap://{LdapTestEndpoint.Host}:{LdapTestEndpoint.Port}";

        private static Dictionary<string, string> Credentials => new() { ["username"] = LdapTestEndpoint.Admin, ["password"] = LdapTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { LdapTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null) =>
            Contexts.DrainAsync(new LdapConnector(), Contexts.Extract(Egress, Endpoint, operation, parameters, settings, Credentials, timeoutSeconds: 15));

        [Fact]
        public async Task A_test_binds_and_a_wrong_password_is_refused()
        {
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new LdapConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), Endpoint, credentials: Credentials, timeoutSeconds: 15)));
            var right = await new LdapConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: Credentials, timeoutSeconds: 15));
            output.WriteLine($"[live] {right.Success}: {right.Message}");
            Assert.True(right.Success, right.Message);
            Assert.EndsWith(" - dc=kafe,dc=example.", right.Message, StringComparison.Ordinal);
            var wrong = await new LdapConnector().TestConnectionAsync(Contexts.Test(Egress, Endpoint, credentials: new Dictionary<string, string> { ["username"] = LdapTestEndpoint.Admin, ["password"] = "not-the-password" }, timeoutSeconds: 15));
            output.WriteLine($"[live] {wrong.Success}: {wrong.Message}");
            Assert.False(wrong.Success);
            Assert.StartsWith("LDAP: ", wrong.Message, StringComparison.Ordinal);

            // A name that resolves nowhere, pinned to the directory: the session dials the address, never the name.
            var pinned = new StubEgress { Pinned = IPAddress.Parse(LdapTestEndpoint.Host) };
            var byPin = await new LdapConnector().TestConnectionAsync(Contexts.Test(pinned, $"ldap://kafe-directory.invalid:{LdapTestEndpoint.Port}", credentials: Credentials, timeoutSeconds: 15));
            Assert.True(byPin.Success, byPin.Message);
            Assert.Equal(("kafe-directory.invalid", LdapTestEndpoint.Port), pinned.Pins.Single());
        }

        [Fact]
        public async Task People_are_created_searched_compared_updated_renamed_moved_and_deleted()
        {
            var unit = $"ou=run-{Guid.NewGuid().ToString("N")[..8]},{LdapTestEndpoint.Root}";
            var ada = $"cn=Ada Kafé,{unit}";
            try
            {
                Assert.True((bool)Assert.Single(await Run("create", new() { ["dn"] = unit, ["attributes"] = "{\"objectClass\": \"organizationalUnit\"}" })).Fields["created"]!);
                await Run("create", new() { ["dn"] = $"ou=alumni,{unit}", ["attributes"] = "{\"objectClass\": [\"organizationalUnit\"], \"description\": \"Former staff\"}" });
                await Run("create", new() { ["dn"] = ada, ["attributes"] = "{\"objectClass\": [\"inetOrgPerson\"], \"cn\": [\"Ada Kafé\", \"Ada\"], \"sn\": \"Kafé\", \"mail\": \"ada@kafe.example\", \"employeeNumber\": 1042}" });
                await Run("create", new() { ["dn"] = $"cn=Bo,{unit}", ["attributes"] = "{\"objectClass\": \"inetOrgPerson\", \"sn\": \"Barista\"}" });
                var again = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("create", new() { ["dn"] = ada, ["attributes"] = "{\"objectClass\": \"inetOrgPerson\", \"sn\": \"Kafé\"}" }));
                output.WriteLine($"[live] {again.Message}");
                Assert.StartsWith("LDAP answered EntryAlreadyExists", again.Message, StringComparison.Ordinal);

                var found = Assert.Single(await Run("search", new() { ["base"] = unit, ["filter"] = "(&(objectClass=inetOrgPerson)(mail=*))", ["attributes"] = "cn,mail,employeeNumber" })).Fields;
                Assert.Equal(ada, found["dn"]);
                Assert.Equal(new[] { "Ada Kafé", "Ada" }, JsonNode.Parse((string)found["cn"]!)!.AsArray().Select(value => value!.GetValue<string>())); // several values: a JSON array
                Assert.Equal(("ada@kafe.example", "1042"), ((string?)found["mail"], (string?)found["employeeNumber"]));
                Assert.Equal(new[] { "dn", "cn", "employeeNumber", "mail" }, found.Keys); // only what was asked for
                Assert.Equal(new[] { $"ou=alumni,{unit}", $"cn=Ada Kafé,{unit}", $"cn=Bo,{unit}" }.OrderBy(dn => dn), (await Run("search", new() { ["base"] = unit, ["scope"] = "one", ["attributes"] = "objectClass" })).Select(record => (string)record.Fields["dn"]!).OrderBy(dn => dn));
                Assert.Equal(unit, (string)Assert.Single(await Run("search", new() { ["base"] = unit, ["scope"] = "base" })).Fields["dn"]!);
                Assert.Equal(4, (await Run("search", new() { ["filter"] = "(objectClass=*)" }, new() { ["base_dn"] = unit })).Count); // the connection's base, the whole subtree

                Assert.True((bool)Assert.Single(await Run("compare", new() { ["dn"] = ada, ["attribute"] = "mail", ["value"] = "ada@kafe.example" })).Fields["matches"]!);
                Assert.False((bool)Assert.Single(await Run("compare", new() { ["dn"] = ada, ["attribute"] = "mail", ["value"] = "bo@kafe.example" })).Fields["matches"]!);

                await Run("update", new() { ["dn"] = ada, ["add"] = "{\"telephoneNumber\": [\"+46 8 123\", \"+46 8 456\"]}", ["replace"] = "{\"mail\": \"ada@kafe.example\", \"title\": \"Barista\"}" });
                await Run("update", new() { ["dn"] = ada, ["remove"] = "{\"telephoneNumber\": \"+46 8 123\", \"employeeNumber\": \"\"}" });
                var changed = Assert.Single(await Run("search", new() { ["base"] = ada, ["scope"] = "base", ["attributes"] = "mail,title,telephoneNumber,employeeNumber" })).Fields;
                Assert.Equal(("ada@kafe.example", "Barista", "+46 8 456", false), ((string?)changed["mail"], (string?)changed["title"], (string?)changed["telephoneNumber"], changed.ContainsKey("employeeNumber")));

                var renamed = Assert.Single(await Run("rename", new() { ["dn"] = ada, ["to"] = $"cn=Ada Lovelace,{unit}" })).Fields;
                Assert.False((bool)renamed["moved"]!);
                var moved = Assert.Single(await Run("rename", new() { ["dn"] = $"cn=Ada Lovelace,{unit}", ["to"] = $"cn=Ada Lovelace,ou=alumni,{unit}" })).Fields;
                Assert.True((bool)moved["moved"]!);
                var lovelace = Assert.Single(await Run("search", new() { ["base"] = $"ou=alumni,{unit}", ["filter"] = "(cn=Ada*)", ["attributes"] = "cn,sn" })).Fields;
                Assert.Equal(($"cn=Ada Lovelace,ou=alumni,{unit}", "Kafé"), ((string?)lovelace["dn"], (string?)lovelace["sn"]));
                Assert.Contains("Ada Lovelace", (string)lovelace["cn"]!, StringComparison.Ordinal); // the old RDN value replaced by the new

                var nonLeaf = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("delete", new() { ["dn"] = $"ou=alumni,{unit}" }));
                output.WriteLine($"[live] {nonLeaf.Message}");
                Assert.StartsWith("LDAP answered NotAllowedOnNonLeaf", nonLeaf.Message, StringComparison.Ordinal);
                Assert.True((bool)Assert.Single(await Run("delete", new() { ["dn"] = $"cn=Ada Lovelace,ou=alumni,{unit}" })).Fields["deleted"]!);
                var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("delete", new() { ["dn"] = $"cn=Ada Lovelace,ou=alumni,{unit}" }));
                Assert.StartsWith("LDAP answered NoSuchObject", gone.Message, StringComparison.Ordinal);
            }
            finally
            {
                LdapTestEndpoint.DeleteTree(unit);
            }
        }

        [Fact]
        public async Task A_search_pages_past_its_page_size_and_stops_at_its_most()
        {
            var unit = $"ou=run-{Guid.NewGuid().ToString("N")[..8]},{LdapTestEndpoint.Root}";
            try
            {
                await Run("create", new() { ["dn"] = unit, ["attributes"] = "{\"objectClass\": \"organizationalUnit\"}" });
                for (var i = 1; i <= 7; i++)
                {
                    await Run("create", new() { ["dn"] = $"cn=Gäst {i},{unit}", ["attributes"] = $"{{\"objectClass\": \"inetOrgPerson\", \"sn\": \"Gäst\", \"roomNumber\": {i}}}" });
                }

                var paged = new Dictionary<string, string> { ["page_size"] = "3" };
                var all = await Run("search", new() { ["base"] = unit, ["filter"] = "(sn=Gäst)", ["attributes"] = "roomNumber" }, paged);
                Assert.Equal(Enumerable.Range(1, 7).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), all.Select(record => (string)record.Fields["roomNumber"]!).OrderBy(room => room));
                Assert.Equal(5, (await Run("search", new() { ["base"] = unit, ["filter"] = "(sn=Gäst)", ["max"] = "5" }, paged)).Count);
                Assert.Equal(2, (await Run("search", new() { ["base"] = unit, ["filter"] = "(sn=Gäst)", ["max"] = "2" }, paged)).Count);
            }
            finally
            {
                LdapTestEndpoint.DeleteTree(unit);
            }
        }
    }
}
