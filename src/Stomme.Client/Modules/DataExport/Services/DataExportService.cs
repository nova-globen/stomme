using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models.Export;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.DataExport.Services
{
    public interface IDataExportService
    {
        /// <summary>The sources THIS user may export from.</summary>
        Task<List<ExportSourceView>> GetSourcesAsync();

        Task<List<ExportJobView>> GetMyExportsAsync();

        /// <summary>Every export on the site. Needs <c>export:manage</c>.</summary>
        Task<List<ExportJobView>> GetSiteExportsAsync();

        /// <returns>The new export's id, or <see cref="Guid.Empty"/> when the server refused the request.</returns>
        Task<Guid> RequestAsync(ExportRequest request);

        Task CancelAsync(Guid exportId);

        /// <summary>Where the browser fetches the artifact from. The server authorises that request itself.</summary>
        string DownloadUrl(Guid exportId);

        /// <summary>The caller's schedules; every schedule of the site for a manager.</summary>
        Task<List<ExportScheduleView>> GetSchedulesAsync();

        /// <summary>A refusal throws with the server's reason.</summary>
        Task<ExportScheduleView> CreateScheduleAsync(ExportScheduleRequest schedule);

        Task PauseScheduleAsync(Guid scheduleId);

        Task ResumeScheduleAsync(Guid scheduleId);

        Task DeleteScheduleAsync(Guid scheduleId);

        /// <summary>A manager becomes the owner every later run is requested as.</summary>
        Task TakeOverScheduleAsync(Guid scheduleId);
    }

    /// <summary>The client half: every call is an HTTP request the server authorises on its own terms.</summary>
    public class DataExportService : ServiceBase, IDataExportService, IClientService
    {
        /// <summary>A refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public DataExportService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("DataExport");

        public async Task<List<ExportSourceView>> GetSourcesAsync() =>
            await GetJsonAsync<List<ExportSourceView>>($"{ApiUrl}/sources") ?? new List<ExportSourceView>();

        public async Task<List<ExportJobView>> GetMyExportsAsync() =>
            await GetJsonAsync<List<ExportJobView>>($"{ApiUrl}/mine") ?? new List<ExportJobView>();

        public async Task<List<ExportJobView>> GetSiteExportsAsync() =>
            await GetJsonAsync<List<ExportJobView>>($"{ApiUrl}/site") ?? new List<ExportJobView>();

        public Task<Guid> RequestAsync(ExportRequest request) =>
            PostJsonAsync<ExportRequest, Guid>(ApiUrl, request);

        public Task CancelAsync(Guid exportId) =>
            PostAsync($"{ApiUrl}/{exportId}/cancel");

        public string DownloadUrl(Guid exportId) => $"{ApiUrl}/{exportId}/download";

        public async Task<List<ExportScheduleView>> GetSchedulesAsync() =>
            await GetJsonAsync<List<ExportScheduleView>>($"{ApiUrl}/schedules") ?? new List<ExportScheduleView>();

        public Task<ExportScheduleView> CreateScheduleAsync(ExportScheduleRequest schedule) =>
            PostJsonAsync<ExportScheduleRequest, ExportScheduleView>($"{ApiUrl}/schedules", schedule);

        public Task PauseScheduleAsync(Guid scheduleId) => PostAsync($"{ApiUrl}/schedules/{scheduleId}/pause");

        public Task ResumeScheduleAsync(Guid scheduleId) => PostAsync($"{ApiUrl}/schedules/{scheduleId}/resume");

        public Task DeleteScheduleAsync(Guid scheduleId) => DeleteAsync($"{ApiUrl}/schedules/{scheduleId}");

        public Task TakeOverScheduleAsync(Guid scheduleId) => PostAsync($"{ApiUrl}/schedules/{scheduleId}/takeover");
    }
}
