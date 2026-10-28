#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.SuseManager
{
    /// <summary>
    /// SUSE Manager / Uyuni (BL-96 increment 4, lifted from the predecessor platform's <c>susemanager</c>): the XML-RPC surface - a session
    /// opened with the Basic credentials, the systems listed, each system optionally merged with its
    /// <c>listActiveSystemsDetails</c> row, the session closed best-effort - a pasted <c>/rpc/api</c> suffix forgiven.
    /// Every request through the host's egress (D110).
    /// </summary>
    public sealed class SuseManagerConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "susemanager";

        public string DisplayName => "SUSE Multi-Linux Manager (formerly SUSE Manager)"; // the licensing review's name (BRAND_ASSET_REGISTRY.json); the type id stays susemanager

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.NetworkInfrastructure };

        public Uri? WebsiteUrl => new("https://www.suse.com/products/suse-manager/");

        public Uri? ApiDocumentationUrl => new("https://documentation.suse.com/suma/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("fetch_details", "Fetch system details", ConnectorConfigFieldType.Boolean, Description: "Enrich each system with system.listActiveSystemsDetails (one extra call per system).")
            {
                Group = "Query",
                Default = "true",
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("systems", "All systems", "Streams every registered system (system.listSystems)."),
            new ConnectorOperation("active-systems", "Active systems", "Streams the active systems (system.listActiveSystems)."),
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var rpcUrl = RpcUrl(context.Endpoint);
                var (username, password) = Credentials(context.Credentials);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var sessionKey = await LoginAsync(client, rpcUrl, username, password, cancellationToken);
                await LogoutAsync(client, rpcUrl, sessionKey, CancellationToken.None);
                return ConnectionTestResult.Ok("SUSE Manager accepted the credentials and opened a session.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var rpcUrl = RpcUrl(context.Endpoint);
            var (username, password) = Credentials(context.Credentials);
            var listMethod = ListMethod(context.OperationName);
            var fetchDetails = Values.GetBool(context.Settings, "fetch_details", defaultValue: true);

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            string? sessionKey = null;
            try
            {
                sessionKey = await LoginAsync(client, rpcUrl, username, password, cancellationToken);
                var systems = await ListSystemsAsync(client, rpcUrl, listMethod, sessionKey, cancellationToken);
                foreach (var system in systems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (system.TryGetValue("id", out var idValue) && idValue is long id && id > 0)
                    {
                        if (fetchDetails)
                        {
                            await MergeDetailsAsync(client, rpcUrl, sessionKey, (int)id, system, cancellationToken);
                        }

                        yield return new ConnectorRecord(system);
                    }
                }
            }
            finally
            {
                if (sessionKey is not null)
                {
                    try
                    {
                        await LogoutAsync(client, rpcUrl, sessionKey, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
                    {
                        // Logout is best-effort session cleanup; a failure here must not mask the extract result.
                    }
                }
            }
        }

        private static async Task<string> LoginAsync(HttpClient client, string rpcUrl, string username, string password, CancellationToken cancellationToken) =>
            XmlRpc.ReadScalarResponse(await PostAsync(client, rpcUrl, XmlRpc.BuildCall("auth.login", XmlRpc.StringValue(username), XmlRpc.StringValue(password)), cancellationToken));

        private static async Task<IReadOnlyList<Dictionary<string, object?>>> ListSystemsAsync(HttpClient client, string rpcUrl, string listMethod, string sessionKey, CancellationToken cancellationToken) =>
            XmlRpc.ReadStructArray(await PostAsync(client, rpcUrl, XmlRpc.BuildCall(listMethod, XmlRpc.StringValue(sessionKey)), cancellationToken));

        private static async Task MergeDetailsAsync(HttpClient client, string rpcUrl, string sessionKey, int id, Dictionary<string, object?> system, CancellationToken cancellationToken)
        {
            var details = XmlRpc.ReadStructArray(await PostAsync(client, rpcUrl, XmlRpc.BuildCall("system.listActiveSystemsDetails", XmlRpc.StringValue(sessionKey), XmlRpc.ArrayOfInts(new[] { id })), cancellationToken));
            if (details.Count > 0)
            {
                foreach (var (key, value) in details[0])
                {
                    system[key] = value;
                }
            }
        }

        private static Task LogoutAsync(HttpClient client, string rpcUrl, string sessionKey, CancellationToken cancellationToken) =>
            PostAsync(client, rpcUrl, XmlRpc.BuildCall("auth.logout", XmlRpc.StringValue(sessionKey)), cancellationToken);

        private static async Task<string> PostAsync(HttpClient client, string rpcUrl, string body, CancellationToken cancellationToken)
        {
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var content = new StringContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
                return new HttpRequestMessage(HttpMethod.Post, new Uri(rpcUrl)) { Content = content };
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(FailureMessage(response.StatusCode));
            }

            return payload;
        }

        private static string ListMethod(string? operation) => operation?.ToLowerInvariant() switch
        {
            null or "" or "systems" => "system.listSystems",
            "active-systems" => "system.listActiveSystems",
            _ => throw new InvalidOperationException($"Unknown operation '{operation}' for the SUSE Manager connector."),
        };

        private static (string Username, string Password) Credentials(IReadOnlyDictionary<string, string> credentials)
        {
            var username = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The SUSE Manager connection is missing 'username' - check the connection's credentials.");
            var password = credentials.TryGetValue("password", out var secret) && !string.IsNullOrEmpty(secret) ? secret : throw new InvalidOperationException("The SUSE Manager connection is missing 'password' - check the connection's credentials.");
            return (username, password);
        }

        private static string RpcUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The SUSE Manager connection needs its server base URL as the endpoint (e.g. https://suma.example.com).");
            }

            var baseUrl = endpoint.Trim().TrimEnd('/');
            if (baseUrl.EndsWith("/rpc/api", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^"/rpc/api".Length]; // a pasted suffix is forgiven
            }

            return $"{Values.BaseUrl(baseUrl, baseUrl)}/rpc/api";
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "SUSE Manager rejected the request (401) - check the server URL and credentials.",
            HttpStatusCode.NotFound => "SUSE Manager returned 404 - check the server base URL exposes /rpc/api.",
            _ => $"SUSE Manager request failed with {(int)status} {status}.",
        };
    }

    /// <summary>The XML-RPC calls and answers of SUSE Manager: a fault surfaces its faultString, structs flatten to <c>parent_child</c> columns, arrays join to a comma-separated string, a value with no type element is a string.</summary>
    internal static class XmlRpc
    {
        public static string BuildCall(string method, params XElement[] parameters) =>
            "<?xml version=\"1.0\"?>" + new XElement("methodCall", new XElement("methodName", method), new XElement("params", parameters.Select(value => new XElement("param", value)))).ToString(SaveOptions.DisableFormatting);

        public static XElement StringValue(string value) => new("value", new XElement("string", value));

        public static XElement IntValue(int value) => new("value", new XElement("int", value.ToString(CultureInfo.InvariantCulture)));

        public static XElement ArrayOfInts(IEnumerable<int> values) => new("value", new XElement("array", new XElement("data", values.Select(IntValue))));

        public static string ReadScalarResponse(string xml)
        {
            var value = Parse(xml).Element("params")?.Element("param")?.Element("value") ?? throw new InvalidOperationException("The SUSE Manager response carried no value.");
            return ParseScalar(value)?.ToString() ?? throw new InvalidOperationException("The SUSE Manager response carried an empty value.");
        }

        public static IReadOnlyList<Dictionary<string, object?>> ReadStructArray(string xml)
        {
            var data = Parse(xml).Element("params")?.Element("param")?.Element("value")?.Element("array")?.Element("data");
            var rows = new List<Dictionary<string, object?>>();
            if (data is null)
            {
                return rows;
            }

            foreach (var value in data.Elements("value"))
            {
                if (value.Element("struct") is { } structElement)
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    ParseStructInto(structElement, prefix: null, row);
                    rows.Add(row);
                }
            }

            return rows;
        }

        private static XElement Parse(string xml)
        {
            var response = XDocument.Parse(xml).Root ?? throw new InvalidOperationException("The SUSE Manager response was empty.");
            if (response.Element("fault") is { } fault)
            {
                var faultString = fault.Element("value")?.Element("struct")?.Elements("member").FirstOrDefault(m => string.Equals(m.Element("name")?.Value, "faultString", StringComparison.Ordinal))?.Element("value");
                throw new InvalidOperationException($"SUSE Manager returned a fault: {(faultString is null ? "unknown fault" : ParseScalar(faultString)?.ToString())}");
            }

            return response;
        }

        private static void ParseStructInto(XElement structElement, string? prefix, Dictionary<string, object?> target)
        {
            foreach (var member in structElement.Elements("member"))
            {
                var memberName = member.Element("name")?.Value;
                if (string.IsNullOrEmpty(memberName))
                {
                    continue;
                }

                var name = prefix is null ? memberName : $"{prefix}_{memberName}";
                var value = member.Element("value");
                if (value is null)
                {
                    target[name] = null;
                }
                else if (value.Element("struct") is { } nested)
                {
                    ParseStructInto(nested, name, target);
                }
                else if (value.Element("array") is { } array)
                {
                    target[name] = array.Element("data") is { } data ? string.Join(", ", data.Elements("value").Select(v => ParseScalar(v)?.ToString() ?? string.Empty)) : string.Empty;
                }
                else
                {
                    target[name] = ParseScalar(value);
                }
            }
        }

        private static object? ParseScalar(XElement value)
        {
            var typeNode = value.Elements().FirstOrDefault();
            if (typeNode is null)
            {
                return value.Value; // XML-RPC treats a value with no type element as a string
            }

            return typeNode.Name.LocalName switch
            {
                "string" or "dateTime.iso8601" or "base64" => typeNode.Value,
                "int" or "i4" => long.TryParse(typeNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : typeNode.Value,
                "double" => double.TryParse(typeNode.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : typeNode.Value,
                "boolean" => typeNode.Value.Trim() == "1",
                "nil" => null,
                _ => typeNode.Value, // a struct or array where a scalar was expected keeps its text
            };
        }
    }
}
