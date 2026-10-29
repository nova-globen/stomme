#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Shared;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>
    /// The Shared read port's implementation: the REQUEST's site's datasets with their schema and
    /// classification-masked summary, and the bounded preview - refused for Confidential AND Restricted data (Q1). A
    /// dataset of another site is not found. The masking and the refusal are the aggregate's, so the rule lives once.
    /// </summary>
    public sealed class DatasetCatalog(IDatasetRepository datasets, SiteState siteState) : IDatasetCatalog
    {
        public async Task<IReadOnlyList<DatasetDescriptor>> ListDescriptorsAsync(CancellationToken ct = default)
        {
            if (siteState.Alias?.SiteId is not { } siteId)
            {
                return Array.Empty<DatasetDescriptor>();
            }

            return (await datasets.ListForSiteAsync(siteId, ct)).Select(dataset => dataset.ToDescriptor()).ToList();
        }

        public async Task<DatasetDescriptor?> GetDescriptorAsync(Guid datasetId, CancellationToken ct = default)
        {
            var dataset = await datasets.GetByIdAsync(datasetId, ct);
            return dataset is null || dataset.SiteId != siteState.Alias?.SiteId ? null : dataset.ToDescriptor();
        }

        public async Task<DatasetPreviewRows> GetPreviewRowsAsync(Guid datasetId, CancellationToken ct = default)
        {
            var dataset = await datasets.GetByIdAsync(datasetId, ct);
            if (dataset is null || dataset.SiteId != siteState.Alias?.SiteId)
            {
                throw new InvalidOperationException($"Dataset '{datasetId}' does not exist.");
            }

            return dataset.RawRowsAllowed
                ? new DatasetPreviewRows(true, dataset.SampleRowsJson, "OK")
                : new DatasetPreviewRows(false, "[]", DatasetClassificationPolicy.RawRowsRefusal(dataset.Classification));
        }
    }
}
