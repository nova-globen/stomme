#nullable enable
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// BL-235 - a success answer a connector cannot read is a failure, never an empty answer: a sign-in page, a proxy's or
    /// Cloudflare Access's HTML under HTTP 200 used to pass a connection test that asked only for the status.
    /// </summary>
    public static class JsonAnswers
    {
        /// <summary>The failure a success answer that is not JSON is, naming its likely cause; null when the answer is JSON or empty.</summary>
        public static async Task<string?> NotJsonAsync(HttpResponseMessage response, string service, CancellationToken cancellationToken)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                return null;
            }
            catch (JsonException)
            {
                return $"{service} answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.";
            }
        }
    }
}
