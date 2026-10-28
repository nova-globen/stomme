using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Services;
using Stomme.Shared;
using GuestbookEntry = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Services
{
    /// <summary>
    /// The client/server contract the Guestbook UI consumes. The component depends on this interface, never
    /// on a repository — data access from a Blazor component goes through an <c>IService</c> over HTTP
    /// (CLAUDE.md), so the same component renders on Static SSR, Server and WebAssembly. The server binds it
    /// to <c>ServerGuestbookService</c> (mediator dispatch); the client binds it to
    /// <see cref="GuestbookService"/> (HTTP to the controller).
    /// </summary>
    public interface IGuestbookService
    {
        Task<List<GuestbookEntry>> ListEntriesAsync(int moduleId);

        Task<GuestbookEntry> SignAsync(GuestbookEntry entry);

        /// <summary>Streams the book's signatures incrementally — the mediator's stream-request path.</summary>
        IAsyncEnumerable<GuestbookEntry> StreamEntries(int moduleId, CancellationToken ct = default);
    }

    /// <summary>
    /// The WebAssembly/browser implementation: calls the Guestbook Web API over HTTP. Marked
    /// <see cref="IClientService"/> so the framework registers it as <see cref="IGuestbookService"/> for the
    /// client, the same convention every client service uses.
    /// </summary>
    public class GuestbookService : ServiceBase, IGuestbookService, IClientService
    {
        public GuestbookService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Guestbook");

        public async Task<List<GuestbookEntry>> ListEntriesAsync(int moduleId) =>
            await GetJsonAsync<List<GuestbookEntry>>($"{ApiUrl}?moduleid={moduleId}") ?? new List<GuestbookEntry>();

        public async Task<GuestbookEntry> SignAsync(GuestbookEntry entry) =>
            await PostJsonAsync($"{ApiUrl}", entry);

        public async IAsyncEnumerable<GuestbookEntry> StreamEntries(
            int moduleId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var stream = GetHttpClient()
                .GetFromJsonAsAsyncEnumerable<GuestbookEntry>($"{ApiUrl}/stream?moduleid={moduleId}", ct);
            await foreach (var entry in stream.WithCancellation(ct))
            {
                if (entry != null)
                {
                    yield return entry;
                }
            }
        }
    }
}
