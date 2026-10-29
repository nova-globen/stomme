#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.CloudUsage;

// Lifted verbatim from the predecessor platform's CloudCostNormalizer: the per-cloud keyword tables, the SaaS defaults,
// the ISO 4217 rule and the tag aliases are unchanged; only the namespace moved.
namespace Stomme.Modules.CloudUsage.Domain
{
    /// <summary>
    /// Provider-agnostic source cost row awaiting normalization into the Unified Cost Schema:
    /// the raw service name, the raw currency code, and the provider's resource tags as delivered.
    /// </summary>
    public sealed record RawCloudCost(
        DateOnly Date,
        CloudProvider Provider,
        string Service,
        string Resource,
        decimal Cost,
        string Currency,
        IReadOnlyDictionary<string, string>? Tags = null);

    /// <summary>
    /// Domain normalization service: maps raw provider cost rows to conformant Unified Cost Schema
    /// entries — service-category mapping, ISO 4217 currency normalization, and department/project/
    /// environment extraction from provider tags.
    /// </summary>
    public interface ICloudCostNormalizer
    {
        /// <summary>Normalizes a raw provider cost row into a conformant <see cref="CloudCostEntry"/>.</summary>
        CloudCostEntry Normalize(RawCloudCost cost);

        /// <summary>Maps a provider's raw service name to its normalized category; unmapped services yield <see cref="CloudServiceCategory.Other"/>.</summary>
        CloudServiceCategory MapServiceCategory(CloudProvider provider, string service);
    }

    public sealed class CloudCostNormalizer : ICloudCostNormalizer
    {
        private static readonly string[] DepartmentTagKeys = ["department", "dept", "costcenter", "cost-center", "cost_center"];
        private static readonly string[] ProjectTagKeys = ["project", "proj", "application", "app"];
        private static readonly string[] EnvironmentTagKeys = ["environment", "env", "stage"];

        /// <summary>Ordered keyword tables per infrastructure cloud; the first matching keyword wins.</summary>
        private static readonly IReadOnlyDictionary<CloudProvider, (string Keyword, CloudServiceCategory Category)[]> ServiceKeywords =
            new Dictionary<CloudProvider, (string, CloudServiceCategory)[]>
            {
                [CloudProvider.Azure] =
                [
                    ("virtual machine", CloudServiceCategory.Compute),
                    ("app service", CloudServiceCategory.Compute),
                    ("functions", CloudServiceCategory.Compute),
                    ("kubernetes", CloudServiceCategory.Compute),
                    ("container", CloudServiceCategory.Compute),
                    ("cosmos", CloudServiceCategory.Database),
                    ("sql", CloudServiceCategory.Database),
                    ("database", CloudServiceCategory.Database),
                    ("blob", CloudServiceCategory.Storage),
                    ("storage", CloudServiceCategory.Storage),
                    ("virtual network", CloudServiceCategory.Network),
                    ("load balancer", CloudServiceCategory.Network),
                    ("front door", CloudServiceCategory.Network),
                    ("bandwidth", CloudServiceCategory.Network),
                    ("synapse", CloudServiceCategory.Analytics),
                    ("databricks", CloudServiceCategory.Analytics),
                    ("data factory", CloudServiceCategory.Analytics),
                    ("openai", CloudServiceCategory.Ai),
                    ("cognitive", CloudServiceCategory.Ai),
                    ("machine learning", CloudServiceCategory.Ai),
                    ("log analytics", CloudServiceCategory.Monitoring),
                    ("application insights", CloudServiceCategory.Monitoring),
                    ("monitor", CloudServiceCategory.Monitoring),
                    ("key vault", CloudServiceCategory.Security),
                    ("defender", CloudServiceCategory.Security),
                    ("sentinel", CloudServiceCategory.Security),
                    ("service bus", CloudServiceCategory.Integration),
                    ("event grid", CloudServiceCategory.Integration),
                    ("logic apps", CloudServiceCategory.Integration),
                    ("api management", CloudServiceCategory.Integration),
                ],
                [CloudProvider.Aws] =
                [
                    ("ec2", CloudServiceCategory.Compute),
                    ("lambda", CloudServiceCategory.Compute),
                    ("fargate", CloudServiceCategory.Compute),
                    ("elastic compute", CloudServiceCategory.Compute),
                    ("s3", CloudServiceCategory.Storage),
                    ("simple storage", CloudServiceCategory.Storage),
                    ("glacier", CloudServiceCategory.Storage),
                    ("ebs", CloudServiceCategory.Storage),
                    ("efs", CloudServiceCategory.Storage),
                    ("rds", CloudServiceCategory.Database),
                    ("relational database", CloudServiceCategory.Database),
                    ("dynamo", CloudServiceCategory.Database),
                    ("aurora", CloudServiceCategory.Database),
                    ("cloudfront", CloudServiceCategory.Network),
                    ("route 53", CloudServiceCategory.Network),
                    ("elastic load", CloudServiceCategory.Network),
                    ("vpc", CloudServiceCategory.Network),
                    ("redshift", CloudServiceCategory.Analytics),
                    ("athena", CloudServiceCategory.Analytics),
                    ("kinesis", CloudServiceCategory.Analytics),
                    ("glue", CloudServiceCategory.Analytics),
                    ("sagemaker", CloudServiceCategory.Ai),
                    ("bedrock", CloudServiceCategory.Ai),
                    ("comprehend", CloudServiceCategory.Ai),
                    ("cloudwatch", CloudServiceCategory.Monitoring),
                    ("x-ray", CloudServiceCategory.Monitoring),
                    ("guardduty", CloudServiceCategory.Security),
                    ("kms", CloudServiceCategory.Security),
                    ("waf", CloudServiceCategory.Security),
                    ("sns", CloudServiceCategory.Integration),
                    ("sqs", CloudServiceCategory.Integration),
                    ("eventbridge", CloudServiceCategory.Integration),
                    ("step functions", CloudServiceCategory.Integration),
                    ("api gateway", CloudServiceCategory.Integration),
                ],
                [CloudProvider.Gcp] =
                [
                    ("compute engine", CloudServiceCategory.Compute),
                    ("kubernetes", CloudServiceCategory.Compute),
                    ("cloud run", CloudServiceCategory.Compute),
                    ("cloud functions", CloudServiceCategory.Compute),
                    ("app engine", CloudServiceCategory.Compute),
                    ("cloud storage", CloudServiceCategory.Storage),
                    ("filestore", CloudServiceCategory.Storage),
                    ("persistent disk", CloudServiceCategory.Storage),
                    ("cloud sql", CloudServiceCategory.Database),
                    ("spanner", CloudServiceCategory.Database),
                    ("bigtable", CloudServiceCategory.Database),
                    ("firestore", CloudServiceCategory.Database),
                    ("bigquery", CloudServiceCategory.Analytics),
                    ("dataflow", CloudServiceCategory.Analytics),
                    ("dataproc", CloudServiceCategory.Analytics),
                    ("looker", CloudServiceCategory.Analytics),
                    ("vertex", CloudServiceCategory.Ai),
                    ("dialogflow", CloudServiceCategory.Ai),
                    ("cloud cdn", CloudServiceCategory.Network),
                    ("cloud dns", CloudServiceCategory.Network),
                    ("load balancing", CloudServiceCategory.Network),
                    ("interconnect", CloudServiceCategory.Network),
                    ("cloud monitoring", CloudServiceCategory.Monitoring),
                    ("cloud logging", CloudServiceCategory.Monitoring),
                    ("operations suite", CloudServiceCategory.Monitoring),
                    ("cloud armor", CloudServiceCategory.Security),
                    ("secret manager", CloudServiceCategory.Security),
                    ("cloud kms", CloudServiceCategory.Security),
                    ("pub/sub", CloudServiceCategory.Integration),
                    ("pubsub", CloudServiceCategory.Integration),
                    ("apigee", CloudServiceCategory.Integration),
                ],
            };

