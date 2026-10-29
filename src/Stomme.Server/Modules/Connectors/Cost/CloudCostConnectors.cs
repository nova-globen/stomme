#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Cost
{
    /// <summary>
    /// AWS Cost Explorer: a configuration check, no call - a live
    /// probe needs AWS Signature V4 request signing, which is not written, so the test says so and the first cost sync is
    /// the verification. The connector declares ApiKeyPair, so both halves of the key are the scheme's fields in the per-site secret store and
    /// the schema keeps the region only.
    /// </summary>
    public sealed class AwsCostConnector : IConnector
    {
        public string ConnectorType => "awscost";

        public string DisplayName => "AWS Cost Explorer";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost };

        public Uri? WebsiteUrl => new("https://aws.amazon.com/aws-cost-management/");

        public Uri? ApiDocumentationUrl => new("https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "AWS region for the Cost Explorer API (e.g. us-east-1). The IAM access key id and secret access key are the connection's key pair, never settings."),
        });

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var errors = ConfigSchema.Validate(context.Settings);
            if (errors.Count > 0)
            {
                return Task.FromResult(ConnectionTestResult.Fail(string.Join(" ", errors)));
            }

            if (Values.Get(context.Credentials, "access-key") is null)
            {
                return Task.FromResult(ConnectionTestResult.Fail("The AWS Cost Explorer connection is missing 'access-key' (the IAM access key id) - check the connection's credentials."));
            }

            if (Values.Get(context.Credentials, "secret-key") is null)
            {
                return Task.FromResult(ConnectionTestResult.Fail("The AWS Cost Explorer connection is missing 'secret-key' (the IAM secret access key) - check the connection's credentials."));
            }

            return Task.FromResult(ConnectionTestResult.Ok("Configuration present. Live verification requires AWS Signature V4 request signing and runs on the first cost sync."));
        }
    }

    /// <summary>
    /// Google Cloud Billing: a configuration check, no call - the
    /// service-account key must parse as a JSON object; the live verification needs the GCP OAuth token flow and runs on
    /// the first cost sync. The key JSON is the ApiKey scheme's <c>api-key</c> field in the per-site secret
    /// store.
    /// </summary>
    public sealed class GcpCostConnector : IConnector
    {
        public string ConnectorType => "gcpcost";

        public string DisplayName => "Google Cloud Billing";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost };

        public Uri? WebsiteUrl => new("https://cloud.google.com/billing");

        public Uri? ApiDocumentationUrl => new("https://cloud.google.com/billing/docs/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("billingAccountId", "Billing account id", ConnectorConfigFieldType.Text, Required: true, Description: "GCP billing account whose cost data is queried. The service-account key JSON is the connection's API key credential, never a setting."),
            new ConnectorConfigField("projectId", "Project id", ConnectorConfigFieldType.Text, Required: false, Description: "Optional GCP project scoping the cost query."),
            new ConnectorConfigField("bigQueryDataset", "BigQuery dataset", ConnectorConfigFieldType.Text, Required: false, Description: "Optional BigQuery dataset carrying the billing export, when costs are read from an export instead of the Cloud Billing API."),
        });

        public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var errors = ConfigSchema.Validate(context.Settings);
            if (errors.Count > 0)
            {
                return Task.FromResult(ConnectionTestResult.Fail(string.Join(" ", errors)));
            }

            var key = Values.Get(context.Credentials, "api-key");
            if (key is null)
            {
                return Task.FromResult(ConnectionTestResult.Fail("The Google Cloud Billing connection is missing 'api-key' (the service-account key JSON) - check the connection's credentials."));
            }

            try
            {
                using var document = JsonDocument.Parse(key);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Task.FromResult(ConnectionTestResult.Fail("The 'api-key' credential must be a service-account key JSON object."));
                }
            }
            catch (JsonException)
            {
                return Task.FromResult(ConnectionTestResult.Fail("The 'api-key' credential must be a service-account key JSON object."));
            }

            return Task.FromResult(ConnectionTestResult.Ok("Configuration present; the service-account key parses as JSON. Live verification requires the GCP OAuth token flow and runs on the first cost sync."));
        }
    }
}
