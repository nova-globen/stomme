#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The SeaTable connector's live tests' server: SEATABLE_TEST_URL / SEATABLE_TEST_TOKEN where set, otherwise this
    /// machine's throwaway SeaTable 6.2 that <c>scripts/seatable-test-server.ps1</c> makes on 127.0.0.1:18098 - the API token of its
    /// base Kafe in the file that script writes under the user's temp folder. Neither is a FAILURE that says how to get one, never a
    /// skip. A test makes the tables it needs over SeaTable's API - n8n's node makes none - and deletes them afterwards.
    /// </summary>
    internal static class SeaTableTestEndpoint
    {
        public static string Endpoint => Environment.GetEnvironmentVariable("SEATABLE_TEST_URL") is { Length: > 0 } url ? url.TrimEnd('/') : "http://127.0.0.1:18098";

        public static string Token => Environment.GetEnvironmentVariable("SEATABLE_TEST_TOKEN") is { Length: > 0 } given ? given : Read("token");

        private static string Read(string name)
        {
            var file = Path.Combine(Path.GetTempPath(), "stomme-seatable-test", name);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"No SeaTable test server: run 'pwsh scripts/seatable-test-server.ps1' (a throwaway SeaTable 6.2 on 127.0.0.1:18098, its base's API token under {Path.GetDirectoryName(file)}) or set SEATABLE_TEST_URL and SEATABLE_TEST_TOKEN.");
        }

        /// <summary>Orders - Name, Qty, Paid, Due, Kind (Kaffe or Bulle) and a link, Customer, to Customers - made, deleted when disposed.</summary>
        public static async Task<Tables> TablesAsync(string orders, string customers)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var token = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/api/v2.1/dtable/app-access-token/");
            token.Headers.TryAddWithoutValidation("Authorization", "Token " + Token);
            var access = JsonNode.Parse(await (await client.SendAsync(token)).EnsureSuccessStatusCode().Content.ReadAsStringAsync())!;
            var tables = new Tables(client, $"{Endpoint}/api-gateway/api/v2/dtables/{access["dtable_uuid"]!.GetValue<string>()}/", access["access_token"]!.GetValue<string>(), orders, customers);
            await tables.SendAsync(HttpMethod.Post, "tables/", new JsonObject { ["table_name"] = customers, ["columns"] = new JsonArray(new JsonObject { ["column_name"] = "Name", ["column_type"] = "text" }) });
            await tables.SendAsync(HttpMethod.Post, "tables/", new JsonObject
            {
                ["table_name"] = orders,
                ["columns"] = new JsonArray(
                    new JsonObject { ["column_name"] = "Name", ["column_type"] = "text" }, new JsonObject { ["column_name"] = "Qty", ["column_type"] = "number" },
                    new JsonObject { ["column_name"] = "Paid", ["column_type"] = "checkbox" }, new JsonObject { ["column_name"] = "Due", ["column_type"] = "date" }),
            });
            await tables.SendAsync(HttpMethod.Post, "columns/", new JsonObject { ["table_name"] = orders, ["column_name"] = "Kind", ["column_type"] = "single-select" });
            await tables.SendAsync(HttpMethod.Post, "column-options/", new JsonObject
            {
                ["table_name"] = orders, ["column"] = "Kind",
                ["options"] = new JsonArray(new JsonObject { ["name"] = "Kaffe", ["color"] = "#aaaaaa", ["textColor"] = "#000000" }, new JsonObject { ["name"] = "Bulle", ["color"] = "#bbbbbb", ["textColor"] = "#000000" }),
            });
            await tables.SendAsync(HttpMethod.Post, "columns/", new JsonObject { ["table_name"] = orders, ["column_name"] = "Customer", ["column_type"] = "link", ["column_data"] = new JsonObject { ["table"] = orders, ["other_table"] = customers } });
            return tables;
        }

        internal sealed class Tables(HttpClient client, string gateway, string access, string orders, string customers) : IAsyncDisposable
        {
            public async Task SendAsync(HttpMethod method, string route, JsonObject body)
            {
                using var request = new HttpRequestMessage(method, gateway + route) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Authorization", "Token " + access);
                (await client.SendAsync(request)).EnsureSuccessStatusCode();
            }

            public async ValueTask DisposeAsync()
            {
                foreach (var table in new[] { orders, customers })
                {
                    await SendAsync(HttpMethod.Delete, "tables/", new JsonObject { ["table_name"] = table });
                }

                client.Dispose();
            }
        }
    }
}
