using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Datasets.Services
{
    /// <summary>
    /// The Datasets module's client contract (BL-94 increment 10). No method takes a site: the request is the site, and the
    /// server decides every call again on its key. A list or a get carries the masked summary and never raw rows; a
    /// preview is the higher key and comes back refused, not thrown, for a sensitive dataset.
    /// </summary>
    public interface IDatasetsService
    {
        Task<List<DatasetDescriptor>> ListAsync();

        Task<DatasetDescriptor> GetAsync(Guid id);

        Task<DatasetPreviewRows> PreviewAsync(Guid id);

        Task<DatasetDescriptor> RegisterAsync(RegisterDatasetRequest request);

        /// <summary>A dataset from a file the file manager holds, opened as the caller (increment 13).</summary>
        Task<DatasetDescriptor> UploadAsync(UploadDatasetRequest request);

        Task<DatasetDescriptor> RefreshAsync(Guid id, RefreshDatasetRequest request);

        Task DeleteAsync(Guid id);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Datasets</c>.</summary>
    public class DatasetsService : ServiceBase, IDatasetsService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason, which this module's page shows.</summary>
        protected override bool ThrowOnRefusal => true;

        public DatasetsService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Datasets");

        public async Task<List<DatasetDescriptor>> ListAsync() => await GetJsonAsync<List<DatasetDescriptor>>(ApiUrl) ?? new List<DatasetDescriptor>();

        public Task<DatasetDescriptor> GetAsync(Guid id) => GetJsonAsync<DatasetDescriptor>($"{ApiUrl}/{id}");

        public Task<DatasetPreviewRows> PreviewAsync(Guid id) => GetJsonAsync<DatasetPreviewRows>($"{ApiUrl}/{id}/preview");

        public Task<DatasetDescriptor> RegisterAsync(RegisterDatasetRequest request) => PostJsonAsync<RegisterDatasetRequest, DatasetDescriptor>(ApiUrl, request);

        public Task<DatasetDescriptor> UploadAsync(UploadDatasetRequest request) => PostJsonAsync<UploadDatasetRequest, DatasetDescriptor>($"{ApiUrl}/upload", request);

        public Task<DatasetDescriptor> RefreshAsync(Guid id, RefreshDatasetRequest request) => PostJsonAsync<RefreshDatasetRequest, DatasetDescriptor>($"{ApiUrl}/{id}/refresh", request);

        public Task DeleteAsync(Guid id) => DeleteAsync($"{ApiUrl}/{id}");
    }
}
