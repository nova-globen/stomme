#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Mcp.Domain;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Stomme.Modules.Mcp.Infrastructure
{
    /// <summary>How a refresh ended. A refusal is told apart from a failure, because they mean different things to an operator.</summary>
    public sealed class McpDiscoveryResult
    {
        private McpDiscoveryResult(DiscoveredToolSet? tools, string? failure, bool refused)
        {
            Tools = tools;
            Failure = failure;
            Refused = refused;
        }

        /// <summary>What the server offers, when the refresh succeeded; null otherwise.</summary>
        public DiscoveredToolSet? Tools { get; }

        /// <summary>Why the refresh did not succeed - operator-facing, and never an exception's text.</summary>
        public string? Failure { get; }

        /// <summary>True when the GUARD refused the destination, as opposed to the server failing to answer.</summary>
        public bool Refused { get; }

        public bool Succeeded => Tools is not null;

        public static McpDiscoveryResult Discovered(DiscoveredToolSet tools) => new(tools, null, false);

        public static McpDiscoveryResult Denied(string reason) => new(null, reason, true);

        public static McpDiscoveryResult Failed(string reason) => new(null, reason, false);
    }

    /// <summary>
    /// Dials an external MCP server and asks it what it offers. The ONLY thing in this module that makes
    /// an outbound connection.
    /// </summary>
    /// <remarks>
    /// <b>Registering does not come here.</b> A new registration is <c>Unverified</c> with no tools; discovery is the
    /// explicit <c>mcp:manage</c> refresh. Dialling on registration and rendering the response would make the
    /// registration form an SSRF oracle for anyone who could reach it - the response told the caller what was listening.
    /// </remarks>
    public interface IMcpClientConnector
    {
        Task<McpDiscoveryResult> DiscoverToolsAsync(McpServerRegistrationAggregate registration, string? apiKey, CancellationToken ct = default);
    }

    public sealed class McpClientConnector : IMcpClientConnector
    {
        /// <summary>A refresh that has not finished by now is a refresh that is not going to.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly IMcpEndpointGuard _guard;
        private readonly ILogger<McpClientConnector> _logger;

        public McpClientConnector(IMcpEndpointGuard guard, ILogger<McpClientConnector> logger)
        {
            _guard = guard;
            _logger = logger;
        }

        public async Task<McpDiscoveryResult> DiscoverToolsAsync(McpServerRegistrationAggregate registration, string? apiKey, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(registration);

            // The transport VO already refuses anything that is not an absolute http(s) URL, so this cannot normally
            // fail - it is the second half of the same rule, for a row written before that refusal existed.
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(registration.Transport.Value, out var endpoint) || !OutboundConnectGuard.IsPermittedScheme(endpoint))
            {
                return McpDiscoveryResult.Denied("The registered address is not an absolute http or https URL.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            HttpClient? http = null;
            try
            {
                http = await _guard.CreateGuardedClientAsync(registration.SiteId, ct);

                var options = new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    Name = registration.Name,
                    ConnectionTimeout = Timeout,
                };

                // The bearer goes ONLY through the guarded client: it is set on the transport that owns that client,
                // so there is no path by which the key travels on a connection the guard has not approved.
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey.Trim() };
                }

                // ownsClient: false - the client is disposed here, in the finally, whatever the transport does.
                var transport = new HttpClientTransport(options, http!, loggerFactory: null, ownsHttpClient: false);
                await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                return McpDiscoveryResult.Discovered(Bounded(tools, registration));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return McpDiscoveryResult.Failed($"The server did not answer within {Timeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (_guard.IsDenial(ex))
            {
                // The refusal's own message names the host and says why; it is the operator's to read.
                _logger.LogWarning("MCP refresh of '{Server}' on site {SiteId} was refused by the outbound guard.", registration.Name, registration.SiteId);
                return McpDiscoveryResult.Denied(Denial(ex) ?? "The destination is not permitted.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception's text is the remote server's and may carry more than an operator should be shown here.
                _logger.LogWarning(ex, "MCP refresh of '{Server}' on site {SiteId} failed.", registration.Name, registration.SiteId);
                return McpDiscoveryResult.Failed("The server could not be reached, or did not answer as an MCP server.");
            }
            finally
            {
                http?.Dispose();
            }
        }

        /// <summary>
        /// What the server said, put through <see cref="DiscoveredToolSet"/>'s bounds. A set it REFUSES outright - a
        /// repeated name - is a failed refresh, not an exception out of the module: the row keeps what it had.
        /// </summary>
        private DiscoveredToolSet Bounded(IEnumerable<McpClientTool> tools, McpServerRegistrationAggregate registration)
        {
            var kept = new List<DiscoveredTool>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools)
            {
                var name = (tool.Name ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > DiscoveredToolSet.MaximumNameLength || !names.Add(name))
                {
                    // Bounded, not refused: one unusable entry from somebody else's server must not cost this site the
                    // other 200. The VO would throw on the repeat, and a throw here would lose the whole refresh.
                    _logger.LogWarning("MCP server '{Server}' on site {SiteId} offered a tool name that was empty, over-long or repeated; it was dropped.",
                        registration.Name, registration.SiteId);
                    continue;
                }

                kept.Add(new DiscoveredTool(name, tool.Description, tool.JsonSchema.ToString()));
                if (kept.Count == DiscoveredToolSet.MaximumTools)
                {
                    _logger.LogWarning("MCP server '{Server}' on site {SiteId} offered more than {Maximum} tools; the rest were dropped.",
                        registration.Name, registration.SiteId, DiscoveredToolSet.MaximumTools);
                    break;
                }
            }

            return DiscoveredToolSet.Of(kept);
        }

        private static string? Denial(Exception? exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is OutboundDestinationDeniedException denied)
                {
                    return denied.Message;
                }
            }

            return null;
        }
    }
}
