#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AzureVirtualMachines
{
    /// <summary>
    /// Azure Virtual Machines (lifted from the predecessor platform's <c>azurevm</c>): OAuth2 client credentials over
    /// Azure Resource Manager through the family's token provider, keeping the legacy three-phase shape per subscription -
    /// the VM list (configurable api-version), the instance view (<c>statusOnly=true</c>) for the power state, and the
    /// primary-NIC enrichment (MAC and first private IP; public IPs stay unfetched). The power state is parsed from the
    /// status whose code starts with <c>PowerState/</c>, never by array index (a legacy bug); records keep typed
    /// ARM-native fields. Every request through the host's egress.
    /// </summary>
    public sealed class AzureVirtualMachinesConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://management.azure.com";
        internal const string DefaultScope = "https://management.azure.com/.default";

        /// <summary>Pinned like the legacy bridge - the subscriptions list is a stable, versioned discovery call.</summary>
        internal const string SubscriptionsApiVersion = "2020-01-01";

        internal const string DefaultVmApiVersion = "2024-07-01";
        internal const string NicApiVersion = "2023-04-01";

        private const string PowerStatePrefix = "PowerState/";

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "azurevm";

        public string DisplayName => "Azure Virtual Machines";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.MicrosoftCloudDirectory };

        public Uri? WebsiteUrl => new("https://azure.microsoft.com/products/virtual-machines");

        public Uri? ApiDocumentationUrl => new("https://learn.microsoft.com/rest/api/compute/virtual-machines");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Description: "The token scope requested from the identity platform.")
            {
                Group = "Authentication",
                Default = DefaultScope,
                HelpText = "Change only for sovereign clouds (e.g. Azure US Government / 21Vianet endpoints).",
            },
            new ConnectorConfigField("discover_all_subscriptions", "Discover all subscriptions", ConnectorConfigFieldType.Boolean, Description: "Enumerate every subscription visible to the service principal.")
            {
                Group = "Scope",
                Default = "false",
            },
            new ConnectorConfigField("subscription_id", "Subscription id", ConnectorConfigFieldType.Text, Description: "The subscription to read.")
            {
                Group = "Scope",
                HelpText = "Required unless subscription discovery is enabled.",
            },
            new ConnectorConfigField("api_version", "Compute api-version", ConnectorConfigFieldType.Text, Description: "The ARM api-version used for the virtual-machines calls.")
            {
                Group = "Query",
                Default = DefaultVmApiVersion,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("virtual-machines", "Virtual machines", "Streams virtual machines with power state and primary NIC details across the selected subscriptions.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("include_power_state", "Include power state", ConnectorConfigFieldType.Boolean, Description: "Reads the instance view (statusOnly) per subscription.") { Default = "true" },
                    new ConnectorConfigField("include_network", "Include network details", ConnectorConfigFieldType.Boolean, Description: "Reads each VM's primary network interface for its MAC address and private IP.") { Default = "true" },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = BearerClient.Create(context.Egress, context.Timeout, token);
                var (subscriptions, _) = await GetCollectionAsync(client, $"{baseUrl}/subscriptions?api-version={SubscriptionsApiVersion}", cancellationToken);
                return ConnectionTestResult.Ok($"Azure Resource Manager responded; {subscriptions.Count} subscription(s) visible.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!string.IsNullOrWhiteSpace(context.OperationName) && !string.Equals(context.OperationName, "virtual-machines", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Azure Virtual Machines connector.");
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var apiVersion = Values.GetOrDefault(context.Settings, "api_version", DefaultVmApiVersion);
            var includePowerState = Values.GetBool(context.Parameters, "include_power_state", defaultValue: true);
            var includeNetwork = Values.GetBool(context.Parameters, "include_network", defaultValue: true);

            using var client = BearerClient.Create(context.Egress, context.Timeout, token);
            var subscriptions = await ResolveSubscriptionsAsync(client, baseUrl, context.Settings, cancellationToken);
            foreach (var (subscriptionId, subscriptionName) in subscriptions)
            {
                var listUrl = $"{baseUrl}/subscriptions/{subscriptionId}/providers/Microsoft.Compute/virtualMachines?api-version={Uri.EscapeDataString(apiVersion)}";
                var powerStates = includePowerState
                    ? await FetchPowerStatesAsync(client, listUrl, cancellationToken)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var next = (string?)listUrl;
                while (next is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (vms, nextLink) = await GetCollectionAsync(client, next, cancellationToken);
                    foreach (var vm in vms)
                    {
                        var fields = MapVm(vm, subscriptionId, subscriptionName);
                        if (includePowerState && fields["id"] is string vmResourceId && powerStates.TryGetValue(vmResourceId, out var powerState))
                        {
                            fields["powerState"] = powerState;
                        }

                        if (includeNetwork && PrimaryNicId(vm) is { } nicId)
                        {
                            var (mac, privateIp) = await FetchNicAsync(client, baseUrl, nicId, cancellationToken);
                            fields["macAddress"] = mac;
                            fields["privateIpAddress"] = privateIp;
                        }

                        yield return new ConnectorRecord(fields);
                    }

                    next = nextLink;
                }
            }
        }

        private static async Task<List<(string Id, string? Name)>> ResolveSubscriptionsAsync(HttpClient client, string baseUrl, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
        {
            if (Values.GetBool(settings, "discover_all_subscriptions", defaultValue: false))
            {
                var subscriptions = new List<(string, string?)>();
                var next = (string?)$"{baseUrl}/subscriptions?api-version={SubscriptionsApiVersion}";
                while (next is not null)
                {
                    var (items, nextLink) = await GetCollectionAsync(client, next, cancellationToken);
                    foreach (var item in items)
                    {
                        if (GetString(item, "subscriptionId") is { } id)
                        {
                            subscriptions.Add((id, GetString(item, "displayName")));
                        }
                    }

                    next = nextLink;
                }

                return subscriptions;
            }

            var configured = Values.Get(settings, "subscription_id")
                ?? throw new InvalidOperationException("The Azure Virtual Machines connection needs a 'subscription_id' (or enable 'discover_all_subscriptions').");
            return new List<(string, string?)> { (configured, null) };
        }

        private static async Task<Dictionary<string, string>> FetchPowerStatesAsync(HttpClient client, string listUrl, CancellationToken cancellationToken)
        {
            // Keyed by the VM's ARM resource id, case-insensitively - ARM ids are not case-stable across APIs.
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var next = (string?)$"{listUrl}&statusOnly=true";
            while (next is not null)
            {
                var (vms, nextLink) = await GetCollectionAsync(client, next, cancellationToken);
                foreach (var vm in vms)
                {
                    if (GetString(vm, "id") is not { } id)
                    {
                        continue;
                    }

                    if (vm.TryGetProperty("properties", out var props) && props.TryGetProperty("instanceView", out var view) &&
                        view.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var status in statuses.EnumerateArray())
                        {
                            // A prefix match, never statuses[1]: the legacy index-based read broke whenever ARM reordered or extended the list.
                            if (GetString(status, "code") is { } code && code.StartsWith(PowerStatePrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                states[id] = code[PowerStatePrefix.Length..];
                                break;
                            }
                        }
                    }
                }

                next = nextLink;
            }

            return states;
        }

        private static async Task<(string? MacAddress, string? PrivateIp)> FetchNicAsync(HttpClient client, string baseUrl, string nicId, CancellationToken cancellationToken)
        {
            var payload = await BearerClient.GetAsync(client, $"{baseUrl}{nicId}?api-version={NicApiVersion}", FailureMessage, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var mac = GetString(root, "properties", "macAddress");

            string? privateIp = null;
            if (root.TryGetProperty("properties", out var props) && props.TryGetProperty("ipConfigurations", out var configs) && configs.ValueKind == JsonValueKind.Array)
            {
                JsonElement? chosen = null;
                foreach (var config in configs.EnumerateArray())
                {
                    chosen ??= config;
                    if (config.TryGetProperty("properties", out var cp) && cp.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                    {
                        chosen = config;
                        break;
                    }
                }

                if (chosen is { } picked)
                {
                    privateIp = GetString(picked, "properties", "privateIPAddress");
                }
            }

            return (mac, privateIp);
        }

        private static Dictionary<string, object?> MapVm(JsonElement vm, string subscriptionId, string? subscriptionName) => new(StringComparer.Ordinal)
        {
            ["id"] = GetString(vm, "id"),
            ["name"] = GetString(vm, "name"),
            ["location"] = GetString(vm, "location"),
            ["vmId"] = GetString(vm, "properties", "vmId"),
            ["vmSize"] = GetString(vm, "properties", "hardwareProfile", "vmSize"),
            ["osType"] = GetString(vm, "properties", "storageProfile", "osDisk", "osType"),
            ["provisioningState"] = GetString(vm, "properties", "provisioningState"),
            ["imagePublisher"] = GetString(vm, "properties", "storageProfile", "imageReference", "publisher"),
            ["imageOffer"] = GetString(vm, "properties", "storageProfile", "imageReference", "offer"),
            ["imageSku"] = GetString(vm, "properties", "storageProfile", "imageReference", "sku"),
            ["imageVersion"] = GetString(vm, "properties", "storageProfile", "imageReference", "version"),
            ["subscriptionId"] = subscriptionId,
            ["subscriptionName"] = subscriptionName,
        };

        private static string? PrimaryNicId(JsonElement vm)
        {
            if (!vm.TryGetProperty("properties", out var props) || !props.TryGetProperty("networkProfile", out var network) ||
                !network.TryGetProperty("networkInterfaces", out var nics) || nics.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? first = null;
            foreach (var nic in nics.EnumerateArray())
            {
                var id = GetString(nic, "id");
                first ??= id;
                if (nic.TryGetProperty("properties", out var np) && np.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                {
                    return id;
                }
            }

            return first;
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        private static async Task<(List<JsonElement> Items, string? NextLink)> GetCollectionAsync(HttpClient client, string url, CancellationToken cancellationToken) =>
            ArmCollection.Read(await BearerClient.GetAsync(client, url, FailureMessage, cancellationToken));

        private static string? GetString(JsonElement element, params string[] path)
        {
            var current = element;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Azure Resource Manager rejected the token (401) - check the tenant's token URL and the client credentials.",
            HttpStatusCode.Forbidden => "Azure Resource Manager denied access (403) - grant the service principal the Reader role on the target subscriptions.",
            _ => $"Azure Resource Manager request failed with {(int)status} {status}.",
        };
    }

    /// <summary>One ARM collection page: the <c>value</c> array (elements cloned so they outlive the parse) and the <c>nextLink</c> continuation - ARM's shape, distinct from Graph's <c>@odata.nextLink</c>.</summary>
    internal static class ArmCollection
    {
        public static (List<JsonElement> Items, string? NextLink) Read(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The ARM response carries no 'value' array.");
            }

            var items = new List<JsonElement>();
            foreach (var element in rows.EnumerateArray())
            {
                items.Add(element.Clone());
            }

            string? nextLink = null;
            if (root.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String)
            {
                nextLink = next.GetString();
            }

            return (items, nextLink);
        }
    }
}
