#nullable enable
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Ldap
{
    /// <summary>
    /// LDAP (BL-229, n8n's LDAP node: a directory's entries searched, compared, created, updated, renamed and deleted) over
    /// System.DirectoryServices.Protocols - any LDAPv3 directory, OpenLDAP, 389 Directory Server, Active Directory - bound by a
    /// DN and its password (the Basic scheme; anonymous without a user), over the host's egress. A test binds and names the
    /// directory's naming contexts. (The Active Directory connector reads AD's own shapes; this one is the directory's own.)
    /// </summary>
    /// <remarks>
    /// <para><c>search</c> (a base, a filter, a scope, the attributes asked for - n8n's Search, paged up to <c>max</c>), each
    /// entry its <c>dn</c> and its attributes - a single value as itself, several as a JSON array, a binary one as base64;
    /// <c>compare</c> (whether an entry's attribute holds a value), <c>create</c> (an entry and its attributes, a JSON object of
    /// names and a value or values), <c>update</c> (values added, replaced and removed - n8n's Update), <c>rename</c> (to a new
    /// DN: its RDN and, when it moves, its parent) and <c>delete</c>.</para>
    /// <para><b>The directory is dialled at the address the host pinned</b> - the host name through the egress with its port -
    /// and with LDAPS or StartTLS the certificate is checked against the original name. A DN, a filter and an attribute's name
    /// are held to their shape before anything is dialled. The directory's refusals said with LDAP's result and its message.</para>
    /// </remarks>
    public sealed class LdapConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50;

        private static readonly string[] Known = { "search", "compare", "create", "update", "rename", "delete" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly Func<LdapSettings, ILdapClient> clientFactory;

        public LdapConnector()
            : this(null)
        {
        }

        internal LdapConnector(Func<LdapSettings, ILdapClient>? clientFactory) =>
            this.clientFactory = clientFactory ?? (settings => new SystemLdapClient(settings));

        // libldap connects synchronously with the system's own timeout, whatever the connection's: the egress reaches the pin
        // first within the connection's, so an address that does not answer fails in its time and the library is never let dial.
        private static Task Reach(IConnectorEgress egress, LdapSettings settings, CancellationToken cancellationToken) =>
            egress.ReachAsync(settings.Address, settings.Port, settings.Host, settings.Timeout, "LDAP", cancellationToken);

        public string ConnectorType => "ldap";

        public string DisplayName => "LDAP";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string EndpointExample => "ldap://dir.kafe.example:389";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://datatracker.ietf.org/doc/html/rfc4511");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/dotnet/api/system.directoryservices.protocols");

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "Bind DN - cn=n8n,ou=services,dc=kafe,dc=example (empty: anonymous)",
            ["password"] = "Its password",
        };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("start_tls", "StartTLS", ConnectorConfigFieldType.Boolean, Required: false, Description: "Upgrade an ldap:// connection to TLS before binding (ldaps:// is TLS from the start).") { Default = "false" },
            new ConnectorConfigField("base_dn", "Base DN", ConnectorConfigFieldType.Text, Required: false, Description: "Where a search starts when a step names no base - dc=kafe,dc=example."),
            new ConnectorConfigField("page_size", "Page size", ConnectorConfigFieldType.Integer, Required: false, Description: "Entries a page of a search holds, 1 to 1000.") { Default = "500" },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField DnField = Field("dn", "DN", "The entry's distinguished name - cn=Ada,ou=people,dc=kafe,dc=example.", required: true);

        private static readonly ConnectorConfigField AttributesField = Field("attributes", "Attributes", "A JSON object of names and a value or values - {\"objectClass\": [\"inetOrgPerson\"], \"cn\": \"Ada\", \"sn\": \"Kafé\"}.", type: ConnectorConfigFieldType.Multiline);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("search", "Search", "A directory's entries under a base, by a filter - n8n's Search.")
            {
                Parameters = new(new[]
                {
                    Field("base", "Base DN", "Where it starts - ou=people,dc=kafe,dc=example. Empty: the connection's."),
                    Field("filter", "Filter", "An RFC 4515 filter - (&(objectClass=person)(mail=*@kafe.example)). Empty: every entry.") with { Default = "(objectClass=*)" },
                    Field("scope", "Scope", "sub (the whole subtree, the default), one (one level) or base (the base alone).") with { Default = "sub" },
                    Field("attributes", "Attributes", "Comma-separated names to read - cn, mail. Empty: every one."),
                    Field("max", "At most", "How many entries, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("compare", "Compare", "Whether an entry's attribute holds a value - n8n's Compare.")
            {
                Parameters = new(new[] { DnField, Field("attribute", "Attribute", "Its name - mail.", required: true), Field("value", "Value", "The value - ada@kafe.example.", required: true) }),
            },
            new ConnectorOperation("create", "Create", "An entry and its attributes - n8n's Create.") { Parameters = new(new[] { DnField, AttributesField with { Required = true } }) },
            new ConnectorOperation("update", "Update", "Values added, replaced and removed - n8n's Update.")
            {
                Parameters = new(new[]
                {
                    DnField,
                    Field("add", "Add", "A JSON object of names and values added - {\"mail\": \"ada@kafe.example\"}.", type: ConnectorConfigFieldType.Multiline),
                    Field("replace", "Replace", "A JSON object of names and the values that replace theirs - {\"title\": \"Barista\"}.", type: ConnectorConfigFieldType.Multiline),
                    Field("remove", "Remove", "A JSON object of names and the values removed - an empty one removes every value: {\"telephoneNumber\": \"\"}.", type: ConnectorConfigFieldType.Multiline),
                }),
            },
            new ConnectorOperation("rename", "Rename or move", "An entry under a new DN - its RDN and, when it moves, its parent - n8n's Rename.")
            {
                Parameters = new(new[] { DnField, Field("to", "New DN", "Its new distinguished name - cn=Ada Lovelace,ou=people,dc=kafe,dc=example.", required: true) }),
            },
            new ConnectorOperation("delete", "Delete", "An entry - one with none under it - n8n's Delete.") { Parameters = new(new[] { DnField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            LdapSettings settings;
            try
            {
                settings = await SettingsAsync(context.Endpoint, context.Settings, context.Credentials, context.Timeout, context.Egress, cancellationToken);
                await Reach(context.Egress, settings, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }

            try
            {
                using var client = Open(settings);
                var contexts = client.NamingContexts();
                return ConnectionTestResult.Ok($"LDAP: bound to {settings.Host}{(settings.Username is null ? " anonymously" : " as " + settings.Username)}{(contexts.Count > 0 ? " - " + string.Join(", ", contexts) : string.Empty)}.");
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or TimeoutException)
            {
                return ConnectionTestResult.Fail(Said(ex));
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "search").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"LDAP has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is dialled.
            var dn = operation == "search" ? string.Empty : DnOf(Values.Get(p, "dn"), "dn");
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var pageSize = Values.Get(context.Settings, "page_size") is { } page ? int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is > 0 and <= 1000 ? size : throw new InvalidOperationException($"'{page}' is not a number from 1 to 1000 (page_size).") : 500;
            string baseDn = string.Empty, filter = "(objectClass=*)", attribute = string.Empty, value = string.Empty, to = string.Empty;
            var scope = SearchScope.Subtree;
            var names = new List<string>();
            var entry = new List<(string Name, IReadOnlyList<string> Values)>();
            var changes = new List<(DirectoryAttributeOperation Operation, string Name, IReadOnlyList<string> Values)>();
            switch (operation)
            {
                case "search":
                    baseDn = DnOf(Values.Get(p, "base") ?? Values.Get(context.Settings, "base_dn") ?? throw new InvalidOperationException("Give where the search starts (base), or the connection's base DN."), "base");
                    filter = FilterOf(Values.Get(p, "filter") ?? "(objectClass=*)");
                    scope = (Values.Get(p, "scope") ?? "sub").ToLowerInvariant() switch
                    {
                        "sub" or "subtree" => SearchScope.Subtree,
                        "one" or "onelevel" => SearchScope.OneLevel,
                        "base" => SearchScope.Base,
                        var other => throw new InvalidOperationException($"'{other}' is not sub, one or base (scope)."),
                    };
                    names = (Values.Get(p, "attributes") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(name => NameOf(name, "attributes")).ToList();
                    break;
                case "compare":
                    attribute = NameOf(Values.Get(p, "attribute") ?? throw new InvalidOperationException("Give the attribute's name (attribute)."), "attribute");
                    value = p.TryGetValue("value", out var compared) && compared is not null ? compared : throw new InvalidOperationException("Give the value it is compared with (value).");
                    break;
                case "create":
                    entry = AttributesOf(Values.Get(p, "attributes") ?? throw new InvalidOperationException("Give its attributes - a JSON object, objectClass among them (attributes)."), "attributes", allowEmpty: false);
                    if (!entry.Any(field => string.Equals(field.Name, "objectClass", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("Give its objectClass among its attributes - {\"objectClass\": [\"inetOrgPerson\"], ...}.");
                    }

                    break;
                case "update":
                    foreach (var (name, operationOf) in new[] { ("add", DirectoryAttributeOperation.Add), ("replace", DirectoryAttributeOperation.Replace), ("remove", DirectoryAttributeOperation.Delete) })
                    {
                        if (Values.Get(p, name) is { } json)
                        {
                            changes.AddRange(AttributesOf(json, name, allowEmpty: operationOf != DirectoryAttributeOperation.Add).Select(field => (operationOf, field.Name, field.Values)));
                        }
                    }

                    if (changes.Count == 0)
                    {
                        throw new InvalidOperationException("Give what changes - add, replace or remove.");
                    }

                    break;
                case "rename":
                    to = DnOf(Values.Get(p, "to"), "to");
                    break;
            }

            var settings = await SettingsAsync(context.Endpoint, context.Settings, context.Credentials, context.Timeout, context.Egress, cancellationToken);
            await Reach(context.Egress, settings, cancellationToken);
            using var client = Open(settings);
            switch (operation)
            {
                case "search":
                    foreach (var found in Call(() => client.Search(baseDn, filter, scope, names, pageSize, max, cancellationToken).Take(max).ToList()))
                    {
                        yield return Record(found);
                    }

                    break;
                case "compare":
                    yield return Fields(("dn", dn), ("attribute", attribute), ("value", value), ("matches", Call(() => client.Compare(dn, attribute, value))));
                    break;
                case "create":
                    Call(() => client.Add(dn, entry));
                    yield return Fields(("dn", dn), ("created", true));
                    break;
                case "update":
                    Call(() => client.Modify(dn, changes));
                    yield return Fields(("dn", dn), ("added", Count(changes, DirectoryAttributeOperation.Add)), ("replaced", Count(changes, DirectoryAttributeOperation.Replace)), ("removed", Count(changes, DirectoryAttributeOperation.Delete)));
                    break;
                case "rename":
                    {
                        var (rdn, parent) = Split(to);
                        var (_, currentParent) = Split(dn);
                        Call(() => client.Rename(dn, rdn, string.Equals(parent, currentParent, StringComparison.OrdinalIgnoreCase) ? null : parent));
                        yield return Fields(("dn", to), ("from", dn), ("moved", !string.Equals(parent, currentParent, StringComparison.OrdinalIgnoreCase)));
                        break;
                    }

                default:
                    Call(() => client.Delete(dn));
                    yield return Fields(("dn", dn), ("deleted", true));
                    break;
            }
        }

        /// <summary>A DN held to its shape: one line, at most 2,048 characters, an attribute=value at its start.</summary>
        public static string DnOf(string? given, string what)
        {
            var value = (given ?? throw new InvalidOperationException($"Give the distinguished name - cn=Ada,ou=people,dc=kafe,dc=example ({what}).")).Trim();
            return value.Length is > 0 and <= 2048 && !value.Any(char.IsControl) && value.IndexOf('=') > 0
                ? value
                : throw new InvalidOperationException($"'{given}' is not a distinguished name - cn=Ada,ou=people,dc=kafe,dc=example ({what}).");
        }

        /// <summary>The first RDN and the rest of a DN - split at its first comma that is not escaped.</summary>
        public static (string Rdn, string Parent) Split(string dn)
        {
            for (var i = 0; i < dn.Length; i++)
            {
                if (dn[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (dn[i] == ',')
                {
                    return (dn[..i].Trim(), dn[(i + 1)..].Trim());
                }
            }

            return (dn.Trim(), string.Empty);
        }

        private static string FilterOf(string given)
        {
            var value = given.Trim();
            if (!value.StartsWith('(') || !value.EndsWith(')') || value.Any(char.IsControl))
            {
                throw new InvalidOperationException($"'{given}' is not an LDAP filter - (objectClass=person), in parentheses (filter).");
            }

            var depth = 0;
            foreach (var c in value)
            {
                depth += c == '(' ? 1 : c == ')' ? -1 : 0;
                if (depth < 0)
                {
                    break;
                }
            }

            return depth == 0 ? value : throw new InvalidOperationException($"'{given}' is not an LDAP filter - its parentheses do not pair (filter).");
        }

        private static string NameOf(string name, string what) =>
            name.Length is > 0 and <= 128 && (char.IsAsciiLetter(name[0]) || char.IsAsciiDigit(name[0])) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ';' or '.')
                ? name
                : throw new InvalidOperationException($"'{name}' is not an attribute's name - mail, cn, objectClass ({what}).");

        // A JSON object of attribute names and a value, a list of values - or, where it may be, an empty one.
        private static List<(string Name, IReadOnlyList<string> Values)> AttributesOf(string json, string what, bool allowEmpty)
        {
            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                parsed = null;
            }

            var fields = new List<(string, IReadOnlyList<string>)>();
            foreach (var (name, node) in parsed ?? throw new InvalidOperationException($"The {what} is not a JSON object of names and values - {{\"mail\": \"ada@kafe.example\"}}."))
            {
                var values = node switch
                {
                    JsonArray array => array.Select(item => item is JsonValue single ? single.ToString() : item?.ToJsonString() ?? string.Empty).ToList(),
                    JsonValue single when single.TryGetValue<string>(out var text) => text.Length == 0 ? new List<string>() : new List<string> { text },
                    JsonValue single => new List<string> { single.ToString() },
                    null => new List<string>(),
                    _ => throw new InvalidOperationException($"'{name}' in the {what} is an object - give a value or a list of values."),
                };
                if (values.Count == 0 && !allowEmpty)
                {
                    throw new InvalidOperationException($"'{name}' in the {what} has no value.");
                }

                fields.Add((NameOf(name, what), values));
            }

            return fields.Count > 0 ? fields : throw new InvalidOperationException($"The {what} names no attribute.");
        }

        private static int Count(IEnumerable<(DirectoryAttributeOperation Operation, string Name, IReadOnlyList<string> Values)> changes, DirectoryAttributeOperation operation) =>
            changes.Count(change => change.Operation == operation);

        private static async Task<LdapSettings> SettingsAsync(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, IConnectorEgress egress, CancellationToken cancellationToken)
        {
            var given = (endpoint ?? string.Empty).Trim();
            if (given.Length == 0)
            {
                throw new InvalidOperationException("Give the directory's address - ldap://dir.kafe.example:389, or ldaps://.");
            }

            if (!given.Contains("://", StringComparison.Ordinal))
            {
                given = "ldap://" + given;
            }

            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var uri) || uri.Scheme is not ("ldap" or "ldaps") || uri.Host.Length == 0)
            {
                throw new InvalidOperationException($"'{endpoint}' is not a directory's address - ldap://dir.kafe.example:389, or ldaps://.");
            }

            if (uri.UserInfo.Length > 0 || uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new InvalidOperationException($"'{endpoint}' is not a directory's address alone - the bind DN and password are the connection's credentials, a base DN its setting.");
            }

            var ssl = uri.Scheme == "ldaps";
            var startTls = Values.GetBool(settings, "start_tls", false);
            if (ssl && startTls)
            {
                throw new InvalidOperationException("Give ldaps:// or StartTLS, not both - ldaps:// is TLS from the start.");
            }

            var port = uri.IsDefaultPort || uri.Port <= 0 ? ssl ? 636 : 389 : uri.Port;
            var address = await egress.ResolveAndPinAsync(uri.Host, port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The directory host '{uri.Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            var user = Values.Get(credentials, "username");
            return new LdapSettings(uri.Host, address, port, ssl, startTls, user, user is null ? null : credentials.TryGetValue("password", out var password) ? password : null, timeout);
        }

        // The session, bound - or, where this host's LDAP library does not load (libldap on Linux), that said.
        private ILdapClient Open(LdapSettings settings)
        {
            try
            {
                return Call(() => clientFactory(settings));
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PlatformNotSupportedException)
            {
                throw new InvalidOperationException($"This server cannot speak LDAP: its LDAP library did not load (on Linux the libldap package) - {ex.Message}", ex);
            }
        }

        // A directory's refusal: its result and its message, or the exception's own.
        private static T Call<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (DirectoryException ex)
            {
                throw new InvalidOperationException(Said(ex), ex);
            }
        }

        private static void Call(Action action) => Call(() =>
        {
            action();
            return true;
        });

        private static string Said(Exception ex) => ex switch
        {
            DirectoryOperationException operation => $"LDAP answered {operation.Response?.ResultCode.ToString() ?? "an error"}{(string.IsNullOrWhiteSpace(operation.Response?.ErrorMessage) ? "." : ": " + operation.Response!.ErrorMessage.Trim())}",
            LdapException ldap => $"LDAP: {ldap.Message.Trim()}{(string.IsNullOrWhiteSpace(ldap.ServerErrorMessage) ? string.Empty : " - " + ldap.ServerErrorMessage.Trim())}",
            _ => ex.Message,
        };

        private static ConnectorRecord Record(LdapEntry entry)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["dn"] = entry.Dn };
            foreach (var (name, values) in entry.Attributes)
            {
                fields[name] = values.Count == 1 ? values[0] : new JsonArray(values.Select(value => (JsonNode)value).ToArray()).ToJsonString(Wire);
            }

            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }

    /// <summary>One directory session's inputs: the host as named (what a certificate must name), the address the egress pinned (what the socket dials), the port, LDAPS or StartTLS, the bind (null = anonymous), the timeout.</summary>
    internal sealed record LdapSettings(string Host, IPAddress Address, int Port, bool Ssl, bool StartTls, string? Username, string? Password, TimeSpan Timeout);

    /// <summary>An entry: its DN and each attribute's values in the order the directory gave them.</summary>
    internal sealed record LdapEntry(string Dn, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Attributes);

    /// <summary>The seam over a directory: the real one speaks LDAP through System.DirectoryServices.Protocols; a test substitutes its own.</summary>
    internal interface ILdapClient : IDisposable
    {
        IReadOnlyList<string> NamingContexts();

        IEnumerable<LdapEntry> Search(string baseDn, string filter, SearchScope scope, IReadOnlyList<string> attributes, int pageSize, int max, CancellationToken cancellationToken);

        bool Compare(string dn, string attribute, string value);

        void Add(string dn, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> attributes);

        void Modify(string dn, IReadOnlyList<(DirectoryAttributeOperation Operation, string Name, IReadOnlyList<string> Values)> changes);

        void Rename(string dn, string newRdn, string? newParent);

        void Delete(string dn);
    }

    /// <summary>
    /// The real client: it dials the PINNED address, never the name; with LDAPS or StartTLS the server's certificate is verified
    /// by chain and checked against the ORIGINAL host name. The API is synchronous by nature (a bind, a request).
    /// </summary>
    internal sealed class SystemLdapClient : ILdapClient
    {
        private readonly LdapConnection connection;

        public SystemLdapClient(LdapSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Address.ToString(), settings.Port))
            {
                Timeout = settings.Timeout,
                AuthType = settings.Username is null ? AuthType.Anonymous : AuthType.Basic,
            };
            connection.SessionOptions.ProtocolVersion = 3;
            if (settings.Ssl || settings.StartTls)
            {
                var host = settings.Host;
                connection.SessionOptions.VerifyServerCertificate = (_, certificate) => Verifies(certificate, host);
            }

            connection.SessionOptions.SecureSocketLayer = settings.Ssl;
            if (settings.StartTls)
            {
                connection.SessionOptions.StartTransportLayerSecurity(null);
            }

            if (settings.Username is not null)
            {
                connection.Credential = new NetworkCredential(settings.Username, settings.Password);
            }

            connection.Bind();
        }

        public IReadOnlyList<string> NamingContexts()
        {
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, "namingContexts"));
            return response.Entries.Cast<SearchResultEntry>().SelectMany(entry => entry.Attributes.Contains("namingContexts") ? Read(entry.Attributes["namingContexts"]) : Array.Empty<string>()).ToList();
        }

        public IEnumerable<LdapEntry> Search(string baseDn, string filter, SearchScope scope, IReadOnlyList<string> attributes, int pageSize, int max, CancellationToken cancellationToken)
        {
            var page = new PageResultRequestControl(Math.Min(pageSize, max));
            var request = new SearchRequest(baseDn, filter, scope, attributes.Count == 0 ? null : attributes.ToArray());
            request.Controls.Add(page);
            var read = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (SearchResponse)connection.SendRequest(request);
                foreach (SearchResultEntry entry in response.Entries)
                {
                    read++;
                    yield return new LdapEntry(entry.DistinguishedName, entry.Attributes.Values.Cast<DirectoryAttribute>().OrderBy(attribute => attribute.Name, StringComparer.OrdinalIgnoreCase).Select(attribute => (attribute.Name, Read(attribute))).ToList());
                }

                var next = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                // A page holds at most what was asked (RFC 2696), so the most is reached at a page's end; the connector holds to it.
                if (read >= max || next is null || next.Cookie.Length == 0)
                {
                    yield break;
                }

                page.Cookie = next.Cookie;
                page.PageSize = Math.Min(pageSize, max - read);
            }
        }

        public bool Compare(string dn, string attribute, string value)
        {
            try
            {
                var response = (CompareResponse)connection.SendRequest(new CompareRequest(dn, attribute, value));
                return response.ResultCode == ResultCode.CompareTrue;
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode is ResultCode.CompareTrue or ResultCode.CompareFalse)
            {
                // The protocol answers a comparison as a result code, which the library raises.
                return ex.Response.ResultCode == ResultCode.CompareTrue;
            }
        }

        public void Add(string dn, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> attributes) =>
            connection.SendRequest(new AddRequest(dn, attributes.Select(attribute => new DirectoryAttribute(attribute.Name, attribute.Values.Cast<object>().ToArray())).ToArray()));

        public void Modify(string dn, IReadOnlyList<(DirectoryAttributeOperation Operation, string Name, IReadOnlyList<string> Values)> changes) =>
            connection.SendRequest(new ModifyRequest(dn, changes.Select(change =>
            {
                var modification = new DirectoryAttributeModification { Name = change.Name, Operation = change.Operation };
                foreach (var value in change.Values)
                {
                    modification.Add(value);
                }

                return modification;
            }).ToArray()));

        public void Rename(string dn, string newRdn, string? newParent) =>
            connection.SendRequest(new ModifyDNRequest(dn, newParent ?? LdapConnector.Split(dn).Parent, newRdn) { DeleteOldRdn = true });

        public void Delete(string dn) => connection.SendRequest(new DeleteRequest(dn));

        public void Dispose() => connection.Dispose();

        /// <summary>The chain must build and the certificate must name the ORIGINAL host.</summary>
        internal static bool Verifies(X509Certificate certificate, string host)
        {
            using var leaf = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(leaf) && leaf.MatchesHostname(host);
        }

        private static IReadOnlyList<string> Read(DirectoryAttribute attribute)
        {
            var values = new List<string>(attribute.Count);
            for (var i = 0; i < attribute.Count; i++)
            {
                values.Add(attribute[i] switch
                {
                    string text => text,
                    byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes) is var decoded && !decoded.Contains('�') && !decoded.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? decoded : Convert.ToBase64String(bytes),
                    { } other => other.ToString() ?? string.Empty,
                    _ => string.Empty,
                });
            }

            return values;
        }
    }
}
