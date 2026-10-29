#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The Taiga connector's live tests' server: TAIGA_TEST_URL / TAIGA_TEST_USERNAME / TAIGA_TEST_PASSWORD where set,
    /// otherwise this machine's throwaway Taiga 6.10 that <c>scripts/taiga-test-server.ps1</c> makes on 127.0.0.1:18092 - its user
    /// kafe, registered by that script, and the password in the file it writes under the user's temp folder. Neither is a
    /// FAILURE that says how to get one, never a skip. A test makes a project of its own here (the connector makes none, as
    /// n8n's node makes none) and deletes it again.
    /// </summary>
    internal static class TaigaTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("TAIGA_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18092";

        public static string Username => Environment.GetEnvironmentVariable("TAIGA_TEST_USERNAME") is { Length: > 0 } given ? given : "kafe";

        public static string Password => Environment.GetEnvironmentVariable("TAIGA_TEST_PASSWORD") is { Length: > 0 } given ? given : Read("password");

        public static async Task<long> MakeProjectAsync(string name)
        {
            using var client = await ClientAsync();
            using var made = await client.PostAsync("projects", Json(new JsonObject { ["name"] = name, ["description"] = "live test", ["is_issues_activated"] = true }));
            made.EnsureSuccessStatusCode();
            return JsonNode.Parse(await made.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        }

        public static async Task DeleteProjectAsync(long project)
        {
            using var client = await ClientAsync();
            using var deleted = await client.DeleteAsync($"projects/{project}");
        }

        private static async Task<HttpClient> ClientAsync()
        {
            var client = new HttpClient { BaseAddress = new Uri(Endpoint + "/api/v1/"), Timeout = TimeSpan.FromSeconds(60) };
            using var signedIn = await client.PostAsync("auth", Json(new JsonObject { ["type"] = "normal", ["username"] = Username, ["password"] = Password }));
            signedIn.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonNode.Parse(await signedIn.Content.ReadAsStringAsync())!["auth_token"]!.GetValue<string>());
            return client;
        }

        // A body of a known length: Taiga's server answers 400 to a chunked one (PostAsJsonAsync's, measured).
        private static StringContent Json(JsonObject body) => new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-taiga-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No Taiga test server: run 'pwsh scripts/taiga-test-server.ps1' (a throwaway Taiga 6.10 on 127.0.0.1:18092, its user's password under {Path.GetDirectoryName(file)}) or set TAIGA_TEST_URL, TAIGA_TEST_USERNAME and TAIGA_TEST_PASSWORD.");
        }
    }
}
