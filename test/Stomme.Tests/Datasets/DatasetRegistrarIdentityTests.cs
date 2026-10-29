#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Modules.Datasets.Application;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The registrar's identity rules over the module's real startup on the File store: a
    /// registration for a NEW origin under a name another dataset holds is rejected, not overwritten; the same origin
    /// registered again refreshes the one row and may rename it; an over-cap sample is rejected before inference and
    /// writes nothing; the command classifies, so a PII sample registered as Public comes back Restricted and a secret
    /// column as Confidential; there is no default-tenant fallback - a missing site or tenant throws.
    /// </summary>
    public sealed class DatasetRegistrarIdentityTests
    {
        private const string Rows = "[ { \"sku\": \"A1\", \"quantity\": \"10\" } ]";

        [Fact]
        public async Task A_same_name_different_origin_registration_is_rejected_not_overwritten()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var first = await harness.RegisterAsync(new DatasetRegistration("Sales", OriginKind.PipelineOutput, "pipeline-1", Rows));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RegisterAsync(new DatasetRegistration("sales", OriginKind.Upload, "file-9", "[ { \"other\": \"1\" } ]")));

            Assert.Contains("another origin", ex.Message);
            var only = Assert.Single(await harness.SendAsync(new ListDatasetsQuery()));
            Assert.Equal((first, "Sales", OriginKind.PipelineOutput), (only.Id, only.Name, only.OriginKind));
            Assert.Contains(only.Schema.Columns, column => column.Name == "sku"); // the first registration's schema survives
        }

        [Fact]
        public async Task The_same_origin_registered_again_refreshes_the_one_row_and_may_rename_it()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var first = await harness.RegisterAsync(new DatasetRegistration("Sales", OriginKind.PipelineOutput, "Pipeline-1", Rows));

            var again = await harness.RegisterAsync(new DatasetRegistration("Sales renamed", OriginKind.PipelineOutput, " pipeline-1 ", "[ { \"sku\": \"B2\", \"quantity\": \"3\" }, { \"sku\": \"C3\", \"quantity\": \"4\" } ]"));

            Assert.Equal(first, again);
            var only = Assert.Single(await harness.SendAsync(new ListDatasetsQuery()));
            Assert.Equal(("Sales renamed", 2), (only.Name, only.Summary.RowCountEstimate));

            // A rename onto a name another origin holds is refused, and the row stays as it was.
            await harness.RegisterAsync(new DatasetRegistration("Ledger", OriginKind.Upload, "file-1", Rows));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RegisterAsync(new DatasetRegistration("LEDGER", OriginKind.PipelineOutput, "pipeline-1", Rows)));
            Assert.Equal(new[] { "Ledger", "Sales renamed" }, (await harness.SendAsync(new ListDatasetsQuery())).Select(dataset => dataset.Name));
        }

        [Fact]
        public async Task An_over_cap_sample_is_rejected_before_inference_and_writes_nothing()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var huge = "[" + new string(' ', DatasetLimits.MaxSampleBytes) + "]";

            await Assert.ThrowsAsync<ArgumentException>(() => harness.RegisterAsync(new DatasetRegistration("Huge", OriginKind.Upload, null, huge)));
            await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => harness.SendAsync(new RegisterDatasetCommand("Huge", OriginKind.Upload, null, huge)));

            Assert.Empty(await harness.SendAsync(new ListDatasetsQuery()));
        }

        [Fact]
        public async Task The_command_classifies_so_a_PII_sample_registered_as_Public_comes_back_Restricted()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();

            var id = await harness.SendAsync(new RegisterDatasetCommand("Customers", OriginKind.Upload, "file-1", "[ { \"name\": \"Ada\", \"contact\": \"ada@example.com\" } ]", DataClassification.Public));
            var dataset = await harness.SendAsync(new GetDatasetQuery(id));

            Assert.NotNull(dataset);
            Assert.Equal(DataClassification.Restricted, dataset.Classification);
            Assert.All(dataset.Summary.Columns, column => Assert.Empty(column.Samples));
            Assert.False((await harness.SendAsync(new PreviewDatasetRowsQuery(id))).Allowed);

            var secret = await harness.SendAsync(new RegisterDatasetCommand("Keys", OriginKind.Upload, "file-2", "[ { \"api_key\": \"k\" } ]", DataClassification.Internal));
            Assert.Equal(DataClassification.Confidential, (await harness.SendAsync(new GetDatasetQuery(secret)))!.Classification);

            // A benign refresh never lowers it.
            await harness.SendAsync(new RefreshDatasetCommand(secret, "[ { \"note\": \"ok\" } ]"));
            Assert.Equal(DataClassification.Confidential, (await harness.SendAsync(new GetDatasetQuery(secret)))!.Classification);
        }

        [Fact]
        public async Task A_missing_site_or_tenant_throws_with_no_default_fallback()
        {
            await using var noSite = await DatasetsApplicationHarness.StartAsync(withSite: false);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => noSite.RegisterAsync(new DatasetRegistration("X", OriginKind.Upload, null, Rows)));
            Assert.Contains("site", ex.Message, StringComparison.OrdinalIgnoreCase);

            await using var noTenant = await DatasetsApplicationHarness.StartAsync(services =>
            {
                services.RemoveAll<ITenantContext>();
                services.AddScoped<ITenantContext, NoTenantContext>();
            });
            var tenant = await Assert.ThrowsAsync<InvalidOperationException>(() => noTenant.RegisterAsync(new DatasetRegistration("X", OriginKind.Upload, null, Rows)));
            Assert.Contains("tenant", tenant.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