        /// <summary>SaaS sources whose whole bill maps to one category unless a keyword refines it.</summary>
        private static readonly IReadOnlyDictionary<CloudProvider, CloudServiceCategory> ProviderDefaults =
            new Dictionary<CloudProvider, CloudServiceCategory>
            {
                [CloudProvider.Datadog] = CloudServiceCategory.Monitoring,
                [CloudProvider.Snowflake] = CloudServiceCategory.Analytics,
                [CloudProvider.GitHub] = CloudServiceCategory.Licensing,
                [CloudProvider.Microsoft365] = CloudServiceCategory.Licensing,
                [CloudProvider.Salesforce] = CloudServiceCategory.Licensing,
            };

        public CloudCostEntry Normalize(RawCloudCost cost)
        {
            ArgumentNullException.ThrowIfNull(cost);
            ArgumentException.ThrowIfNullOrWhiteSpace(cost.Service);
            ArgumentNullException.ThrowIfNull(cost.Resource);
            var service = cost.Service.Trim();
            return new CloudCostEntry(
                cost.Date,
                cost.Provider,
                MapServiceCategory(cost.Provider, service),
                service,
                cost.Resource.Trim(),
                cost.Cost,
                NormalizeCurrency(cost.Currency),
                ExtractTag(cost.Tags, DepartmentTagKeys),
                ExtractTag(cost.Tags, ProjectTagKeys),
                ExtractTag(cost.Tags, EnvironmentTagKeys));
        }

        public CloudServiceCategory MapServiceCategory(CloudProvider provider, string service)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                return CloudServiceCategory.Other;
            }

            if (ServiceKeywords.TryGetValue(provider, out var keywords))
            {
                foreach (var (keyword, category) in keywords)
                {
                    if (service.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        return category;
                    }
                }
            }

            return ProviderDefaults.GetValueOrDefault(provider, CloudServiceCategory.Other);
        }

        private static string NormalizeCurrency(string currency)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(currency);
            var trimmed = currency.Trim();
            if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetter))
            {
                throw new ArgumentException($"Currency '{currency}' is not a three-letter ISO 4217 code.", nameof(currency));
            }

            return trimmed.ToUpperInvariant();
        }

        private static string? ExtractTag(IReadOnlyDictionary<string, string>? tags, string[] aliases)
        {
            if (tags is null || tags.Count == 0)
            {
                return null;
            }

            foreach (var alias in aliases)
            {
                foreach (var (key, value) in tags)
                {
                    if (string.Equals(key, alias, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }

            return null;
        }
    }
}
