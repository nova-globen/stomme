#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Modules.Datasets.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The predecessor platform's upload cases by name (DatasetTests:
    /// <c>RegisterUploadedDataset_MaterializesFile_AsUploadOriginDataset</c>, <c>RegisterUploadedDataset_ExplicitName_OverridesTheFileName</c>;
    /// <c>RegisterUploadedDataset_WithoutSourceWired_Throws</c> is CHANGED - the module's startup always wires the source, so the
    /// case here is a refusal from the source registering nothing) over the module's real startup on the File store, with
    /// the managed-file source swapped for a fake: the row count is the FILE's, not the sample's; the origin is
    /// <c>file:&lt;id&gt;</c> so the same file registered again refreshes its one row; a parser refusal registers nothing;
    /// a file id of zero fails validation before anything is read.
    /// </summary>
    public sealed class UploadedDatasetCommandTests
    {
        [Fact]
        public async Task An_upload_registers_a_dataset_named_after_the_file_with_the_files_full_row_count()
        {
            var source = new FakeUploadedDatasetSource();
            source.Files[7] = new UploadedDatasetContent("sales", "[{\"sku\":\"A1\",\"amount\":\"10\"},{\"sku\":\"A2\",\"amount\":\"20\"}]", 120_000, UploadFormat.Csv);
            await using var harness = await DatasetsApplicationHarness.StartAsync(services =>
            {
                services.RemoveAll<IUploadedDatasetSource>();
                services.AddSingleton<IUploadedDatasetSource>(source);
            });
            var id = await harness.SendAsync(new RegisterUploadedDatasetCommand(7));
            var dataset = (await harness.SendAsync(new GetDatasetQuery(id)))!;
            Assert.Equal(("sales", OriginKind.Upload, "file:7", 120_000, DataClassification.Internal), (dataset.Name, dataset.OriginKind, dataset.OriginReference, dataset.Summary.RowCountEstimate, dataset.Classification));
            Assert.Equal(new[] { "sku", "amount" }, dataset.Schema.Columns.Select(column => column.Name));
            Assert.Equal(2, (await harness.SendAsync(new PreviewDatasetRowsQuery(id))).RowsJson!.Count(c => c == '{'));
        }

        [Fact]
        public async Task The_same_file_registered_again_refreshes_the_one_row_and_an_explicit_name_and_classification_are_kept()
        {
            var source = new FakeUploadedDatasetSource();
            source.Files[3] = new UploadedDatasetContent("people", "[{\"age\":\"30\"}]", 1, UploadFormat.Xlsx);
            await using var harness = await DatasetsApplicationHarness.StartAsync(services =>
            {
                services.RemoveAll<IUploadedDatasetSource>();
                services.AddSingleton<IUploadedDatasetSource>(source);
            });
            var first = await harness.SendAsync(new RegisterUploadedDatasetCommand(3, "People", DataClassification.Confidential));
            source.Files[3] = new UploadedDatasetContent("people", "[{\"age\":\"30\",\"city\":\"Oslo\"}]", 5, UploadFormat.Xlsx);
            var second = await harness.SendAsync(new RegisterUploadedDatasetCommand(3, "People", DataClassification.Internal));

            Assert.Equal(first, second);
            var dataset = (await harness.SendAsync(new GetDatasetQuery(first)))!;
            Assert.Equal(("People", 5, 2, DataClassification.Confidential), (dataset.Name, dataset.Summary.RowCountEstimate, dataset.Schema.Columns.Count, dataset.Classification)); // never lowered
            Assert.Single(await harness.SendAsync(new ListDatasetsQuery()));
        }

        [Fact]
        public async Task A_refused_file_registers_nothing_and_a_zero_file_id_fails_validation_before_anything_is_read()
        {
            var source = new FakeUploadedDatasetSource();
            await using var harness = await DatasetsApplicationHarness.StartAsync(services =>
            {
                services.RemoveAll<IUploadedDatasetSource>();
                services.AddSingleton<IUploadedDatasetSource>(source);
            });
            source.Refusal = new DatasetUploadRefusedException("The file is an XLSX package named .csv.");
            var refused = await Assert.ThrowsAsync<DatasetUploadRefusedException>(() => harness.SendAsync(new RegisterUploadedDatasetCommand(9)));
            Assert.Contains("package", refused.Message);

            await Assert.ThrowsAsync<ValidationException>(() => harness.SendAsync(new RegisterUploadedDatasetCommand(0)));
            Assert.Empty(await harness.SendAsync(new ListDatasetsQuery()));
            Assert.Equal(1, source.Reads); // the refused read; the invalid command never reached the source
        }

        private sealed class FakeUploadedDatasetSource : IUploadedDatasetSource
        {
            public Dictionary<int, UploadedDatasetContent> Files { get; } = new();

            public Exception? Refusal { get; set; }

            public int Reads { get; private set; }

            public Task<UploadedDatasetContent> ReadAsync(int fileId, CancellationToken ct = default)
            {
                Reads++;
                if (Refusal is not null)
                {
                    throw Refusal;
                }

                return Task.FromResult(Files.TryGetValue(fileId, out var content) ? content : throw new System.IO.FileNotFoundException($"File '{fileId}' does not exist."));
            }
        }
    }
}
