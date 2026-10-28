#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Mcp
{
    /// <summary>
    /// One condition a caller must satisfy to see or use a contributed MCP resource or tool (D101, BL-80).
    /// </summary>
    /// <remarks>
    /// The two axes of <c>IAccessGuard.IsAllowedAsync(feature, permission)</c>, named here so that a contributor needs
    /// nothing but <c>Stomme.Shared</c>. A gate that names NEITHER is refused: it would be a condition everyone meets.
    /// </remarks>
    public sealed class McpAccessGate
    {
        public McpAccessGate(string? feature, string? permission)
        {
            if (string.IsNullOrWhiteSpace(feature) && string.IsNullOrWhiteSpace(permission))
            {
                throw new ArgumentException("A gate names a feature, a permission, or both. One that names neither admits everyone.");
            }

            Feature = string.IsNullOrWhiteSpace(feature) ? null : feature;
            Permission = string.IsNullOrWhiteSpace(permission) ? null : permission;
        }

        /// <summary>A feature flag that must be on, or null.</summary>
        public string? Feature { get; }

        /// <summary>A <c>resource:operation</c> permission key the caller must hold, or null.</summary>
        public string? Permission { get; }

        public static McpAccessGate ForPermission(string permission) => new(null, permission);

        public static McpAccessGate ForFeature(string feature) => new(feature, null);
    }

    /// <summary>What happens when a permitted caller invokes a tool.</summary>
    /// <remarks>
    /// <b><see cref="Deny"/> is zero ON PURPOSE.</b> An approval that was never set - a default-initialised field, a
    /// value a newer release added and this one does not know - must land on the side that does nothing. D101 lists the
    /// members as Auto, Confirm, Deny; it does not fix their numbers, and the permissive one is not the one to be 0.
    /// </remarks>
    public enum McpToolApproval
    {
        /// <summary>Never runs. Answered like an unknown tool, and audited as denied.</summary>
        Deny = 0,

        /// <summary>
        /// Needs a human to approve each call. Until an approval pipeline exists this DENIES, with its own audit
        /// outcome - it is a statement about the tool, not a promise that something will ask.
        /// </summary>
        Confirm = 1,

        /// <summary>Runs when the gates pass. Refused at construction for a tool whose name says it changes something.</summary>
        Auto = 2,
    }

    /// <summary>The rules for a tool's NAME.</summary>
    public static class McpToolNames
    {
        public const int MaximumLength = 64;

        /// <summary>Prefixes that say "this tool changes something". Such a tool may never be <see cref="McpToolApproval.Auto"/>.</summary>
        public static readonly IReadOnlyList<string> MutatingPrefixes =
            new[] { "create_", "update_", "delete_", "execute_", "run_", "export_", "send_" };

        /// <summary>
        /// Lower-case ASCII letters, digits, underscore, dash and dot; 1 to 64 characters; starting with a letter.
        /// </summary>
        /// <remarks>
        /// A non-canonical name is REJECTED, never rewritten (the <c>ExportSourceKeys</c> rule) - and here the casing is
        /// load-bearing: <see cref="IsMutating"/> is a prefix test, so <c>Execute_Pipeline</c> would otherwise walk
        /// straight past it.
        /// </remarks>
        public static bool IsCanonical(string? name)
        {
            if (string.IsNullOrEmpty(name) || name!.Length > MaximumLength || name[0] < 'a' || name[0] > 'z')
            {
                return false;
            }

            foreach (var character in name)
            {
                var ok = (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '_' || character == '-' || character == '.';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsMutating(string name)
        {
            foreach (var prefix in MutatingPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A resource a contributor publishes. Its gates are REQUIRED: there is no "visible to all".</summary>
    public sealed class McpResourceDescriptor
    {
        /// <param name="uri">The resource URI, or a template with <c>{name}</c> segments (see <see cref="McpUriTemplate"/>).</param>
        /// <param name="requires">At least one gate. An empty list is refused - see the type remarks.</param>
        /// <remarks>
        /// In the predecessor platform <c>Requires</c> was optional and defaulted to empty, and an empty list did not mean "nobody" - it
        /// meant INVOKABLE BY EVERY AUTHENTICATED PRINCIPAL ON EVERY SITE, and it was the default. So the list has no
        /// default here and an empty one throws: forgetting the gate is a construction error, not an open door.
        /// </remarks>
        public McpResourceDescriptor(string uri, string name, string description, string? mimeType, IReadOnlyList<McpAccessGate> requires)
        {
            if (string.IsNullOrWhiteSpace(uri) || uri.IndexOf("://", StringComparison.Ordinal) <= 0 || uri.Trim() != uri)
            {
                throw new ArgumentException("A resource URI has a scheme, e.g. stomme://site, and no surrounding white space.", nameof(uri));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A resource has a name.", nameof(name));
            }

            Uri = uri;
            Name = name;
            Description = description ?? string.Empty;
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType;
            Requires = McpDescriptorGates.Require(requires, "resource", uri);
        }

        public string Uri { get; }

        public string Name { get; }

        public string Description { get; }

        public string? MimeType { get; }

        /// <summary>Every gate must pass. Never empty.</summary>
        public IReadOnlyList<McpAccessGate> Requires { get; }

        /// <summary>True when the URI is a template (it contains a <c>{</c>).</summary>
        public bool IsTemplate() => Uri.IndexOf('{') >= 0;
    }

    /// <summary>A tool a contributor publishes. Its gates AND its approval are REQUIRED.</summary>
    public sealed class McpToolDescriptor
    {
        /// <remarks>
        /// <b>A tool whose name says it changes something may not be <see cref="McpToolApproval.Auto"/></b>, and that
        /// is refused HERE rather than left to a scanner: the predecessor platform's one mutating tool, <c>execute_pipeline</c>, arrived
        /// as Auto and "deliberately ungated at the mediator level". A scanner over the tree would not see a packaged
        /// module; a constructor sees every descriptor there is.
        /// </remarks>
        public McpToolDescriptor(string name, string description, string parametersJsonSchema, IReadOnlyList<McpAccessGate> requires, McpToolApproval approval)
        {
            if (!McpToolNames.IsCanonical(name))
            {
                throw new ArgumentException(
                    $"A tool name is 1 to {McpToolNames.MaximumLength} lower-case ASCII letters, digits, '_', '-' or '.', starting with a letter. It is refused, not rewritten.",
                    nameof(name));
            }

            if (!Enum.IsDefined(typeof(McpToolApproval), approval))
            {
                throw new ArgumentOutOfRangeException(nameof(approval), "An approval this release does not know is not one it can honour.");
            }

            if (approval == McpToolApproval.Auto && McpToolNames.IsMutating(name))
            {
                throw new ArgumentException(
                    $"'{name}' names a tool that changes something ({string.Join(", ", McpToolNames.MutatingPrefixes)}); such a tool may not be Auto. Use Confirm or Deny.",
                    nameof(approval));
            }

            Name = name;
            Description = description ?? string.Empty;
            ParametersJsonSchema = string.IsNullOrWhiteSpace(parametersJsonSchema) ? "{\"type\":\"object\"}" : parametersJsonSchema;
            Requires = McpDescriptorGates.Require(requires, "tool", name);
            Approval = approval;
        }

        public string Name { get; }

        public string Description { get; }

        /// <summary>The JSON Schema of the arguments. An object schema with no properties when the contributor gave none.</summary>
        public string ParametersJsonSchema { get; }

        /// <summary>Every gate must pass. Never empty.</summary>
        public IReadOnlyList<McpAccessGate> Requires { get; }

        public McpToolApproval Approval { get; }
    }

    internal static class McpDescriptorGates
    {
        public static IReadOnlyList<McpAccessGate> Require(IReadOnlyList<McpAccessGate>? requires, string kind, string subject)
        {
            if (requires is null || requires.Count == 0)
            {
                throw new ArgumentException(
                    $"The {kind} '{subject}' declares no gate. There is no 'visible to all': name the permission or the feature a caller needs.",
                    nameof(requires));
            }

            var copy = new McpAccessGate[requires.Count];
            for (var i = 0; i < requires.Count; i++)
            {
                copy[i] = requires[i] ?? throw new ArgumentException($"The {kind} '{subject}' has a null gate.", nameof(requires));
            }

            // A copy: the contributor keeps no handle with which to empty the list after construction.
            return copy;
        }
    }

    /// <summary>What reading a resource returned.</summary>
    public sealed class McpResourceContent
    {
        public McpResourceContent(string uri, string mimeType, string text)
        {
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "text/plain" : mimeType;
            Text = text ?? string.Empty;
        }

        public string Uri { get; }

        public string MimeType { get; }

        public string Text { get; }
    }

    /// <summary>What invoking a tool returned.</summary>
    public sealed class McpToolResult
    {
        public McpToolResult(string contentJson, bool isError = false)
        {
            ContentJson = contentJson ?? string.Empty;
            IsError = isError;
        }

        public string ContentJson { get; }

        public bool IsError { get; }
    }

    /// <summary>Reads the resources matching one URI or template.</summary>
    public interface IMcpResourceReader
    {
        /// <param name="arguments">The <c>{name}</c> segments of the template, matched from <paramref name="uri"/>; empty for a plain URI.</param>
        /// <returns>Null when there is no such resource.</returns>
        Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default);
    }

    /// <summary>Runs one tool.</summary>
    public interface IMcpToolInvoker
    {
        Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default);
    }

    /// <summary>
    /// Something that publishes MCP resources and tools - descriptors AND the handlers behind them, as ONE unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One unit on purpose.</b> the predecessor platform had four interfaces - two that published descriptors, two that handled them -
    /// unioned by name, first registration wins. So a module registered earlier could publish a same-named descriptor
    /// with NO gates and the real invoker ran behind it; or register a same-named invoker and receive the caller's
    /// arguments. Here the catalog resolves a handler FROM THE CONTRIBUTOR THAT PUBLISHED THE DESCRIPTOR, and a name
    /// published twice is a startup failure, never a silent winner.
    /// </para>
    /// <para>
    /// Discovered like <c>IFeatureContributor</c> (an assembly whose name contains <c>Stomme</c>, a public
    /// parameterless constructor), or registered explicitly when it has dependencies.
    /// </para>
    /// </remarks>
    public interface IMcpContributor
    {
        /// <summary>A stable id for logs and audit, e.g. <c>stomme</c>.</summary>
        string ContributorId { get; }

        IReadOnlyCollection<McpResourceDescriptor> Resources { get; }

        IReadOnlyCollection<McpToolDescriptor> Tools { get; }

        /// <summary>The reader for a descriptor THIS contributor published, by its URI or template. Null is a gate failure.</summary>
        IMcpResourceReader? ReaderFor(string uriTemplate);

        /// <summary>The invoker for a tool THIS contributor published. Null is a gate failure.</summary>
        IMcpToolInvoker? InvokerFor(string toolName);
    }

    /// <summary>What this install publishes, as the CURRENT caller may see and use it.</summary>
    /// <remarks>Unknown and denied give the same answer - null - so the catalog is not an oracle for what exists.</remarks>
    public interface IMcpServerCatalog
    {
        Task<IReadOnlyList<McpResourceDescriptor>> GetResourcesAsync(CancellationToken ct = default);

        Task<IReadOnlyList<McpToolDescriptor>> GetToolsAsync(CancellationToken ct = default);

        Task<McpResourceContent?> ReadResourceAsync(string uri, CancellationToken ct = default);

        Task<McpToolResult?> InvokeToolAsync(string name, string argumentsJson, CancellationToken ct = default);
    }

    /// <summary>A tool discovered on an EXTERNAL MCP server this site has registered.</summary>
    public sealed class McpClientToolDescriptor
    {
        public McpClientToolDescriptor(Guid registrationId, string serverName, string name, string? description, string? parametersJsonSchema)
        {
            RegistrationId = registrationId;
            ServerName = serverName ?? string.Empty;
            Name = name ?? string.Empty;
            Description = description;
            ParametersJsonSchema = parametersJsonSchema;
        }

        public Guid RegistrationId { get; }

        public string ServerName { get; }

        public string Name { get; }

        public string? Description { get; }

        public string? ParametersJsonSchema { get; }
    }

    /// <summary>The external tools available to the current SITE. List-only in Wave 1.</summary>
    public interface IMcpClientToolSource
    {
        Task<IReadOnlyList<McpClientToolDescriptor>> GetAvailableToolsAsync(CancellationToken ct = default);
    }
}
