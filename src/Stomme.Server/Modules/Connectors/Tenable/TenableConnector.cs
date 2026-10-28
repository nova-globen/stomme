#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Tenable
{
    /// <summary>
    /// Tenable Vulnerability Management (BL-96 increment 5, lifted from the predecessor platform's <c>tenable</c>): the asset export started,
    /// polled until FINISHED within a bounded wait (the legacy loop was unbounded), every chunk streamed; the ApiKeyPair
    /// on the <c>X-ApiKeys</c> header in the exact legacy wire format. Every request through the host's egress (D110).
    /// </summary>
    public sealed class TenableConnector(Func<TimeSpan, CancellationToken, Task>? pollDelay = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://cloud.tenable.com";
        internal const string DefaultChunkSize = "1000";
        internal const string DefaultMaxWait = "00:10:00";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly Func<TimeSpan, CancellationToken, Task> pollDelay = pollDelay ?? Task.Delay;

        public string ConnectorType => "tenable";

        public string DisplayName => "Tenable Vulnerability Management";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri? WebsiteUrl => new("https://www.tenable.com/products/vulnerability-management");

        public Uri? ApiDocumentationUrl => new("https://developer.tenable.com/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("chunk_size", "Chunk size", ConnectorConfigFieldType.Integer, Description: "Assets per export chunk.")
            {
                Group = "Query",
                Default = DefaultChunkSize,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("assets", "Assets export", "Starts a Tenable asset export, waits for it to finish, and streams every chunk.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("chunk_size", "Chunk size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured chunk size for this run."),
                    new ConnectorConfigField("max_wait", "Maximum wait", ConnectorConfigFieldType.Duration, Description: "How long to wait for the export to finish before failing.") { Default = DefaultMaxWait },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = CreateClient(context.Egress, context.Timeout, ApiKeys(context.Credentials));
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/scans?limit=1")), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "Tenable", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"Tenable responded with {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail(FailureMessage(response.StatusCode));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!string.IsNullOrWhiteSpace(context.OperationName) && !string.Equals(context.OperationName, "assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Tenable connector.");
            }

            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var keys = ApiKeys(context.Credentials);
            var chunkSize = ChunkSize(context.Settings, context.Parameters);
            var maxWait = MaxWait(context.Parameters);

            using var client = CreateClient(context.Egress, context.Timeout, keys);
            var exportUuid = await StartExportAsync(client, baseUrl, chunkSize, cancellationToken);
            var chunks = await WaitForExportAsync(client, baseUrl, exportUuid, maxWait, cancellationToken);
            foreach (var chunkId in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await BearerClient.GetAsync(client, $"{baseUrl}/assets/export/{exportUuid}/chunks/{chunkId}", FailureMessage, cancellationToken);
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"Tenable chunk {chunkId} is not a JSON array.");
                }

                foreach (var asset in document.RootElement.EnumerateArray())
                {
                    yield return FlattenedRows.Flatten(asset);
                }
            }
        }

        private static async Task<string> StartExportAsync(HttpClient client, string baseUrl, int chunkSize, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["chunk_size"] = chunkSize }.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client,
                () => new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}/assets/export")) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(FailureMessage(response.StatusCode));
            }

            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("export_uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Tenable did not return an export_uuid.");
            }

            return uuid.GetString()!;
        }

        private async Task<List<long>> WaitForExportAsync(HttpClient client, string baseUrl, string exportUuid, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            // Bounded: a stuck export fails with a clear message instead of hanging.
            var waited = TimeSpan.Zero;
            while (true)
            {
                var payload = await BearerClient.GetAsync(client, $"{baseUrl}/assets/export/{exportUuid}/status", FailureMessage, cancellationToken);
                using var document = JsonDocument.Parse(payload);
                var status = document.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "UNKNOWN";
                if (string.Equals(status, "FINISHED", StringComparison.OrdinalIgnoreCase))
                {
                    var chunks = new List<long>();
                    if (document.RootElement.TryGetProperty("chunks_available", out var available) && available.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var chunk in available.EnumerateArray())
                        {
                            if (chunk.TryGetInt64(out var id))
                            {
                                chunks.Add(id);
                            }
                        }
                    }

                    return chunks;
                }

                if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The Tenable asset export ended in status {status}.");
                }

                if (waited >= maxWait)
                {
                    throw new InvalidOperationException($"The Tenable asset export did not finish within {maxWait} (last status: {status}).");
                }

                await pollDelay(PollInterval, cancellationToken);
                waited += PollInterval;
            }
        }

        private static (string AccessKey, string SecretKey) ApiKeys(IReadOnlyDictionary<string, string> credentials)
        {
            var accessKey = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Tenable connection is missing 'access-key' - check the connection's credentials.");
            var secretKey = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Tenable connection is missing 'secret-key' - check the connection's credentials.");
            return (accessKey, secretKey);
        }

        private static int ChunkSize(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "chunk_size") ?? Values.GetOrDefault(settings, "chunk_size", DefaultChunkSize);
            if (!int.TryParse(raw, out var size) || size <= 0)
            {
                throw new InvalidOperationException($"The chunk size '{raw}' is not a positive integer.");
            }

            return size;
        }

        private static TimeSpan MaxWait(IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.GetOrDefault(parameters, "max_wait", DefaultMaxWait);
            if (!TimeSpan.TryParse(raw, out var maxWait) || maxWait <= TimeSpan.Zero)
            {
                throw new InvalidOperationException($"The max_wait value '{raw}' is not a positive duration.");
            }

            return maxWait;
        }

        private static HttpClient CreateClient(IConnectorEgress egress, TimeSpan timeout, (string AccessKey, string SecretKey) keys)
        {
            var client = egress.CreateHttpClient(timeout);
            client.DefaultRequestHeaders.Add("X-ApiKeys", $"accessKey={keys.AccessKey}; secretKey={keys.SecretKey};"); // the exact legacy wire format, trailing semicolon included
            return client;
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Tenable rejected the API keys (401) - check the access/secret key pair.",
            HttpStatusCode.Forbidden => "Tenable denied access (403) - the key pair lacks the required export/scan permissions.",
            _ => $"Tenable request failed with {(int)status} {status}.",
        };
    }
}
