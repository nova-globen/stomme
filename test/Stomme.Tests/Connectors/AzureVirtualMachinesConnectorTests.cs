#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.AzureVirtualMachines;
using Xunit;
using static Stomme.Tests.Connectors.RecordingHandler;

namespace Stomme.Tests.Connectors
{
    /// <summary>The AzureVirtualMachinesConnectorTests cases (8), kept by name over the port.</summary>
    public sealed class AzureVirtualMachinesConnectorTests
    {
        private const string SubscriptionsJson = """{"value":[{"subscriptionId":"s1","displayName":"Production"},{"subscriptionId":"s2","displayName":"Sandbox"}]}""";

        // In the instance view the PowerState status is deliberately FIRST: the legacy bridge read statuses[1] and would have picked the provisioning status here.
        private const string InstanceViewJson = """
            {"value":[{"id":"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-1","name":"vm-1",
              "properties":{"instanceView":{"statuses":[{"code":"PowerState/running","displayStatus":"VM running"},{"code":"ProvisioningState/succeeded","displayStatus":"Provisioning succeeded"}]}}}]}
            """;

        private const string VmPageJson = """
            {"value":[{"id":"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-1","name":"vm-1","location":"westeurope",
              "properties":{"vmId":"11111111-2222-3333-4444-555555555555","provisioningState":"Succeeded","hardwareProfile":{"vmSize":"Standard_D2s_v5"},
                "storageProfile":{"imageReference":{"publisher":"Canonical","offer":"ubuntu-24_04-lts","sku":"server","version":"latest"},"osDisk":{"osType":"Linux"}},
                "networkProfile":{"networkInterfaces":[
                  {"id":"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic-secondary","properties":{"primary":false}},
                  {"id":"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic-primary","properties":{"primary":true}}]}}}]}
            """;

        private const string NicJson = """{"name":"nic-primary","properties":{"macAddress":"00-0D-3A-11-22-33","ipConfigurations":[{"name":"ipconfig1","properties":{"primary":true,"privateIPAddress":"10.0.0.4"}}]}}""";

        private static RecordingHandler ArmHandler(string vmPage = VmPageJson, string instanceView = InstanceViewJson, string subscriptions = SubscriptionsJson) => new(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("statusOnly=true", StringComparison.Ordinal))
            {
                return Json(instanceView);
            }

            if (url.Contains("/providers/Microsoft.Compute/virtualMachines", StringComparison.Ordinal))
            {
                return Json(vmPage);
            }

            if (url.Contains("/networkInterfaces/", StringComparison.Ordinal))
            {
                return Json(NicJson);
            }

            if (url.Contains("/subscriptions?", StringComparison.Ordinal))
            {
                return Json(subscriptions);
            }

