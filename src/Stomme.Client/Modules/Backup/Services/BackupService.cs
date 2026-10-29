using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Modules.Backup.Services
{
    /// <summary>
    /// The backup contract (BL-81, D106 item 9). Everything here is the Host's; the server decides every call again. A
    /// passphrase travels in a request BODY - never a URL - and is never kept.
    /// </summary>
    public interface IBackupService
    {
        /// <summary>Whether backup is switched on here (<c>Backup:Enabled</c>).</summary>
        Task<bool> IsEnabledAsync();

        Task<List<BackupSnapshotView>> ListAsync();

        /// <summary>Takes a snapshot - queued; the row appears as Creating and becomes Available.</summary>
        Task<BackupOperationResult> CreateAsync(BackupPassphraseRequest request);

        /// <summary>Pre-flight and stage a restore - queued; applied when the host restarts.</summary>
        Task<BackupOperationResult> RestoreAsync(Guid snapshotId, BackupPassphraseRequest request);

        /// <summary>What the worker decided about a queued restore; null when nothing was asked.</summary>
        Task<BackupOperationResult> RestoreReportAsync(Guid snapshotId);

        Task DeleteAsync(Guid snapshotId);

        /// <summary>Brings in an archive taken elsewhere; it is verified end to end before it is listed.</summary>
        Task<BackupOperationResult> ImportAsync(Stream archive, string passphrase);

        Task<List<BackupScheduleView>> SchedulesAsync();

        Task<BackupScheduleView> SaveScheduleAsync(BackupScheduleView view);
    }

    /// <summary>The BROWSER side: HTTP to <c>api/Backup</c>. A refused request comes back as a result carrying the server's words.</summary>
    public class BackupService : ServiceBase, IBackupService, IClientService
    {
        /// <summary>BL-222: a refusal throws with the server's reason; this module's pages catch it, where a null read as success on a WebAssembly site.</summary>
        protected override bool ThrowOnRefusal => true;

        public BackupService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Backup");

        /// <summary>The API answers 404 on every route while switched off.</summary>
        public async Task<bool> IsEnabledAsync()
        {
            using var response = await GetHttpClient().GetAsync(ApiUrl);
            return response.StatusCode != HttpStatusCode.NotFound;
        }

        public async Task<List<BackupSnapshotView>> ListAsync() =>
            await GetJsonAsync<List<BackupSnapshotView>>(ApiUrl) ?? new List<BackupSnapshotView>();

        public Task<BackupOperationResult> CreateAsync(BackupPassphraseRequest request) => SendAsync(HttpMethod.Post, ApiUrl, JsonContent.Create(request));

        public Task<BackupOperationResult> RestoreAsync(Guid snapshotId, BackupPassphraseRequest request) =>
            SendAsync(HttpMethod.Post, $"{ApiUrl}/{snapshotId}/restore", JsonContent.Create(request));

        public async Task<BackupOperationResult> RestoreReportAsync(Guid snapshotId)
        {
            using var response = await GetHttpClient().GetAsync($"{ApiUrl}/{snapshotId}/restore");
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BackupOperationResult>() : null;
        }

        public async Task DeleteAsync(Guid snapshotId)
        {
            using var response = await GetHttpClient().DeleteAsync($"{ApiUrl}/{snapshotId}");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            }
        }

        public Task<BackupOperationResult> ImportAsync(Stream archive, string passphrase)
        {
            var form = new MultipartFormDataContent
            {
                { new StreamContent(archive), "archive", "archive.stbak" },
                { new StringContent(passphrase ?? string.Empty), "passphrase" },
            };
            return SendAsync(HttpMethod.Post, $"{ApiUrl}/import", form);
        }

        public async Task<List<BackupScheduleView>> SchedulesAsync() =>
            await GetJsonAsync<List<BackupScheduleView>>($"{ApiUrl}/schedules") ?? new List<BackupScheduleView>();

        public async Task<BackupScheduleView> SaveScheduleAsync(BackupScheduleView view)
        {
            using var response = await GetHttpClient().PutAsJsonAsync($"{ApiUrl}/schedules", view);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            }

            return await response.Content.ReadFromJsonAsync<BackupScheduleView>();
        }

        public string DownloadUrl(Guid snapshotId) => $"{ApiUrl}/{snapshotId}/download";

        private async Task<BackupOperationResult> SendAsync(HttpMethod method, string url, HttpContent content)
        {
            using (content)
            using (var request = new HttpRequestMessage(method, url) { Content = content })
            using (var response = await GetHttpClient().SendAsync(request))
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<BackupOperationResult>() ?? new BackupOperationResult();
                }

                var reason = response.StatusCode == HttpStatusCode.BadRequest ? await response.Content.ReadAsStringAsync() : $"The server refused the request ({(int)response.StatusCode}).";
                return new BackupOperationResult { Accepted = false, Problems = new List<string> { reason }, Completed = true };
            }
        }
    }
}
