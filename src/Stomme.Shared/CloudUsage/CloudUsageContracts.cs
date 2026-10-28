#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.CloudUsage
{
    // ============================================================================================================
    // The CloudUsage module's Shared contracts (BL-97 increment 2): the Unified Cost Schema a cost source is normalised
    // into, and the permission keys a page or an API consumer reads. BCL-ONLY like the other Shared contracts.
    // ============================================================================================================

    /// <summary>
    /// A cloud or SaaS cost source. Values start at 1 so the CLR default (0) is not a provider and the domain refuses it.
    /// <see cref="GenericRest"/> is the extension path for a source behind the same contract.
    /// </summary>
    public enum CloudProvider
    {
        Azure = 1,

        Aws = 2,

        Gcp = 3,

        Datadog = 4,

        Snowflake = 5,

        GitHub = 6,

        Microsoft365 = 7,

        Salesforce = 8,

        GenericRest = 9,
    }

    /// <summary>
    /// What a page shows for a <see cref="CloudProvider"/> (BL-22, 2026-10-22): the member names are identifiers, and the
    /// budget page listed "Aws", "Gcp", "Microsoft365" and "GenericRest" as they stand.
    /// </summary>
    public static class CloudProviderNames
    {
        public static string Display(CloudProvider provider) => provider switch
        {
            CloudProvider.Aws => "AWS",
            CloudProvider.Gcp => "Google Cloud",
            CloudProvider.Microsoft365 => "Microsoft 365",
            CloudProvider.GenericRest => "Generic REST",
            _ => provider.ToString(),
        };

        /// <summary>A provider carried as its member name (a cost line's), shown the same way; anything else as it is.</summary>
        public static string Display(string? provider) =>
            Enum.TryParse<CloudProvider>(provider, out var parsed) && Enum.IsDefined(parsed) ? Display(parsed) : provider ?? string.Empty;
    }

    /// <summary>The Unified Cost Schema's normalised service category; a service no table maps is <see cref="Other"/>.</summary>
    public enum CloudServiceCategory
    {
        Other = 0,

        Compute = 1,

        Storage = 2,

        Network = 3,

        Database = 4,

        Analytics = 5,

        Ai = 6,

        Monitoring = 7,

        Security = 8,

        Licensing = 9,

        Integration = 10,
    }

    /// <summary>
    /// One Unified Cost Schema row: a normalised provider cost entry. Shape only - the domain enforces the invariants when
    /// the entry is recorded. <paramref name="Cost"/> may be negative: provider credits and refunds arrive as negative rows.
    /// </summary>
    /// <param name="Date">The cost date (a calendar day, no time and no zone).</param>
    /// <param name="Currency">An ISO 4217 code; costs in different currencies are never summed together.</param>
    public sealed record CloudCostEntry(
        DateOnly Date,
        CloudProvider Provider,
        CloudServiceCategory ServiceCategory,
        string Service,
        string Resource,
        decimal Cost,
        string Currency,
        string? Department = null,
        string? Project = null,
        string? Environment = null);

    public static class CloudUsagePermissions
    {
        public const string Resource = "cloud-usage";

        public const string View = "cloud-usage:view";

        public const string Manage = "cloud-usage:manage";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
        };
    }
}
