#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Tests.Fixtures.McpTestModule
{
    /// <summary>
    /// A contributor the MCP suites drive. It has a public parameterless constructor and lives in an assembly
    /// whose name contains <c>Stomme</c>, so it is found by the CONVENTION - nothing registers it.
    /// </summary>
    /// <remarks>
    /// Every descriptor is gated on <see cref="GateTestPermissions.Manage"/>, the key the BL-99 fixture already declares
    /// and the installer seeds for Administrators: the host account is admitted through a ROW, a registered-only user
    /// is not, and no suite here has to invent a permission of its own.
    /// </remarks>
    public sealed class McpTestContributor : IMcpContributor
    {
        public const string Echo = "mcptest_echo";
        public const string Confirm = "mcptest_confirm";
        public const string Never = "mcptest_never";
        public const string Throws = "mcptest_throws";
        public const string Mutating = "update_mcptest";
        public const string Greeting = "mcptest://greeting";
        public const string ItemTemplate = "mcptest://item/{id}";
        public const string SecretInException = "the-handler-said-something-private";

        private static readonly IReadOnlyList<McpAccessGate> Gate = new[] { McpAccessGate.ForPermission(GateTestPermissions.Manage) };

        private static int _invocations;

        /// <summary>How many times ANY of this contributor's handlers actually ran.</summary>
        public static int Invocations => Volatile.Read(ref _invocations);

        public string ContributorId => "mcptest";

        public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } = new[]
        {
            new McpResourceDescriptor(Greeting, "Greeting", "A fixed text.", "text/plain", Gate),
            new McpResourceDescriptor(ItemTemplate, "Item", "One item by id.", "text/plain", Gate),
        };

        public IReadOnlyCollection<McpToolDescriptor> Tools { get; } = new[]
        {
            new McpToolDescriptor(Echo, "Returns its arguments.", "", Gate, McpToolApproval.Auto),
            new McpToolDescriptor(Confirm, "Needs a human.", "", Gate, McpToolApproval.Confirm),
            new McpToolDescriptor(Never, "Never runs.", "", Gate, McpToolApproval.Deny),
            new McpToolDescriptor(Throws, "Throws.", "", Gate, McpToolApproval.Auto),
            new McpToolDescriptor(Mutating, "Says it changes something.", "", Gate, McpToolApproval.Confirm),
        };

        public IMcpResourceReader? ReaderFor(string uriTemplate) =>
            uriTemplate is Greeting or ItemTemplate ? new Reader() : null;

        public IMcpToolInvoker? InvokerFor(string toolName) => toolName switch
        {
            Throws => new Invoker(_ => throw new InvalidOperationException(SecretInException)),
            Echo or Confirm or Never or Mutating => new Invoker(arguments => new McpToolResult("{\"echo\":" + (string.IsNullOrWhiteSpace(arguments) ? "null" : arguments) + "}")),
            _ => null,
        };

        private sealed class Reader : IMcpResourceReader
        {
            public Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _invocations);
                var text = arguments.TryGetValue("id", out var id) ? "item " + id : "hello";
                return Task.FromResult<McpResourceContent?>(new McpResourceContent(uri, "text/plain", text));
            }
        }

        private sealed class Invoker(Func<string, McpToolResult> body) : IMcpToolInvoker
        {
            public Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _invocations);
                return Task.FromResult(body(argumentsJson));
            }
        }
    }

    /// <summary>
    /// <c>api/McpTest</c> - the catalog over plain HTTP, so it is driven with a REAL principal, a REAL resolved alias and
    /// the REAL access guard before the MCP endpoint itself exists. It adds no gate of its own: every refusal asserted
    /// through it is the catalog's.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class McpTestController : Controller
    {
        public const string Unknown = "mcp-unknown";

        private IMcpServerCatalog? Catalog => HttpContext.RequestServices.GetService<IMcpServerCatalog>();

        [HttpGet("tools")]
        public async Task<IActionResult> Tools() =>
            Catalog is { } catalog ? Ok(string.Join(",", (await catalog.GetToolsAsync()).Select(tool => tool.Name))) : NotFound("mcp-disabled");

        [HttpGet("resources")]
        public async Task<IActionResult> Resources() =>
            Catalog is { } catalog ? Ok(string.Join(",", (await catalog.GetResourcesAsync()).Select(resource => resource.Uri))) : NotFound("mcp-disabled");

        [HttpGet("read")]
        public async Task<IActionResult> Read(string uri)
        {
            if (Catalog is not { } catalog)
            {
                return NotFound("mcp-disabled");
            }

            var content = await catalog.ReadResourceAsync(uri);
            return content is null ? Ok(Unknown) : Ok(content.Text);
        }

        [HttpGet("call")]
        public async Task<IActionResult> Call(string name, string? arguments)
        {
            if (Catalog is not { } catalog)
            {
                return NotFound("mcp-disabled");
            }

            var result = await catalog.InvokeToolAsync(name, arguments ?? string.Empty);
            return result is null ? Ok(Unknown) : Ok((result.IsError ? "error:" : "ok:") + result.ContentJson);
        }
    }
}
