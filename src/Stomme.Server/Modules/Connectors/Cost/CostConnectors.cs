#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Cost
{
    /// <summary>
    /// The six small cost connectors (lifted from the predecessor platform's <c>azurecost</c>, <c>datadogcost</c>,
    /// <c>githubcost</c>, <c>m365cost</c>, <c>salesforcecost</c>, <c>snowflakecost</c>): a reachability probe only - one GET
    /// with the Bearer scheme's <c>token</c>, at the default endpoint or the configured one, optionally at a probe path -
    /// through the host's egress. the predecessor platform carried six identical HTTP helpers; the port carries one shape.
    /// <b>CHANGED:</b> the predecessor platform read the token from its legacy single credential and declared no scheme; the port declares
    /// Bearer, so the token is the scheme's field in the per-site secret store, and the schema still names no secret.
    /// </summary>
    public abstract class BearerProbeConnector : IConnector
    {
        public abstract string ConnectorType { get; }

        public abstract string DisplayName { get; }

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public abstract IReadOnlyList<ConnectorCategory> Categories { get; }

        public abstract Uri? WebsiteUrl { get; }

        public abstract Uri? ApiDocumentationUrl { get; }

        public virtual ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("path", "Health check path", ConnectorConfigFieldType.Text, Required: false, Description: "Optional path appended to the API endpoint for the reachability probe (e.g. /api/v1/validate). The API token is the connection's Bearer credential, never a setting."),
        });

        /// <summary>The endpoint used when the connection names none; null when the vendor's endpoint is account-specific and required.</summary>
        protected abstract string? DefaultEndpoint { get; }

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var errors = ConfigSchema.Validate(context.Settings);
            if (errors.Count > 0)
            {
                return ConnectionTestResult.Fail(string.Join(" ", errors));
            }

            var token = Values.Get(context.Credentials, "token");
            if (token is null)
            {
                return ConnectionTestResult.Fail($"The {DisplayName} connection is missing 'token' (the API token) - check the connection's credentials.");
            }

            if (string.IsNullOrWhiteSpace(context.Endpoint) && DefaultEndpoint is null)
            {
                return ConnectionTestResult.Fail($"{DisplayName} uses an account-specific endpoint; set the connection endpoint.");
            }

            var endpoint = string.IsNullOrWhiteSpace(context.Endpoint) ? DefaultEndpoint! : context.Endpoint.Trim();
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                return ConnectionTestResult.Fail("Endpoint must be an absolute http(s) URL.");
            }

            using var client = BearerClient.Create(context.Egress, context.Timeout, token);
            try
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, Target(context, baseUri)), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, DisplayName, cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"HTTP {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message); // the remote's failure; a policy refusal propagates
            }
        }

        /// <summary>The probe target: the endpoint, or the <c>path</c> setting resolved against it.</summary>
        protected virtual Uri Target(ConnectorTestContext context, Uri baseUri) =>
            Values.Get(context.Settings, "path") is { } path && Uri.TryCreate(baseUri, path, out var relative) ? relative : baseUri;
    }

    public sealed class AzureCostConnector : BearerProbeConnector
    {
        public override string ConnectorType => "azurecost";

        public override string DisplayName => "Microsoft Cost Management";

        // Dual-listed: a Microsoft cost source appears under both Cloud & SaaS Cost and Microsoft Cloud & Directory.
        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost, ConnectorCategory.MicrosoftCloudDirectory };

        public override Uri? WebsiteUrl => new("https://azure.microsoft.com/products/cost-management");

        public override Uri? ApiDocumentationUrl => new("https://learn.microsoft.com/rest/api/cost-management/");

        public override ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("subscriptionId", "Subscription id", ConnectorConfigFieldType.Text, Required: true, Description: "Azure subscription whose cost data is queried. The service-principal bearer token is the connection's Bearer credential, never a setting."),
            new ConnectorConfigField("scope", "Scope", ConnectorConfigFieldType.Text, Required: false, Description: "Optional Cost Management scope (e.g. a resource-group or management-group scope) overriding the subscription scope."),
        });

        protected override string? DefaultEndpoint => "https://management.azure.com";

        protected override Uri Target(ConnectorTestContext context, Uri baseUri) =>
            new($"{baseUri.AbsoluteUri.TrimEnd('/')}/subscriptions/{Uri.EscapeDataString(context.Settings["subscriptionId"])}?api-version=2022-12-01");
    }

    public sealed class DatadogCostConnector : BearerProbeConnector
    {
        public override string ConnectorType => "datadogcost";

        public override string DisplayName => "Datadog Cost";

        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost };

        public override Uri? WebsiteUrl => new("https://www.datadoghq.com");

        public override Uri? ApiDocumentationUrl => new("https://docs.datadoghq.com/api/latest/usage-metering/");

        protected override string? DefaultEndpoint => "https://api.datadoghq.com";
    }

    public sealed class GithubCostConnector : BearerProbeConnector
    {
        public override string ConnectorType => "githubcost";

        public override string DisplayName => "GitHub Cost";

        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost };

        public override Uri? WebsiteUrl => new("https://github.com/features");

        public override Uri? ApiDocumentationUrl => new("https://docs.github.com/en/rest/billing");

        protected override string? DefaultEndpoint => "https://api.github.com";
    }

    public sealed class M365CostConnector : BearerProbeConnector
    {
        public override string ConnectorType => "m365cost";

        public override string DisplayName => "Microsoft 365 Cost";

        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost, ConnectorCategory.MicrosoftCloudDirectory };

        public override Uri? WebsiteUrl => new("https://www.microsoft.com/microsoft-365");

        public override Uri? ApiDocumentationUrl => new("https://learn.microsoft.com/graph/api/resources/reports");

        protected override string? DefaultEndpoint => "https://graph.microsoft.com";
    }

    public sealed class SalesforceCostConnector : BearerProbeConnector
    {
        public override string ConnectorType => "salesforcecost";

        public override string DisplayName => "Salesforce Cost";

        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost };

        public override Uri? WebsiteUrl => new("https://www.salesforce.com");

        public override Uri? ApiDocumentationUrl => new("https://developer.salesforce.com/docs/apis");

        protected override string? DefaultEndpoint => null;
    }

    public sealed class SnowflakeCostConnector : BearerProbeConnector
    {
        public override string ConnectorType => "snowflakecost";

        public override string DisplayName => "Snowflake Cost";

        public override IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.CloudAndSaasCost };

        public override Uri? WebsiteUrl => new("https://www.snowflake.com");

        public override Uri? ApiDocumentationUrl => new("https://docs.snowflake.com/en/sql-reference/account-usage");

        protected override string? DefaultEndpoint => null;
    }
}
