#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Cost;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The AwsCostConnectorTests (4) and GcpCostConnectorTests (5) cases, kept by name, over the port. CHANGED:
    /// the AWS key pair is the ApiKeyPair scheme's two fields (the earlier shape kept the id as a setting and the secret
    /// as a single credential), and the GCP service-account key is the ApiKey scheme's field; each missing-credential message
    /// names the field.
    /// </summary>
    public sealed class AwsCostConnectorTests
    {
        private static readonly Dictionary<string, string> Region = new() { ["region"] = "us-east-1" };

        private static ConnectorTestContext Context(IReadOnlyDictionary<string, string>? settings = null, string? accessKey = null, string? secretKey = null)
        {
            var credentials = new Dictionary<string, string>();
            if (accessKey is not null)
            {
                credentials["access-key"] = accessKey;
            }

            if (secretKey is not null)
            {
                credentials["secret-key"] = secretKey;
            }

            return Contexts.Test(new StubEgress(), string.Empty, settings, credentials);
        }

        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new AwsCostConnector();

            Assert.Equal(("awscost", "AWS Cost Explorer", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.True(connector.ConfigSchema.Fields.Single(f => f.Name == "region").Required);
            Assert.DoesNotContain(connector.ConfigSchema.Fields, f => f.Name == "accessKeyId"); // the key id is a credential now, not a setting
            Assert.NotEmpty(connector.ConfigSchema.Validate(new Dictionary<string, string>()));
            Assert.Empty(connector.ConfigSchema.Validate(Region));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheSecretAccessKeyIsTheVaultCredential()
        {
            var connector = new AwsCostConnector();

            Assert.All(connector.ConfigSchema.Fields, f => Assert.NotEqual(ConnectorConfigFieldType.Secret, f.Type));
            Assert.DoesNotContain(connector.ConfigSchema.Fields, f => f.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(connector.ConfigSchema.Fields, f => f.Name.Contains("key", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task WithRegionAccessKeyIdAndVaultSecret_ReportsConfigurationPresent()
        {
            var result = await new AwsCostConnector().TestConnectionAsync(Context(Region, accessKey: "AKIA123", secretKey: "secret-from-the-store"));

            Assert.True(result.Success);
            Assert.Contains("Configuration present", result.Message);
            Assert.Contains("Signature V4", result.Message);
        }

        [Fact]
        public async Task FailsWithoutRequiredSettingsOrCredential()
        {
            var connector = new AwsCostConnector();

            var missingRegion = await connector.TestConnectionAsync(Context(accessKey: "AKIA123", secretKey: "s"));
            Assert.False(missingRegion.Success);
            Assert.Contains("region", missingRegion.Message);

            var missingId = await connector.TestConnectionAsync(Context(Region, secretKey: "s"));
            Assert.False(missingId.Success);
            Assert.Contains("'access-key'", missingId.Message);

            var missingSecret = await connector.TestConnectionAsync(Context(Region, accessKey: "AKIA123"));
            Assert.False(missingSecret.Success);
            Assert.Contains("'secret-key'", missingSecret.Message);
        }
    }

    public sealed class GcpCostConnectorTests
    {
        private static readonly Dictionary<string, string> Account = new() { ["billingAccountId"] = "012345-6789AB-CDEF01" };

        private static ConnectorTestContext Context(IReadOnlyDictionary<string, string>? settings = null, string? key = null) =>
            Contexts.Test(new StubEgress(), string.Empty, settings, key is null ? null : new Dictionary<string, string> { ["api-key"] = key });

        [Fact]
        public void TypeIdDisplayNameAndSchema_AreTheStableContract()
        {
            var connector = new GcpCostConnector();

            Assert.Equal(("gcpcost", "Google Cloud Billing", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.True(connector.ConfigSchema.Fields.Single(f => f.Name == "billingAccountId").Required);
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "projectId").Required);
            Assert.False(connector.ConfigSchema.Fields.Single(f => f.Name == "bigQueryDataset").Required);
            Assert.Empty(connector.ConfigSchema.Validate(Account));
        }

        [Fact]
        public void Schema_NeverDeclaresASecretField_TheServiceAccountKeyIsTheVaultCredential()
        {
            var connector = new GcpCostConnector();

            Assert.All(connector.ConfigSchema.Fields, f => Assert.NotEqual(ConnectorConfigFieldType.Secret, f.Type));
            Assert.DoesNotContain(connector.ConfigSchema.Fields, f => f.Name.Contains("key", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task WithBillingAccountAndServiceAccountKeyJson_ReportsConfigurationPresent()
        {
            var result = await new GcpCostConnector().TestConnectionAsync(Context(Account, key: """{"type":"service_account","project_id":"demo"}"""));

            Assert.True(result.Success);
            Assert.Contains("Configuration present", result.Message);
        }

        [Fact]
        public async Task FailsWhenTheVaultCredentialIsNotAJsonObject()
        {
            Assert.False((await new GcpCostConnector().TestConnectionAsync(Context(Account, key: "not-json"))).Success);
            Assert.False((await new GcpCostConnector().TestConnectionAsync(Context(Account, key: "[1,2]"))).Success);
        }

        [Fact]
        public async Task FailsWithoutTheRequiredBillingAccountId_OrTheCredential()
        {
            var missingAccount = await new GcpCostConnector().TestConnectionAsync(Context(key: "{}"));
            Assert.False(missingAccount.Success);
            Assert.Contains("billingAccountId", missingAccount.Message);

            var missingKey = await new GcpCostConnector().TestConnectionAsync(Context(new Dictionary<string, string> { ["billingAccountId"] = "x" }));
            Assert.False(missingKey.Success);
            Assert.Contains("'api-key'", missingKey.Message);
        }
    }
}
