using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Services
{
    /// <summary>
    /// Service for the dead-letter queue admin surface (<see cref="DeadLetterStatus"/> /
    /// <see cref="DeadLetterItem"/>) — list the messages that exhausted delivery attempts, retry one, or
    /// discard it. Host-only server-side; <see cref="GetStatusAsync"/> answers even when the bus is off,
    /// so callers ask it first rather than reading an empty list as "no dead letters".
    /// </summary>
    public interface IDeadLetterService
    {
        /// <summary>Whether messaging is enabled, whether a real DLQ backs it, and the current count.</summary>
        Task<DeadLetterStatus> GetStatusAsync();

        /// <summary>The dead-lettered messages, newest information the transport keeps.</summary>
        Task<List<DeadLetterItem>> GetDeadLettersAsync(int max = 100);

        /// <summary>Returns the message to the input queue for another delivery attempt.</summary>
        Task RetryAsync(string id);

        /// <summary>Permanently removes the message from the dead-letter queue.</summary>
        Task DiscardAsync(string id);
    }

    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class DeadLetterService : ServiceBase, IDeadLetterService
    {
        public DeadLetterService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string Apiurl => CreateApiUrl("DeadLetter");

        public async Task<DeadLetterStatus> GetStatusAsync()
        {
            return await GetJsonAsync<DeadLetterStatus>($"{Apiurl}/status");
        }

        public async Task<List<DeadLetterItem>> GetDeadLettersAsync(int max = 100)
        {
            return await GetJsonAsync<List<DeadLetterItem>>($"{Apiurl}?max={max}");
        }

        public async Task RetryAsync(string id)
        {
            await PostJsonAsync($"{Apiurl}/retry", new DeadLetterAction { Id = id });
        }

        public async Task DiscardAsync(string id)
        {
            await PostJsonAsync($"{Apiurl}/discard", new DeadLetterAction { Id = id });
        }

        // The id travels in the body — transport ids contain slashes.
        private class DeadLetterAction
        {
            public string Id { get; set; }
        }
    }
}
