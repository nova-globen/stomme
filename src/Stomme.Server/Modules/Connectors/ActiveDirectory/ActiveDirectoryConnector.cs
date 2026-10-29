#nullable enable
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.ActiveDirectory
{
    /// <summary>
    /// The Active Directory connector: the endpoint is the
    /// directory host (<c>dc.corp.example.com</c>, <c>ldap://host:389</c>, <c>ldaps://host</c>), the Basic scheme's
    /// <c>username</c>/<c>password</c> bind (anonymous without a username), and four operations stream computers, users,
    /// sites-with-subnets or any LDAP filter as flattened records.
    /// </summary>
    /// <remarks>
    /// <b>The directory is dialled at the address the host pinned</b>: the host name goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/> with the port, and the LDAP client is built on the permitted
    /// address; with LDAPS the certificate is checked against the original name. Binding needs a reachable domain
    /// controller, so the live path is host-gated; the mapping, paging and endpoint logic run headlessly through the
    /// <see cref="IActiveDirectoryClient"/> seam.
    /// </remarks>
    public sealed class ActiveDirectoryConnector : IConnector, IConnectorExtractor
    {
        private const string UserFilter = "(&(objectCategory=person)(objectClass=user))";

        private static readonly string[] ComputerAttributes =
        {
            "distinguishedName", "name", "whenChanged", "whenCreated", "location", "operatingSystem", "operatingSystemServicePack", "operatingSystemVersion",
            "userAccountControl", "description", "accountExpires", "lastLogoff", "cn", "sAMAccountName", "lastLogonTimestamp",
        };

        private static readonly string[] UserAttributes =
        {
            "distinguishedName", "name", "whenChanged", "whenCreated", "location", "userAccountControl", "accountExpires", "lastLogoff", "cn", "mail",
            "sAMAccountName", "lastLogonTimestamp", "employeeID", "employeeNumber", "employeeType", "department", "countryCode", "co", "st", "street", "streetAddress",
        };

        private static readonly string[] SiteAttributes = { "name", "location", "description", "siteObjectBL" };

        private readonly Func<ActiveDirectoryConnection, IActiveDirectoryClient> clientFactory;

        public ActiveDirectoryConnector()
            : this(null)
        {
        }

        internal ActiveDirectoryConnector(Func<ActiveDirectoryConnection, IActiveDirectoryClient>? clientFactory) =>
            this.clientFactory = clientFactory ?? (connection => new SystemDirectoryServicesClient(connection));

        // libldap connects synchronously with the system's own timeout, whatever the connection's: the egress reaches the pin
        // first within the connection's, so a domain controller that does not answer fails in its time.
        private static Task Reach(IConnectorEgress egress, ActiveDirectoryConnection connection, CancellationToken cancellationToken) =>
            egress.ReachAsync(connection.Address, connection.Port, connection.Host, connection.Timeout, "Active Directory", cancellationToken);

        public string ConnectorType => "activedirectory";

        public string DisplayName => "Microsoft Active Directory";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        // The directory's host, ldap:// or ldaps:// - not HTTP's, so a connection string (a URL endpoint must be http or https).
        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string EndpointExample => "ldaps://dc.corp.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.MicrosoftCloudDirectory };

        public Uri? WebsiteUrl => new("https://learn.microsoft.com/windows-server/identity/ad-ds/active-directory-domain-services");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("base_dn", "Base DN", ConnectorConfigFieldType.Text, Description: "Search base (e.g. DC=corp,DC=example,DC=com). Empty means the directory's defaultNamingContext.") { Group = "Query" },
            new ConnectorConfigField("page_size", "Page size", ConnectorConfigFieldType.Integer, Description: "LDAP paged-search page size (1-1000).") { Group = "Query", Default = "500" },
            new ConnectorConfigField("use_ssl", "Use LDAPS", ConnectorConfigFieldType.Boolean, Description: "Bind over TLS (LDAPS); defaults the port to 636 when no port is given.") { Group = "Connection", Default = "false" },
            new ConnectorConfigField("port", "Port", ConnectorConfigFieldType.Integer, Description: "Directory port; defaults to 389 (or 636 with LDAPS) when omitted.") { Group = "Connection" },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("computers", "Computers", "Streams computer objects under the base DN."),
            new ConnectorOperation("users", "Users", "Streams user (person) objects under the base DN."),
            new ConnectorOperation("subnets", "Subnets", "Streams AD sites and their subnets (from CN=Sites)."),
            new ConnectorOperation("custom", "Custom query", "Runs any LDAP filter you name and streams the entries.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("filter", "LDAP filter", ConnectorConfigFieldType.Text, Required: true, Description: "An RFC 4515 filter, e.g. (&(objectClass=group)(cn=Admins*))."),
                    new ConnectorConfigField("base_dn", "Base DN override", ConnectorConfigFieldType.Text, Description: "Optional search base for this query; defaults to the connection's base DN."),
                    new ConnectorConfigField("scope", "Scope", ConnectorConfigFieldType.Select, Description: "The search scope.")
                    {
                        Default = "subtree",
                        Options = new[] { new ConnectorConfigFieldOption("subtree", "Subtree"), new ConnectorConfigFieldOption("onelevel", "One level"), new ConnectorConfigFieldOption("base", "Base") },
                    },
                    new ConnectorConfigField("attributes", "Attributes", ConnectorConfigFieldType.Text, Description: "Comma-separated attribute names to return; empty returns all attributes."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ActiveDirectoryConnection connection;
            try
            {
                connection = await BuildConnectionAsync(context.Endpoint, context.Settings, context.Credentials, context.Timeout, context.Egress, cancellationToken);
                await Reach(context.Egress, connection, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }

            try
            {
                using var client = clientFactory(connection);
                var namingContext = client.ReadRootDse("defaultNamingContext");
                var where = namingContext.Count > 0 ? $" (defaultNamingContext {namingContext[0]})" : string.Empty;
                return ConnectionTestResult.Ok($"Bound to the directory{where}.");
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var connection = await BuildConnectionAsync(context.Endpoint, context.Settings, context.Credentials, context.Timeout, context.Egress, cancellationToken);
            await Reach(context.Egress, connection, cancellationToken);
            using var client = clientFactory(connection);
            foreach (var record in Read(client, context, connection.Host, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }

        private static IEnumerable<ConnectorRecord> Read(IActiveDirectoryClient client, ConnectorExtractContext context, string adTarget, CancellationToken cancellationToken)
        {
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "computers" : context.OperationName.ToLowerInvariant();
            var pageSize = PageSize(context.Settings);
            switch (operation)
            {
                case "computers":
                    foreach (var entry in client.Search(BaseDn(client, context.Settings), "(objectCategory=computer)", AdSearchScope.Subtree, ComputerAttributes, pageSize, cancellationToken))
                    {
                        yield return ActiveDirectoryRows.Flatten(entry, adTarget);
                    }

                    break;
                case "users":
                    foreach (var entry in client.Search(BaseDn(client, context.Settings), UserFilter, AdSearchScope.Subtree, UserAttributes, pageSize, cancellationToken))
                    {
                        yield return ActiveDirectoryRows.Flatten(entry, adTarget);
                    }

                    break;
                case "subnets":
                    foreach (var record in ReadSubnets(client, adTarget, pageSize, cancellationToken))
                    {
                        yield return record;
                    }

                    break;
                case "custom":
                    foreach (var record in ReadCustom(client, context, adTarget, pageSize, cancellationToken))
                    {
                        yield return record;
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Active Directory connector.");
            }
        }

        private static IEnumerable<ConnectorRecord> ReadSubnets(IActiveDirectoryClient client, string adTarget, int pageSize, CancellationToken cancellationToken)
        {
            var configNc = client.ReadRootDse("configurationNamingContext").FirstOrDefault()
                ?? throw new InvalidOperationException("The directory did not return a configurationNamingContext, so the subnets operation cannot locate CN=Sites.");
            var (flat, qualified) = ActiveDirectoryRows.DomainFromDn(configNc);
            foreach (var site in client.Search($"CN=Sites,{configNc}", "(objectClass=site)", AdSearchScope.OneLevel, SiteAttributes, pageSize, cancellationToken))
            {
                var name = First(site, "name");
                var location = First(site, "location");
                var description = First(site, "description");
                var subnets = site.Attributes.TryGetValue("siteObjectBL", out var backlinks) ? backlinks.Select(SubnetName).ToList() : new List<string>();
                if (subnets.Count == 0)
                {
                    yield return SubnetRecord(flat, qualified, name, location, description, string.Empty, adTarget);
                    continue;
                }

                foreach (var subnet in subnets)
                {
                    yield return SubnetRecord(flat, qualified, name, location, description, subnet, adTarget);
                }
            }
        }

        private static IEnumerable<ConnectorRecord> ReadCustom(IActiveDirectoryClient client, ConnectorExtractContext context, string adTarget, int pageSize, CancellationToken cancellationToken)
        {
            var filter = Value(context.Parameters, "filter") ?? throw new InvalidOperationException("The custom operation requires a 'filter' parameter.");
            var baseDn = Value(context.Parameters, "base_dn") ?? BaseDn(client, context.Settings);
            var scope = ParseScope(Value(context.Parameters, "scope"));
            var attributes = SplitCsv(Value(context.Parameters, "attributes"));
            foreach (var entry in client.Search(baseDn, filter, scope, attributes, pageSize, cancellationToken))
            {
                yield return ActiveDirectoryRows.Flatten(entry, adTarget);
            }
        }

        private static ConnectorRecord SubnetRecord(string flat, string qualified, string name, string location, string description, string subnet, string adTarget) =>
            new(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["FlatDomain"] = flat,
                ["QualifiedDomain"] = qualified,
                ["Name"] = name,
                ["Location"] = location,
                ["Description"] = description,
                ["Subnets"] = subnet,
                ["ADTarget"] = adTarget,
            });

        private static string SubnetName(string distinguishedName)
        {
            var first = distinguishedName.Split(',', 2)[0].Trim();
            return first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? first["CN=".Length..] : first;
        }

        private static string BaseDn(IActiveDirectoryClient client, IReadOnlyDictionary<string, string> settings) =>
            Value(settings, "base_dn")
            ?? client.ReadRootDse("defaultNamingContext").FirstOrDefault()
            ?? throw new InvalidOperationException("No base DN: set 'base_dn' or ensure the directory exposes a defaultNamingContext.");

        /// <summary>The endpoint and settings parsed, then the host pinned through the egress - the connection dials the address, names the host.</summary>
        private static async Task<ActiveDirectoryConnection> BuildConnectionAsync(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials,
            TimeSpan timeout, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var (host, port, ssl) = ParseEndpoint(endpoint, settings);
            var address = await egress.ResolveAndPinAsync(host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The directory host '{host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            credentials.TryGetValue("username", out var username);
            credentials.TryGetValue("password", out var password);
            return new ActiveDirectoryConnection(host, address, port, ssl, string.IsNullOrWhiteSpace(username) ? null : username, string.IsNullOrEmpty(password) ? null : password, timeout);
        }

        internal static (string Host, int Port, bool Ssl) ParseEndpoint(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The Active Directory connection needs its directory host as the endpoint (e.g. dc.corp.example.com).");
            }

            var value = endpoint.Trim();
            var ssl = false;
            int? explicitPort = null;
            if (value.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase))
            {
                ssl = true;
                value = value["ldaps://".Length..];
            }
            else if (value.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase))
            {
                value = value["ldap://".Length..];
            }

            value = value.TrimEnd('/');
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
            {
                explicitPort = parsedPort;
                value = value[..colon];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"The endpoint '{endpoint}' does not contain a directory host.");
            }

            if (Value(settings, "use_ssl") is { } useSsl && bool.TryParse(useSsl, out var flag) && flag)
            {
                ssl = true;
            }

            if (Value(settings, "port") is { } portSetting && int.TryParse(portSetting, NumberStyles.Integer, CultureInfo.InvariantCulture, out var settingPort))
            {
                explicitPort = settingPort;
            }

            return (value, explicitPort ?? (ssl ? 636 : 389), ssl);
        }

        private static int PageSize(IReadOnlyDictionary<string, string> settings) =>
            Value(settings, "page_size") is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? Math.Clamp(size, 1, 1000) : 500;

        private static AdSearchScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
        {
            "base" => AdSearchScope.Base,
            "onelevel" or "one" => AdSearchScope.OneLevel,
            _ => AdSearchScope.Subtree,
        };

        private static IReadOnlyList<string> SplitCsv(string? value) =>
            string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string First(AdEntry entry, string attribute) => entry.Attributes.TryGetValue(attribute, out var values) && values.Count > 0 ? values[0] : string.Empty;

        private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        private static bool IsExpectedFailure(Exception ex) => ex is DirectoryException or InvalidOperationException or ArgumentException or TimeoutException;
    }
}
