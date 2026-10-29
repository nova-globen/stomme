#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Modules.Mcp.Application;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Stomme.Modules.Mcp.Server
{
    /// <summary>
    /// The five MCP verbs, bridged to <see cref="IMcpServerCatalog"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every handler resolves the catalog from <c>RequestContext.Services</c></b> - the SDK's own per-request scope.
    /// Stomme's tenant resolution puts the resolved alias in a SCOPED <c>SiteState</c>, and the catalog is scoped
    /// too, so a handler holding a captured ROOT provider could not serve itself a catalog at all: MS.DI refuses a
    /// scoped service from the root, which <c>McpEndpointDriveTests</c> asserts rather than assumes.
    /// </para>
    /// <para>
    /// <b>MEASURED, and narrower than it first read:</b> swapping this for <c>context.Server.Services</c> changes
    /// nothing today - the mutant survived - because the transport is STATELESS, so the SDK builds a server per
    /// request out of that request's scope and the two providers are the same object. The line is written this way
    /// because it stays correct if statelessness is ever turned off; it is not, today, what stops a cross-request
    /// leak. What would stop one is the per-site drive (<c>McpSiteIsolationTests</c>) - two sites in one
    /// tenant, asserting the endpoint answers for the site the REQUEST resolved - and that is the next increment.
    /// </para>
    /// <para>
    /// <b>Nothing here decides anything.</b> Gates, approval, dispatch and the audit ledger are the catalog's; this
    /// file is shape conversion, and it keeps the catalog's rule that unknown and denied look identical: both come back
    /// as an empty list or an error result carrying no detail.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Endpoint metadata marking the MCP route, so the Identity cookie's challenge can answer it 401 instead of this
    /// host''s usual bare 403 (increment 4 follow-up).
    /// </summary>
    /// <remarks>
    /// <b>Metadata, not a path match.</b> The route is configurable (<c>Mcp:Server:Path</c>), so a string comparison in
    /// the cookie options would be a second place that has to agree with it - and would quietly stop agreeing the day
    /// someone set the key. The marker travels WITH the endpoint.
    /// </remarks>
    internal sealed class McpEndpointMarker
    {
    }

    internal static class McpServerBridge
    {
        /// <summary>An unknown or refused tool. The SDK has no "no such tool" answer that is not an error.</summary>
        private const string RefusedMessage = "No such tool, or it is not available to you.";

        private const string RefusedResourceMessage = "No such resource, or it is not available to you.";

        private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;

        public static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken ct) =>
            WithCatalogAsync(context, new ListToolsResult(), async catalog => new ListToolsResult
            {
                Tools = (await catalog.GetToolsAsync(ct)).Select(ToTool).ToList(),
            });

        public static ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct) =>
            WithCatalogAsync(context, Refused(RefusedMessage), async catalog =>
            {
                var name = context.Params?.Name;
                if (string.IsNullOrEmpty(name))
                {
                    return Refused(RefusedMessage);
                }

                var arguments = context.Params?.Arguments is { } supplied
                    ? JsonSerializer.Serialize(supplied)
                    : string.Empty;

                var result = await catalog.InvokeToolAsync(name!, arguments, ct);
                if (result is null)
                {
                    // Unknown and denied are ONE answer. The ledger tells them apart; the caller cannot.
                    return Refused(RefusedMessage);
                }

                return new CallToolResult
                {
                    Content = { new TextContentBlock { Text = result.ContentJson } },
                    IsError = result.IsError,
                };
            });

        public static ValueTask<ListResourcesResult> ListResourcesAsync(RequestContext<ListResourcesRequestParams> context, CancellationToken ct) =>
            WithCatalogAsync(context, new ListResourcesResult(), async catalog => new ListResourcesResult
            {
                // A template is NOT a resource: it is listed by resources/templates/list, and a client that read it
                // here would try to fetch "mcptest://item/{id}" literally.
                Resources = (await catalog.GetResourcesAsync(ct)).Where(descriptor => !descriptor.IsTemplate()).Select(ToResource).ToList(),
            });

        public static ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(RequestContext<ListResourceTemplatesRequestParams> context, CancellationToken ct) =>
            WithCatalogAsync(context, new ListResourceTemplatesResult(), async catalog => new ListResourceTemplatesResult
            {
                ResourceTemplates = (await catalog.GetResourcesAsync(ct)).Where(descriptor => descriptor.IsTemplate()).Select(ToResourceTemplate).ToList(),
            });

        public static ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> context, CancellationToken ct) =>
            WithCatalogAsync(context, new ReadResourceResult(), async catalog =>
            {
                var uri = context.Params?.Uri;
                if (string.IsNullOrEmpty(uri))
                {
                    throw new McpException(RefusedResourceMessage);
                }

                var content = await catalog.ReadResourceAsync(uri!, ct)
                    ?? throw new McpException(RefusedResourceMessage);

                return new ReadResourceResult
                {
                    Contents = { new TextResourceContents { Uri = content.Uri, MimeType = content.MimeType, Text = content.Text } },
                };
            });

        /// <summary>
        /// Resolves the catalog from THIS request's scope and runs <paramref name="body"/>. With no catalog in the
        /// scope - the server half switched off between composing and calling - the verb answers
        /// <paramref name="whenAbsent"/> rather than throwing a 500 that would say the endpoint exists.
        /// </summary>
        private static async ValueTask<TResult> WithCatalogAsync<TParams, TResult>(
            RequestContext<TParams> context, TResult whenAbsent, Func<IMcpServerCatalog, Task<TResult>> body)
        {
            var catalog = context.Services?.GetService<IMcpServerCatalog>();
            return catalog is null ? whenAbsent : await body(catalog);
        }

        private static CallToolResult Refused(string message) =>
            new() { Content = { new TextContentBlock { Text = message } }, IsError = true };

        private static Tool ToTool(McpToolDescriptor descriptor) => new()
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            InputSchema = ParseSchema(descriptor.ParametersJsonSchema),
        };

        private static Resource ToResource(McpResourceDescriptor descriptor) => new()
        {
            Uri = descriptor.Uri,
            Name = descriptor.Name,
            Description = descriptor.Description,
            MimeType = descriptor.MimeType,
        };

        private static ResourceTemplate ToResourceTemplate(McpResourceDescriptor descriptor) => new()
        {
            UriTemplate = descriptor.Uri,
            Name = descriptor.Name,
            Description = descriptor.Description,
            MimeType = descriptor.MimeType,
        };

        /// <summary>
        /// A contributor's schema text as JSON, or the empty object schema. A contributor cannot break the LIST for
        /// everyone with one malformed schema - the descriptor's constructor already defaults an empty one, and this
        /// is the second half of the same rule for text that is present but not JSON.
        /// </summary>
        private static JsonElement ParseSchema(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return EmptyObjectSchema;
            }

            try
            {
                return JsonDocument.Parse(schema).RootElement.Clone();
            }
            catch (JsonException)
            {
                return EmptyObjectSchema;
            }
        }
    }
}
