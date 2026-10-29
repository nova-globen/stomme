#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Modules.Datasets.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The mediator, registrar and catalog cases, ported by name over the module's real startup
    /// on the File store: DatasetTests (the register/list/preview/registrar/delete ones; the upload ones are not ported here
    /// ), DatasetClassificationTests (4, with a settable classifier), DatasetCatalogTests (3). CHANGED on purpose: a
    /// preview is refused for Restricted as well as Confidential (Q1); the registrar's identity is the origin's.
    /// </summary>
    public sealed class DatasetApplicationTests
    {
        [Fact]
        public async Task Register_ThenList_IncludesMetadata()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.PipelineOutput, "pipeline-1", "[ { \"amount\": \"10\" } ]"));

            var list = await harness.SendAsync(new ListDatasetsQuery());

            var dataset = Assert.Single(list, dataset => dataset.Id == id);
            Assert.Contains(dataset.Schema.Columns, column => column.Name == "amount" && column.DataType == ColumnDataType.Number);
            Assert.Equal((OriginKind.PipelineOutput, "pipeline-1", DataClassification.Internal, 1), (dataset.OriginKind, dataset.OriginReference, dataset.Classification, dataset.Summary.RowCountEstimate));
            Assert.Equal(DatasetsApplicationHarness.Start, dataset.LastRefreshedUtc);
        }

        [Theory]
        [InlineData(DataClassification.Confidential)]
        [InlineData(DataClassification.Restricted)]
        public async Task Preview_ConfidentialDataset_Refused(DataClassification classification)
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Secret", OriginKind.Upload, null, "[ { \"x\": \"1\" } ]", classification));

            var preview = await harness.SendAsync(new PreviewDatasetRowsQuery(id));

            Assert.Equal((false, "[]"), (preview.Allowed, preview.RowsJson));
            Assert.Contains(classification.ToString(), preview.Reason);

            var open = await harness.SendAsync(new RegisterDatasetCommand("Open", OriginKind.Upload, null, "[ { \"x\": \"1\" } ]", DataClassification.Public));
            Assert.True((await harness.SendAsync(new PreviewDatasetRowsQuery(open))).Allowed);
        }

        [Fact]
        public async Task Registrar_RegisterThenRefresh_SameDataset()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();

            var first = await harness.RegisterAsync(new DatasetRegistration("Crawl", OriginKind.PipelineOutput, "p1", "[ { \"a\": \"1\" } ]"));
            harness.Clock.Advance(TimeSpan.FromMinutes(5));
            var second = await harness.RegisterAsync(new DatasetRegistration("Crawl", OriginKind.PipelineOutput, "p1", "[ { \"a\": \"1\" }, { \"a\": \"2\" } ]"));

            Assert.Equal(first, second);
            var view = await harness.SendAsync(new GetDatasetQuery(first));
            Assert.Equal((2, DatasetsApplicationHarness.Start.AddMinutes(5)), (view!.Summary.RowCountEstimate, view.LastRefreshedUtc));
        }

        [Fact]
        public async Task Refresh_RecomputesTheSchema_AndAnUnknownIdThrows()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.Upload, null, "[ { \"a\": \"1\" } ]"));

            await harness.SendAsync(new RefreshDatasetCommand(id, "[ { \"a\": \"1\", \"b\": \"true\" } ]"));

            Assert.Equal(new[] { "a", "b" }, (await harness.SendAsync(new GetDatasetQuery(id)))!.Schema.Columns.Select(column => column.Name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new RefreshDatasetCommand(Guid.NewGuid(), "[ { \"a\": \"1\" } ]")));
        }

        [Fact]
        public async Task Delete_BlockedWhenReferenced()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync(services => services.AddScoped<IDatasetUsageProvider>(_ => new FakeDatasetUsage("dashboard 'Sales'")));
            var id = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.Upload, null, "[ { \"x\": \"1\" } ]"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new DeleteDatasetCommand(id)));

            Assert.Contains("dashboard 'Sales'", ex.Message);
            Assert.Single(await harness.SendAsync(new ListDatasetsQuery()));
        }

        [Fact]
        public async Task Delete_RemovesTheDataset_AndIsIdempotent()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.Upload, null, "[ { \"x\": \"1\" } ]"));

            await harness.SendAsync(new DeleteDatasetCommand(id));
            await harness.SendAsync(new DeleteDatasetCommand(id));

            Assert.Empty(await harness.SendAsync(new ListDatasetsQuery()));
            Assert.Null(await harness.SendAsync(new GetDatasetQuery(id)));
        }

        // ---- DatasetClassificationTests (4) ------------------------------------------------------------------------

        [Fact]
        public async Task Refresh_EscalatesClassification_WhenALaterSampleIntroducesSensitiveData()
        {
            var classifier = new SettableClassifier(DataClassification.Internal);
            await using var harness = await DatasetsApplicationHarness.StartAsync(classifier: classifier);

            var id = await harness.RegisterAsync(new DatasetRegistration("Customers", OriginKind.PipelineOutput, "p1", "[ { \"a\": \"1\" } ]"));
            Assert.Equal(DataClassification.Internal, (await harness.SendAsync(new GetDatasetQuery(id)))!.Classification);

            classifier.Result = DataClassification.Restricted;
            var refreshed = await harness.RegisterAsync(new DatasetRegistration("Customers", OriginKind.PipelineOutput, "p1", "[ { \"a\": \"a@b.com\" } ]"));

            Assert.Equal(id, refreshed);
            Assert.Equal(DataClassification.Restricted, (await harness.SendAsync(new GetDatasetQuery(id)))!.Classification);
        }

        [Fact]
        public async Task Refresh_DoesNotDowngradeClassification()
        {
            var classifier = new SettableClassifier(DataClassification.Confidential);
            await using var harness = await DatasetsApplicationHarness.StartAsync(classifier: classifier);

            var id = await harness.RegisterAsync(new DatasetRegistration("Ledger", OriginKind.Upload, null, "[ { \"password\": \"x\" } ]"));
            Assert.Equal(DataClassification.Confidential, (await harness.SendAsync(new GetDatasetQuery(id)))!.Classification);

            classifier.Result = DataClassification.Internal;
            await harness.RegisterAsync(new DatasetRegistration("Ledger", OriginKind.Upload, null, "[ { \"note\": \"ok\" } ]"));

            Assert.Equal(DataClassification.Confidential, (await harness.SendAsync(new GetDatasetQuery(id)))!.Classification);
        }

        [Fact]
        public async Task Register_EscalatesToDerivedClassification()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync(classifier: new SettableClassifier(DataClassification.Restricted));

            var id = await harness.RegisterAsync(new DatasetRegistration("Customers", OriginKind.PipelineOutput, "p1", "[ { \"email\": \"a@b.com\" } ]"));

            Assert.Equal(DataClassification.Restricted, (await harness.SendAsync(new GetDatasetQuery(id)))!.Classification);
        }

        [Fact]
        public async Task Register_DoesNotDowngradeExplicitClassification()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync(classifier: new SettableClassifier(DataClassification.Internal));

            var id = await harness.RegisterAsync(new DatasetRegistration("Secrets", OriginKind.Upload, null, "[ { \"x\": \"1\" } ]", DataClassification.Confidential));

            Assert.Equal(DataClassification.Confidential, (await harness.SendAsync(new GetDatasetQuery(id)))!.Classification);
        }

        // ---- DatasetCatalogTests (3) ---------------------------------------------------------------------------------

        [Fact]
        public async Task GetDescriptor_ReturnsSchemaAndSummary()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Sales", OriginKind.Upload, null, "[ { \"amount\": \"10\" } ]"));

            var descriptor = await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetCatalog>().GetDescriptorAsync(id));
            var rows = await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetCatalog>().GetPreviewRowsAsync(id));

            Assert.NotNull(descriptor);
            Assert.Contains(descriptor.Schema.Columns, column => column.Name == "amount");
            Assert.Contains(descriptor.Summary.Columns, column => column.Name == "amount" && column.Samples.Count > 0);
            Assert.True(rows.Allowed);
        }

        [Fact]
        public async Task GetDescriptor_UnknownDataset_ReturnsNull()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            Assert.Null(await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetCatalog>().GetDescriptorAsync(Guid.NewGuid())));
        }

        [Fact]
        public async Task SensitiveDataset_MasksSummaryAndRefusesRows()
        {
            await using var harness = await DatasetsApplicationHarness.StartAsync();
            var id = await harness.SendAsync(new RegisterDatasetCommand("Secret", OriginKind.Upload, null, "[ { \"ssn\": \"123\" } ]", DataClassification.Confidential));

            var descriptor = await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetCatalog>().GetDescriptorAsync(id));
            var rows = await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetCatalog>().GetPreviewRowsAsync(id));

            Assert.Equal(DataClassification.Confidential, descriptor!.Classification);
            Assert.All(descriptor.Summary.Columns, column => Assert.Empty(column.Samples));
            Assert.Equal((false, "[]"), (rows.Allowed, rows.RowsJson));
        }
    }
}
