using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Services
{
    /// <summary>
    /// The outcome of one licensing call: the HTTP status, the server's message (a ProblemDetails <c>detail</c>) on a
    /// failure, and the fresh <see cref="LicenceStatus"/> on a success. The admin page needs the status itself - a 404
    /// means "licensing is not enabled", a 400 carries the verifier's reason, a 409 names the source a key comes from and a
    /// 500 names the settings file that could not be written - which the swallowing <see cref="ServiceBase"/> helpers hide.
    /// </summary>
    public class LicenceResult
    {
        public HttpStatusCode StatusCode { get; set; }

        public bool Succeeded => (int)StatusCode >= 200 && (int)StatusCode < 300;

        /// <summary>True when the server answered 404 to a status read: <c>Deployment:Mode</c> is not <c>OnPrem</c>.</summary>
        public bool NotEnabled => StatusCode == HttpStatusCode.NotFound;

        public string Message { get; set; } = string.Empty;

        public LicenceStatus Status { get; set; }
    }

    /// <summary>
    /// Service for the Host-only licensing surface (<c>api/Licence</c>, D105): read the status of every configured licence
    /// key, add a pasted key, remove one. Every call is Host-only server-side and answers 404 unless the installation runs
    /// with <c>Deployment:Mode=OnPrem</c>. Keys are never logged by this service.
    /// </summary>
    public interface ILicenceService
    {
        /// <summary>The installation id and every configured key's reason and source.</summary>
        Task<LicenceResult> GetStatusAsync();

        /// <summary>Verifies, saves and reads back a pasted key; on failure <see cref="LicenceResult.Message"/> says why.</summary>
        Task<LicenceResult> AddKeyAsync(string key);

        /// <summary>Removes the key stored under <paramref name="name"/> (its lid) when its source is writable.</summary>
        Task<LicenceResult> DeleteKeyAsync(string name);
    }

    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class LicenceService : ServiceBase, ILicenceService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public LicenceService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string Apiurl => CreateApiUrl("Licence");

        public Task<LicenceResult> GetStatusAsync() =>
            SendAsync(new HttpRequestMessage(HttpMethod.Get, Apiurl));

        public Task<LicenceResult> AddKeyAsync(string key) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Post, Apiurl)
            {
                Content = JsonContent.Create(new LicenceKeySubmission { Key = key ?? string.Empty })
            });

        public Task<LicenceResult> DeleteKeyAsync(string name) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Apiurl}/{WebUtility.UrlEncode(name)}"));

        // HttpClient used directly: the ServiceBase helpers log the request and swallow the status, and a request body
        // here is a licence key, which must never reach a log.
        private async Task<LicenceResult> SendAsync(HttpRequestMessage request)
        {
            using (request)
            {
                using var response = await GetHttpClient().SendAsync(request);
                var result = new LicenceResult { StatusCode = response.StatusCode };
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    result.Status = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<LicenceStatus>(body, Json);
                }
                else
                {
                    result.Message = ReadProblemDetail(body) ?? response.ReasonPhrase ?? response.StatusCode.ToString();
                }

                return result;
            }
        }

        private static string ReadProblemDetail(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "detail", "title" })
                    {
                        if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            return value.GetString();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // not JSON - fall through to the raw text
            }

            return body.Length <= 300 ? body : body.Substring(0, 300);
        }
    }
}