            return Json("""{"error":"unexpected route"}""", HttpStatusCode.NotFound);
        })
        { TokenRoute = request => request.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) };

        private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal)
        {
            ["token-url"] = "https://login.microsoftonline.com/tenant/oauth2/v2.0/token",
            ["client-id"] = "client",
            ["client-secret"] = "secret",
        };

        private static ConnectorExtractContext Extract(RecordingHandler handler, Dictionary<string, string>? settings = null, Dictionary<string, string>? parameters = null, string operation = "virtual-machines") =>
            Contexts.Extract(new StubEgress(handler), string.Empty, operation, parameters, settings ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["subscription_id"] = "s1" }, Credentials(), timeoutSeconds: 30);

        [Fact]
        public void Declaration_MatchesTheArmPort()
        {
            var connector = new AzureVirtualMachinesConnector();

            Assert.Equal(("azurevm", AuthenticationType.OAuth2ClientCredentials), (connector.ConnectorType, connector.AuthType));
            Assert.True(((IConnector)connector).Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal("virtual-machines", Assert.Single(connector.Operations).Name);
        }

        [Fact]
        public async Task Extract_MapsVmWithPowerStateAndPrimaryNic()
        {
            var handler = ArmHandler();

            var records = await Contexts.DrainAsync(new AzureVirtualMachinesConnector(), Extract(handler));

            var vm = Assert.Single(records);
            Assert.Equal("vm-1", vm.Fields["name"]);
            Assert.Equal("Standard_D2s_v5", vm.Fields["vmSize"]);
            Assert.Equal("Linux", vm.Fields["osType"]);
            Assert.Equal("Canonical", vm.Fields["imagePublisher"]);
            Assert.Equal("s1", vm.Fields["subscriptionId"]);
            Assert.Equal("running", vm.Fields["powerState"]); // parsed by the PowerState/ code prefix although the status sits at index 0
            Assert.Equal("00-0D-3A-11-22-33", vm.Fields["macAddress"]);
            Assert.Equal("10.0.0.4", vm.Fields["privateIpAddress"]);

            // The primary NIC (not the first array entry) was fetched, at the NIC api-version; the VM list honoured the default compute api-version.
            var nic = handler.Requests.Single(r => r.RequestUri!.AbsoluteUri.Contains("/networkInterfaces/"));
            Assert.Contains("nic-primary", nic.RequestUri!.AbsoluteUri);
            Assert.Contains($"api-version={AzureVirtualMachinesConnector.NicApiVersion}", nic.RequestUri!.AbsoluteUri);
            var list = handler.Requests.First(r => r.RequestUri!.AbsoluteUri.Contains("Microsoft.Compute/virtualMachines?api-version="));
            Assert.Contains($"api-version={AzureVirtualMachinesConnector.DefaultVmApiVersion}", list.RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Extract_DiscoverAllSubscriptions_EnumeratesEachSubscription()
        {
            var handler = ArmHandler();

            var records = await Contexts.DrainAsync(new AzureVirtualMachinesConnector(), Extract(handler, settings: new(StringComparer.Ordinal) { ["discover_all_subscriptions"] = "true" }));

            Assert.Equal(2, records.Count);
            Assert.Equal(new[] { "s1", "s2" }, records.Select(r => (string?)r.Fields["subscriptionId"]));
            Assert.Equal(new[] { "Production", "Sandbox" }, records.Select(r => (string?)r.Fields["subscriptionName"]));
            Assert.Contains(handler.Requests, r => r.RequestUri!.AbsoluteUri.Contains($"/subscriptions?api-version={AzureVirtualMachinesConnector.SubscriptionsApiVersion}"));
            // Plain VM-list calls only - the statusOnly instance-view URL shares the prefix.
            Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.AbsoluteUri.Contains("Microsoft.Compute/virtualMachines?api-version=") && !r.RequestUri!.AbsoluteUri.Contains("statusOnly=true")));
        }

        [Fact]
        public async Task Extract_SkipsEnrichmentWhenDisabled()
        {
            var handler = ArmHandler();

            var records = await Contexts.DrainAsync(new AzureVirtualMachinesConnector(), Extract(handler, parameters: new(StringComparer.Ordinal) { ["include_power_state"] = "false", ["include_network"] = "false" }));

            var vm = Assert.Single(records);
            Assert.False(vm.Fields.ContainsKey("powerState"));
            Assert.False(vm.Fields.ContainsKey("macAddress"));
            Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsoluteUri.Contains("statusOnly=true"));
            Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/networkInterfaces/"));
        }

        [Fact]
        public async Task Extract_WithoutSubscriptionScope_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AzureVirtualMachinesConnector(), Extract(ArmHandler(), settings: new(StringComparer.Ordinal))));

            Assert.Contains("subscription_id", ex.Message);
        }

        [Fact]
        public async Task Extract_UnknownOperation_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AzureVirtualMachinesConnector(), Extract(ArmHandler(), operation: "disks")));

            Assert.Contains("Unknown operation", ex.Message);
        }

        [Fact]
        public async Task TestConnection_ReportsVisibleSubscriptions()
        {
            var result = await new AzureVirtualMachinesConnector().TestConnectionAsync(Contexts.Test(new StubEgress(ArmHandler()), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.True(result.Success, result.Message);
            Assert.Contains("2 subscription(s)", result.Message);
        }

        [Fact]
        public async Task TestConnection_Forbidden_MentionsReaderRole()
        {
            var handler = new RecordingHandler(_ => Json("""{"error":{}}""", HttpStatusCode.Forbidden)) { TokenRoute = request => request.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) };

            var result = await new AzureVirtualMachinesConnector().TestConnectionAsync(Contexts.Test(new StubEgress(handler), string.Empty, credentials: Credentials(), timeoutSeconds: 30));

            Assert.False(result.Success);
            Assert.Contains("Reader role", result.Message);
        }
    }
}
