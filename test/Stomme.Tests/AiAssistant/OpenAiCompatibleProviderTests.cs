#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.AiAssistant.Application;
using Microsoft.Extensions.AI;
using Xunit;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// BL-97 increment 9 - the shipped provider (D113) DRIVEN over a real socket: the OpenAI-compatible client, handed the
    /// site's egress, sends <c>POST {endpoint}/chat/completions</c> with the model, the messages and the key as a Bearer, and
    /// reads the reply and its token usage back - against a minimal HTTP server on the loopback address, which the egress
    /// admits only because the site allowlists it (as a local Ollama or LM Studio would be). Without the allowlist the host's
    /// one outbound guard refuses before any connection is made. No live model is called.
    /// </summary>
    public sealed class OpenAiCompatibleProviderTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        private const string Completion = """
            {"id":"chatcmpl-1","object":"chat.completion","created":1767225600,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"hello from the fake model"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}
            """;

        [Fact]
        public async Task The_provider_speaks_the_OpenAI_compatible_API_through_the_sites_egress()
        {
            using var server = new OneShotHttpServer(Completion);
            var serving = server.ServeOnceAsync();
            using var transport = new HostConnectorEgress(new[] { "127.0.0.1" }).CreateHttpClient(TimeSpan.FromSeconds(30));
            using var client = new OpenAiCompatibleLlmProvider().CreateChatClient($"http://127.0.0.1:{server.Port}/v1", "gpt-test", "sk-drive-key", transport);

            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.System, "be brief"), new ChatMessage(ChatRole.User, "say hello") });
            }
            finally
            {
                output.WriteLine("[server] " + string.Join(" | ", server.Headers));
            }

            var request = await serving;

            Assert.Equal("hello from the fake model", response.Text);
            Assert.Equal((11L, 7L), (response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount));
            Assert.Equal("POST", request.Method);
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Equal("Bearer sk-drive-key", request.Authorization);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("gpt-test", body.RootElement.GetProperty("model").GetString());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal(("system", "be brief"), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString()));
            Assert.Equal(("user", "say hello"), (messages[1].GetProperty("role").GetString(), messages[1].GetProperty("content").GetString()));
        }

        [Fact]
        public async Task A_loopback_endpoint_the_site_did_not_allowlist_is_refused_before_any_connection()
        {
            using var server = new OneShotHttpServer(Completion);
            using var transport = new HostConnectorEgress(Array.Empty<string>()).CreateHttpClient(TimeSpan.FromSeconds(30));
            using var client = new OpenAiCompatibleLlmProvider().CreateChatClient($"http://127.0.0.1:{server.Port}/v1", "gpt-test", null, transport);

            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "say hello") }));

            Assert.True(HostConnectorEgress.IsDenial(thrown) || HostConnectorEgress.IsDenial(thrown.InnerException), "the refusal is the outbound guard's: " + thrown.GetType().Name);
            Assert.False(server.Pending, "the guard refuses before a connection reaches the server");
        }

        /// <summary>Accepts one HTTP/1.1 request on 127.0.0.1, records it, answers with a fixed JSON body.</summary>
        private sealed class OneShotHttpServer : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly string _response;

            public OneShotHttpServer(string response)
            {
                _response = response;
                _listener.Start();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public System.Collections.Generic.List<string> Headers { get; } = new();

            public bool Pending => _listener.Pending();

            public async Task<(string Method, string Path, string? Authorization, string Body)> ServeOnceAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var socket = await _listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();

                // Raw bytes: read until the blank line, then exactly Content-Length more - no reader buffering in between.
                var received = new MemoryStream();
                var chunk = new byte[4096];
                int headerEnd;
                while ((headerEnd = IndexOfBlankLine(received.GetBuffer(), (int)received.Length)) < 0)
                {
                    var count = await stream.ReadAsync(chunk, timeout.Token);
                    if (count == 0)
                    {
                        throw new IOException("The client closed the connection before the headers ended.");
                    }

                    received.Write(chunk, 0, count);
                }

                var lines = Encoding.ASCII.GetString(received.GetBuffer(), 0, headerEnd).Split("\r\n");
                Headers.AddRange(lines[1..]);
                var requestLine = lines[0].Split(' ');
                string? authorization = null;
                var length = 0;
                foreach (var header in lines[1..])
                {
                    var colon = header.IndexOf(':');
                    var name = header[..colon].Trim();
                    var value = header[(colon + 1)..].Trim();
                    if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        authorization = value;
                    }
                    else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                var bodyStart = headerEnd + 4;
                while (received.Length - bodyStart < length)
                {
                    var count = await stream.ReadAsync(chunk, timeout.Token);
                    if (count == 0)
                    {
                        throw new IOException("The client closed the connection before the body ended.");
                    }

                    received.Write(chunk, 0, count);
                }

                var body = Encoding.UTF8.GetString(received.GetBuffer(), bodyStart, length);
                var payload = Encoding.UTF8.GetBytes(_response);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, timeout.Token);
                await stream.WriteAsync(payload, timeout.Token);
                await stream.FlushAsync(timeout.Token);
                return (requestLine[0], requestLine[1], authorization, body);
            }

            private static int IndexOfBlankLine(byte[] buffer, int length)
            {
                for (var i = 0; i + 3 < length; i++)
                {
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    {
                        return i;
                    }
                }

                return -1;
            }
            public void Dispose() => _listener.Stop();
        }
    }
}
