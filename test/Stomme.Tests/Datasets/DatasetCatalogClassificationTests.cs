#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Models;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The shared read port over the module's real startup on the File store: for
    /// Confidential AND Restricted data the summary is masked and raw rows are refused; Internal and Public are served
    /// in full; a dataset of another site is not found and cannot be previewed. The cases below cover the masking,
    /// the refusal of unknown datasets and the refusal of rows; refusing rows for Confidential alone would leave
    /// Restricted data readable.
    /// </summary>
    public sealed class DatasetCatalogClassificationTests : IAsyncLifetime
    {
        private const int Site = 1;
        private const int OtherSite = 2;
        private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-datasets-catalog-{Guid.NewGuid():N}");
        private ServiceProvider _services = default!;

        public Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());
            services.AddScoped(_ => new SiteState { Alias = new Alias { AliasId = 1, TenantId = 1, SiteId = Site, Name = "localhost" } });
            new Stomme.Modules.Datasets.Startup.ServerStartup().ConfigureServices(services);
            _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private async Task<Guid> SeedAsync(string name, DataClassification classification, int siteId = Site)
        {
            await using var scope = _services.CreateAsyncScope();
            var inference = SchemaInference.Infer("[ { \"ssn\": \"123-45-6789\", \"amount\": \"10\" } ]");
            var dataset = DatasetAggregate.Register(TenantId.Default, siteId, name, OriginKind.Upload, null, classification, inference.Schema, inference.Summary, inference.SampleRowsJson, Now);
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddAsync(dataset);
            return dataset.Id;
        }

        private async Task<T> InCatalogAsync<T>(Func<IDatasetCatalog, Task<T>> work)
        {
            await using var scope = _services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<IDatasetCatalog>());
        }

        [Theory]
        [InlineData(DataClassification.Confidential)]
        [InlineData(DataClassification.Restricted)]
        public async Task SensitiveDataset_MasksSummaryAndRefusesRows(DataClassification classification)
        {
            var id = await SeedAsync("Secret " + classification, classification);

            var descriptor = await InCatalogAsync(catalog => catalog.GetDescriptorAsync(id));
            var rows = await InCatalogAsync(catalog => catalog.GetPreviewRowsAsync(id));

            Assert.NotNull(descriptor);
            Assert.Equal(classification, descriptor.Classification);
            Assert.True(descriptor.IsSensitive);
            Assert.Equal(new[] { "ssn", "amount" }, descriptor.Schema.Columns.Select(column => column.Name)); // the schema is published
            Assert.All(descriptor.Summary.Columns, column => Assert.Empty(column.Samples));
            Assert.All(descriptor.Summary.Columns, column => Assert.Null(column.Max));
            Assert.Equal(1, descriptor.Summary.RowCountEstimate);
            Assert.Equal((false, "[]"), (rows.Allowed, rows.RowsJson));
            Assert.Contains(classification.ToString(), rows.Reason);
            Assert.DoesNotContain("123-45-6789", System.Text.Json.JsonSerializer.Serialize(descriptor) + rows.RowsJson);
        }

        [Theory]
        [InlineData(DataClassification.Public)]
        [InlineData(DataClassification.Internal)]
        public async Task An_open_dataset_is_served_in_full(DataClassification classification)
        {
            var id = await SeedAsync("Open " + classification, classification);

            var descriptor = await InCatalogAsync(catalog => catalog.GetDescriptorAsync(id));
            var rows = await InCatalogAsync(catalog => catalog.GetPreviewRowsAsync(id));

            Assert.NotNull(descriptor);
            Assert.False(descriptor.IsSensitive);
            Assert.Equal(new[] { "123-45-6789" }, descriptor.Summary.Columns.First(column => column.Name == "ssn").Samples);
            Assert.Equal(("10", "10"), (descriptor.Summary.Columns.First(column => column.Name == "amount").Min, descriptor.Summary.Columns.First(column => column.Name == "amount").Max));
            Assert.True(rows.Allowed);
            Assert.Contains("123-45-6789", rows.RowsJson);
        }

        [Fact]
        public async Task GetDescriptor_UnknownDataset_ReturnsNull_AndAnotherSitesIsNotFound()
        {
            var theirs = await SeedAsync("Theirs", DataClassification.Public, OtherSite);
            await SeedAsync("Mine", DataClassification.Public);

            Assert.Null(await InCatalogAsync(catalog => catalog.GetDescriptorAsync(Guid.NewGuid())));
            Assert.Null(await InCatalogAsync(catalog => catalog.GetDescriptorAsync(theirs)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => InCatalogAsync(catalog => catalog.GetPreviewRowsAsync(theirs)));
            Assert.Equal(new[] { "Mine" }, (await InCatalogAsync(catalog => catalog.ListDescriptorsAsync())).Select(descriptor => descriptor.Name));
        }
    }
}
