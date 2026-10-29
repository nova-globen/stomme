#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Datasets.Infrastructure
{
    /// <summary>
    /// The Datasets module's demo content (D110 Q7): three datasets an evaluator can browse, preview
    /// and query - written per site on the boot/upgrade path through the framework's
    /// <see cref="SiteContentReconciliationRunner"/>, ONLY while <c>DemoContent:Enabled</c> is true (default off; while
    /// it is off this type is not registered), and ADDITIVELY: a site stamped at <see cref="Version"/> is left alone
    /// whatever an operator did to the rows, an unstamped site receives the datasets it lacks by origin.
    /// </summary>
    /// <remarks>
    /// Each dataset goes through the module's own registrar, so it is exactly what a producer would register: the id is
    /// the origin's (<c>demo:&lt;key&gt;</c>), the schema and summary are inferred, and the classifier decides the level -
    /// the customers set carries an e-mail column and comes out Restricted (masked, its preview refused) whatever this
    /// reconciler asserts, which is the demonstration; the classifier's floor is Internal, so nothing here is Public. The registrar reads the site from the scope's alias and the
    /// tenant from the ambient scope the runner entered - the two guards D103 measured.
    /// </remarks>
    public sealed class DatasetsDemoContentReconciler(IDatasetRepository datasets, IDatasetRegistrar registrar, DemoContentStamp stamp, ILogger<DatasetsDemoContentReconciler> logger) : ISiteContentReconciler
    {
        public const string Module = "Datasets";

        /// <summary>Bump when the demo set changes; a site stamped at an older version receives the additions.</summary>
        public const string Version = "1";

        public string Name => "Demo datasets";

        public static readonly IReadOnlyList<DemoDataset> Demo = new[]
        {
            new DemoDataset("Demo sales", "demo:sales", DataClassification.Internal,
                "[{\"sku\":\"A-100\",\"region\":\"North\",\"quantity\":\"12\",\"amount\":\"1440.00\",\"sold_on\":\"2026-09-01\"}," +
                "{\"sku\":\"A-200\",\"region\":\"South\",\"quantity\":\"3\",\"amount\":\"285.50\",\"sold_on\":\"2026-09-02\"}," +
                "{\"sku\":\"B-100\",\"region\":\"North\",\"quantity\":\"40\",\"amount\":\"3980.00\",\"sold_on\":\"2026-09-03\"}," +
                "{\"sku\":\"C-300\",\"region\":\"East\",\"quantity\":\"7\",\"amount\":\"910.00\",\"sold_on\":\"2026-09-05\"}]"),
            new DemoDataset("Demo customers", "demo:customers", DataClassification.Internal,
                "[{\"customer_id\":\"1001\",\"name\":\"Ada Example\",\"email\":\"ada@example.com\",\"country\":\"SE\"}," +
                "{\"customer_id\":\"1002\",\"name\":\"Bo Example\",\"email\":\"bo@example.com\",\"country\":\"NO\"}," +
                "{\"customer_id\":\"1003\",\"name\":\"Cy Example\",\"email\":\"cy@example.com\",\"country\":\"DK\"}]"),
            new DemoDataset("Demo inventory", "demo:inventory", DataClassification.Internal,
                "[{\"sku\":\"A-100\",\"warehouse\":\"Gothenburg\",\"on_hand\":\"120\",\"reorder_at\":\"40\"}," +
                "{\"sku\":\"A-200\",\"warehouse\":\"Gothenburg\",\"on_hand\":\"15\",\"reorder_at\":\"20\"}," +
                "{\"sku\":\"B-100\",\"warehouse\":\"Malmo\",\"on_hand\":\"400\",\"reorder_at\":\"100\"}," +
                "{\"sku\":\"C-300\",\"warehouse\":\"Malmo\",\"on_hand\":\"0\",\"reorder_at\":\"10\"}]"),
        };

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;
            if (await stamp.IsAppliedAsync(context.TenantId, siteId, Module, Version, ct))
            {
                return;
            }

            var created = new List<string>();
            foreach (var demo in Demo)
            {
                if (await datasets.FindByOriginAsync(siteId, OriginKind.DemoSeed, demo.Origin, ct) is not null)
                {
                    continue;
                }

                if (await datasets.FindByNameAsync(siteId, demo.Name, ct) is not null)
                {
                    continue; // an operator's dataset holds the name; the registrar would refuse, so it is not asked
                }

                await registrar.RegisterOrRefreshAsync(new DatasetRegistration(demo.Name, OriginKind.DemoSeed, demo.Origin, demo.SampleJson, demo.Classification), ct);
                created.Add(demo.Name);
            }

            await stamp.MarkAppliedAsync(context.TenantId, siteId, Module, Version, ct);
            logger.LogInformation("Demo datasets on site {SiteId}: created {Created}.", siteId, created.Count == 0 ? "none (all present)" : string.Join(", ", created));
        }
    }

    public sealed record DemoDataset(string Name, string Origin, DataClassification Classification, string SampleJson);
}
