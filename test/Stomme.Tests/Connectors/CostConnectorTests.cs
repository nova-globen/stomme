#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Cost;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-96 increment 4 - the predecessor platform's six cost connector suites (47 cases) by name over the port, one class per connector as
    /// the predecessor platform had them. CHANGED: the token is the Bearer scheme's <c>token</c> field (the predecessor platform read its legacy single
    /// credential), so the missing-credential message names the field; the schema still declares no secret.
    /// </summary>
    internal static class CostProbe
    {
        public static ConnectorTestContext Context(HttpStatusCode status, out StubHandler handler, string endpoint = "", IReadOnlyDictionary<string, string>? settings = null, string? token = null)
        {
            handler = new StubHandler(status);
            return Contexts.Test(new StubEgress(handler), endpoint, settings, token is null ? null : new Dictionary<string, string> { ["token"] = token });
        }

        public static void AssertBearer(StubHandler handler, string token) =>
            Assert.Equal(("Bearer", token), (handler.Request?.Headers.Authorization?.Scheme, handler.Request?.Headers.Authorization?.Parameter));

        public static void AssertNoSecretField(IConnector connector, bool alsoNoKey)
        {
            Assert.All(connector.ConfigSchema.Fields, f => Assert.NotEqual(ConnectorConfigFieldType.Secret, f.Type));
            Assert.DoesNotContain(connector.ConfigSchema.Fields, f => f.Name.Contains("token", StringComparison.OrdinalIgnoreCase));
            if (alsoNoKey)
            {
                Assert.DoesNotContain(connector.ConfigSchema.Fields, f => f.Name.Contains("key", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public sealed class AzureCostConnectorTests
    {
        private static readonly Dictionary<string, string> Subscription = new() { ["subscriptionId"] = "sub-123" };

        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new AzureCostConnector();

            Assert.Equal(("azurecost", "Microsoft Cost Management", AuthenticationType.Bearer), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.True(connector.ConfigSchema.Fields.Single(f => f.Name == "subscriptionId").Required);
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "scope").Required);
            Assert.NotEmpty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
            Assert.Empty(connector.ConfigSchema.Validate(Subscription));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheBearerTokenIsTheVaultCredential() => CostProbe.AssertNoSecretField(new AzureCostConnector(), alsoNoKey: false);

        [Fact]
        public async Task ProbesTheSubscriptionOnTheDefaultArmEndpoint_WithTheVaultBearerCredential()
        {
            var result = await new AzureCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, settings: Subscription, token: "sp-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://management.azure.com/subscriptions/sub-123", handler.Request?.RequestUri?.ToString());
            CostProbe.AssertBearer(handler, "sp-token");
        }

        [Fact]
        public async Task HonorsAnEndpointOverride()
        {
            var result = await new AzureCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: "https://management.usgovcloudapi.net/", settings: Subscription, token: "sp-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://management.usgovcloudapi.net/subscriptions/sub-123", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task FailsWithoutTheRequiredSubscriptionId()
        {
            var result = await new AzureCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, token: "sp-token"));

            Assert.False(result.Success);
            Assert.Contains("subscriptionId", result.Message);
        }

        [Fact]
        public async Task FailsWithoutTheVaultCredential()
        {
            var result = await new AzureCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, settings: Subscription));

            Assert.False(result.Success);
            Assert.Contains("'token'", result.Message);
            Assert.Null(handler.Request);
        }

        [Fact]
        public async Task FailsOnNonSuccessStatus_AndOnAnInvalidEndpoint()
        {
            Assert.False((await new AzureCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.Unauthorized, out _, settings: Subscription, token: "t"))).Success);
            Assert.False((await new AzureCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: "not-a-url", settings: Subscription, token: "t"))).Success);
        }
    }

    public sealed class DatadogCostConnectorTests
    {
        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new DatadogCostConnector();

            Assert.Equal(("datadogcost", "Datadog Cost"), (connector.ConnectorType, connector.DisplayName));
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "path").Required);
            Assert.Empty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
        }

        [Fact]
        public void DeclaresCatalogueMetadata_CategoryAndProductLinks()
        {
            var connector = new DatadogCostConnector();

            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(connector.Categories));
            Assert.Equal((new Uri("https://www.datadoghq.com"), new Uri("https://docs.datadoghq.com/api/latest/usage-metering/")), (connector.WebsiteUrl, connector.ApiDocumentationUrl));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheApiTokenIsTheVaultCredential() => CostProbe.AssertNoSecretField(new DatadogCostConnector(), alsoNoKey: true);

        [Fact]
        public async Task ProbesTheDefaultEndpoint_WithTheVaultApiToken()
        {
            var result = await new DatadogCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, token: "dd-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://api.datadoghq.com", handler.Request?.RequestUri?.ToString());
            CostProbe.AssertBearer(handler, "dd-token");
        }

        [Fact]
        public async Task AppliesTheOptionalProbePath_RelativeToTheEndpoint()
        {
            var result = await new DatadogCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, settings: new Dictionary<string, string> { ["path"] = "/api/v1/validate" }, token: "dd-token"));

            Assert.True(result.Success, result.Message);
            Assert.Equal("https://api.datadoghq.com/api/v1/validate", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task HonorsAnEndpointOverride()
        {
            var result = await new DatadogCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: "https://api.datadoghq.eu/", token: "dd-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://api.datadoghq.eu/", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task FailsWithoutTheVaultApiToken()
        {
            var result = await new DatadogCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _));

            Assert.False(result.Success);
            Assert.Contains("'token'", result.Message);
        }

        [Fact]
        public async Task FailsOnNonSuccessStatus_AndOnAnInvalidEndpoint()
        {
            Assert.False((await new DatadogCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.Forbidden, out _, token: "dd-token"))).Success);
            Assert.False((await new DatadogCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: "not-a-url", token: "dd-token"))).Success);
        }
    }

    public sealed class GithubCostConnectorTests
    {
        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new GithubCostConnector();

            Assert.Equal(("githubcost", "GitHub Cost"), (connector.ConnectorType, connector.DisplayName));
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "path").Required);
            Assert.Empty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
        }

        [Fact]
        public void DeclaresCatalogueMetadata_CategoryAndProductLinks()
        {
            var connector = new GithubCostConnector();

            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(connector.Categories));
            Assert.Equal((new Uri("https://github.com/features"), new Uri("https://docs.github.com/en/rest/billing")), (connector.WebsiteUrl, connector.ApiDocumentationUrl));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheApiTokenIsTheVaultCredential() => CostProbe.AssertNoSecretField(new GithubCostConnector(), alsoNoKey: true);

        [Fact]
        public async Task ProbesTheDefaultEndpoint_WithTheVaultApiToken()
        {
            var result = await new GithubCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, token: "gh-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://api.github.com", handler.Request?.RequestUri?.ToString());
            CostProbe.AssertBearer(handler, "gh-token");
        }

        [Fact]
        public async Task AppliesTheOptionalProbePath_RelativeToTheEndpoint()
        {
            var result = await new GithubCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, settings: new Dictionary<string, string> { ["path"] = "octocat" }, token: "gh-token"));

            Assert.True(result.Success, result.Message);
            Assert.Equal("https://api.github.com/octocat", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task HonorsAnEndpointOverride()
        {
            var result = await new GithubCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: "https://github.example.com/api/v3/", token: "gh-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://github.example.com/api/v3/", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task FailsWithoutTheVaultApiToken()
        {
            var result = await new GithubCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _));

            Assert.False(result.Success);
            Assert.Contains("'token'", result.Message);
        }

        [Fact]
        public async Task FailsOnNonSuccessStatus_AndOnAnInvalidEndpoint()
        {
            Assert.False((await new GithubCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.Forbidden, out _, token: "gh-token"))).Success);
            Assert.False((await new GithubCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: "not-a-url", token: "gh-token"))).Success);
        }
    }

    public sealed class M365CostConnectorTests
    {
        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new M365CostConnector();

            Assert.Equal(("m365cost", "Microsoft 365 Cost"), (connector.ConnectorType, connector.DisplayName));
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "path").Required);
            Assert.Empty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
        }

        [Fact]
        public void DeclaresCatalogueMetadata_DualListedCategoriesAndProductLinks()
        {
            var connector = new M365CostConnector();

            Assert.Contains(ConnectorCategory.CloudAndSaasCost, connector.Categories);
            Assert.Contains(ConnectorCategory.MicrosoftCloudDirectory, connector.Categories);
            Assert.Equal((new Uri("https://www.microsoft.com/microsoft-365"), new Uri("https://learn.microsoft.com/graph/api/resources/reports")), (connector.WebsiteUrl, connector.ApiDocumentationUrl));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheApiTokenIsTheVaultCredential() => CostProbe.AssertNoSecretField(new M365CostConnector(), alsoNoKey: true);

        [Fact]
        public async Task ProbesTheDefaultGraphEndpoint_WithTheVaultApiToken()
        {
            var result = await new M365CostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, token: "graph-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://graph.microsoft.com", handler.Request?.RequestUri?.ToString());
            CostProbe.AssertBearer(handler, "graph-token");
        }

        [Fact]
        public async Task AppliesTheOptionalProbePath_RelativeToTheEndpoint()
        {
            var result = await new M365CostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, settings: new Dictionary<string, string> { ["path"] = "v1.0" }, token: "graph-token"));

            Assert.True(result.Success, result.Message);
            Assert.Equal("https://graph.microsoft.com/v1.0", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task HonorsAnEndpointOverride()
        {
            var result = await new M365CostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: "https://graph.microsoft.us/", token: "graph-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith("https://graph.microsoft.us/", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task FailsWithoutTheVaultApiToken()
        {
            var result = await new M365CostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _));

            Assert.False(result.Success);
            Assert.Contains("'token'", result.Message);
        }

        [Fact]
        public async Task FailsOnNonSuccessStatus_AndOnAnInvalidEndpoint()
        {
            Assert.False((await new M365CostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.Forbidden, out _, token: "graph-token"))).Success);
            Assert.False((await new M365CostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: "not-a-url", token: "graph-token"))).Success);
        }
    }

    public sealed class SalesforceCostConnectorTests
    {
        private const string Instance = "https://my-org.my.salesforce.com";

        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new SalesforceCostConnector();

            Assert.Equal(("salesforcecost", "Salesforce Cost"), (connector.ConnectorType, connector.DisplayName));
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "path").Required);
            Assert.Empty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
        }

        [Fact]
        public void DeclaresCatalogueMetadata_CategoryAndProductLinks()
        {
            var connector = new SalesforceCostConnector();

            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(connector.Categories));
            Assert.Equal((new Uri("https://www.salesforce.com"), new Uri("https://developer.salesforce.com/docs/apis")), (connector.WebsiteUrl, connector.ApiDocumentationUrl));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheApiTokenIsTheVaultCredential() => CostProbe.AssertNoSecretField(new SalesforceCostConnector(), alsoNoKey: true);

        [Fact]
        public async Task RequiresTheInstanceSpecificEndpoint()
        {
            var result = await new SalesforceCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, token: "sf-token"));

            Assert.False(result.Success);
            Assert.Contains("endpoint", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ProbesTheConfiguredEndpoint_WithTheVaultApiToken()
        {
            var result = await new SalesforceCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: Instance + "/", token: "sf-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith(Instance + "/", handler.Request?.RequestUri?.ToString());
            CostProbe.AssertBearer(handler, "sf-token");
        }

        [Fact]
        public async Task AppliesTheOptionalProbePath_RelativeToTheEndpoint()
        {
            var result = await new SalesforceCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: Instance, settings: new Dictionary<string, string> { ["path"] = "/services/data" }, token: "sf-token"));

            Assert.True(result.Success, result.Message);
            Assert.Equal(Instance + "/services/data", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task FailsWithoutTheVaultApiToken()
        {
            var result = await new SalesforceCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: Instance));

            Assert.False(result.Success);
            Assert.Contains("'token'", result.Message);
        }

        [Fact]
        public async Task FailsOnNonSuccessStatus_AndOnAnInvalidEndpoint()
        {
            Assert.False((await new SalesforceCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.Forbidden, out _, endpoint: Instance, token: "sf-token"))).Success);
            Assert.False((await new SalesforceCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: "not-a-url", token: "sf-token"))).Success);
        }
    }

    public sealed class SnowflakeCostConnectorTests
    {
        private const string Account = "https://my-account.snowflakecomputing.com";

        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new SnowflakeCostConnector();

            Assert.Equal(("snowflakecost", "Snowflake Cost"), (connector.ConnectorType, connector.DisplayName));
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "path").Required);
            Assert.Empty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
        }

        [Fact]
        public void DeclaresCatalogueMetadata_CategoryAndProductLinks()
        {
            var connector = new SnowflakeCostConnector();

            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(connector.Categories));
            Assert.Equal((new Uri("https://www.snowflake.com"), new Uri("https://docs.snowflake.com/en/sql-reference/account-usage")), (connector.WebsiteUrl, connector.ApiDocumentationUrl));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheApiTokenIsTheVaultCredential() => CostProbe.AssertNoSecretField(new SnowflakeCostConnector(), alsoNoKey: true);

        [Fact]
        public async Task RequiresTheAccountSpecificEndpoint()
        {
            var result = await new SnowflakeCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, token: "sf-token"));

            Assert.False(result.Success);
            Assert.Contains("endpoint", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ProbesTheConfiguredEndpoint_WithTheVaultApiToken()
        {
            var result = await new SnowflakeCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: Account + "/", token: "sf-token"));

            Assert.True(result.Success, result.Message);
            Assert.StartsWith(Account + "/", handler.Request?.RequestUri?.ToString());
            CostProbe.AssertBearer(handler, "sf-token");
        }

        [Fact]
        public async Task AppliesTheOptionalProbePath_RelativeToTheEndpoint()
        {
            var result = await new SnowflakeCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out var handler, endpoint: Account, settings: new Dictionary<string, string> { ["path"] = "/api/v2/statements" }, token: "sf-token"));

            Assert.True(result.Success, result.Message);
            Assert.Equal(Account + "/api/v2/statements", handler.Request?.RequestUri?.ToString());
        }

        [Fact]
        public async Task FailsWithoutTheVaultApiToken()
        {
            var result = await new SnowflakeCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: Account));

            Assert.False(result.Success);
            Assert.Contains("'token'", result.Message);
        }

        [Fact]
        public async Task FailsOnNonSuccessStatus_AndOnAnInvalidEndpoint()
        {
            Assert.False((await new SnowflakeCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.Forbidden, out _, endpoint: Account, token: "sf-token"))).Success);
            Assert.False((await new SnowflakeCostConnector().TestConnectionAsync(CostProbe.Context(HttpStatusCode.OK, out _, endpoint: "not-a-url", token: "sf-token"))).Success);
        }
    }
}
