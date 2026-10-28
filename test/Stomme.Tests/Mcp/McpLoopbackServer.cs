#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// A REAL MCP server on 127.0.0.1, built on the same SDK - so "the external server" in the client-half suites is a
    /// protocol round-trip and not a stub returning a list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It COUNTS requests and records the Authorization headers it saw.</b> That is what makes "registering does not
    /// dial" and "the bearer never left when the destination was refused" assertions about the WIRE rather than about a
    /// return value - which is the distinction the predecessor platform's registration-time dial got wrong.
    /// </para>
    /// <para>
    /// It listens on loopback, which the outbound guard DENIES by default. That is deliberate: the same listener is
    /// both the refused destination and, once the site allowlists <c>127.0.0.1</c>, the one that discovers - so the two
    /// cases differ by the allowlist alone.
    /// </para>
    /// </remarks>
    internal sealed class McpLoopbackServer : IAsyncDisposable
    {
        public const string ToolName = "external_echo";

        private readonly WebApplication _app;
        private readonly StrongBox<int> _connections;

        private McpLoopbackServer(WebApplication app, string address, StrongBox<int> connections, ConcurrentBag<string> authorizationHeaders)
        {
            _app = app;
            Address = address;
            _connections = connections;
            AuthorizationHeaders = authorizationHeaders;
        }

        /// <summary>The absolute http URL of the MCP endpoint, ready to be registered.</summary>
        public string Address { get; }

        public int Connections => Volatile.Read(ref _connections.Value);

        public ConcurrentBag<string> AuthorizationHeaders { get; }

        public static async Task<McpLoopbackServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddMcpServer(options => options.ScopeRequests = true)
                .WithHttpTransport(transport => transport.Stateless = true)
                .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = [new Tool { Name = ToolName, Description = "An external tool.", InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement }],
                }));

            // The counters exist BEFORE the pipeline that writes to them, so there is no instant in which the server is
            // accepting requests and the middleware has nothing to record into.
            var connections = new StrongBox<int>(0);
            var authorization = new ConcurrentBag<string>();

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref connections.Value);
                if (context.Request.Headers.TryGetValue("Authorization", out var header))
                {
                    authorization.Add(header.ToString());
                }

                await next();
            });
            app.MapMcp("/");
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new McpLoopbackServer(app, address.TrimEnd('/') + "/", connections, authorization);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
