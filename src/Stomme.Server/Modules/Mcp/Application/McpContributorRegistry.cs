#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Mcp;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>A published resource together with the ONE contributor that published it.</summary>
    public sealed record McpPublishedResource(IMcpContributor Contributor, McpResourceDescriptor Descriptor);

    /// <summary>A published tool together with the ONE contributor that published it.</summary>
    public sealed record McpPublishedTool(IMcpContributor Contributor, McpToolDescriptor Descriptor);

    /// <summary>
    /// One contribution that was REJECTED, and why - what the boot log records at Error and the admin page lists.
    /// </summary>
    /// <param name="Kind">"tool", "resource" or "contributor".</param>
    /// <param name="Name">The tool name, resource URI or contributor id the conflict is about.</param>
    /// <param name="Contributors">Every contributor type involved. For a collision, ALL of them - none of them won.</param>
    /// <param name="Reason">Operator-facing, and it says what was dropped rather than what was kept.</param>
    public sealed record McpContributionConflict(string Kind, string Name, IReadOnlyList<string> Contributors, string Reason);

    /// <summary>
    /// Everything this install's contributors publish, each descriptor bound to the contributor it came from (D101
    /// items 1 and 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A name published twice is REJECTED - on every side - and the host starts.</b> D101 originally made it a loud
    /// startup failure. BL-105 measured why that could not stand: contributor discovery matches ANY assembly in
    /// <c>/bin</c> whose name contains <c>Stomme</c>, including a package <c>InstallationManager</c> unpacked there,
    /// so a third-party package publishing a framework name could stop the host - and after a restart there is no
    /// admin UI left to remove it. <b>Owner decision, 2026-09-22:</b> adopt D103/D104's posture (D101 correction 3).
    /// </para>
    /// <para>
    /// <b>The security property is unchanged, and that is the whole reason this is safe.</b> the predecessor platform unioned by name,
    /// first registration wins - so a module registered earlier could publish a same-named descriptor with NO gates
    /// and have the real invoker run behind it, or register a same-named invoker and be handed the caller's
    /// arguments. Rejecting EVERY side of a collision means no same-named descriptor can downgrade a gate or hijack a
    /// handler either: the name simply stops existing. There is deliberately no "the framework contributor wins" -
    /// a winner is a rule an attacker can aim at, and the framework is not privileged here.
    /// </para>
    /// <para>
    /// <b>Nothing a contributor does throws from this constructor.</b> A blank id, a null descriptor, a descriptor
    /// with no handler - each is that contributor's bug, and each could arrive in a package, so each is a rejection
    /// and not a dead install. IN-TREE duplicates are caught earlier and harder, by a reflective BUILD gate
    /// (<c>McpContributorOwnershipTests</c>), because in this repository a collision is our mistake and should fail
    /// before it ships.
    /// </para>
    /// <para>
    /// <b>This type does not log.</b> It is SCOPED - a contributor registered explicitly may need scoped services, and
    /// the container refuses a scoped dependency inside a singleton - so logging here would write the same Error line
    /// on every request. The module startup resolves it once while the pipeline is built and logs
    /// <see cref="Conflicts"/> there; the admin page reads the same list per request.
    /// </para>
    /// </remarks>
    public sealed class McpContributorRegistry
    {
        private readonly Dictionary<string, McpPublishedTool> _tools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, McpPublishedResource> _resources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IMcpToolInvoker> _invokers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IMcpResourceReader> _readers = new(StringComparer.Ordinal);
        private readonly List<McpContributionConflict> _conflicts = new();

        public McpContributorRegistry(IEnumerable<IMcpContributor> contributors)
        {
            ArgumentNullException.ThrowIfNull(contributors);

            var accepted = new Dictionary<string, IMcpContributor>(StringComparer.OrdinalIgnoreCase);
            var usable = new List<IMcpContributor>();

            foreach (var contributor in contributors)
            {
                var type = contributor.GetType().FullName ?? contributor.GetType().Name;

                if (string.IsNullOrWhiteSpace(contributor.ContributorId))
                {
                    Reject("contributor", type, new[] { type }, "It declares no ContributorId, so nothing it publishes can be attributed. It was dropped.");
                    continue;
                }

                if (accepted.TryGetValue(contributor.ContributorId, out var sameId))
                {
                    // BOTH are dropped: keeping the first would be first-wins, which is the rule this replaces.
                    var first = sameId.GetType().FullName ?? sameId.GetType().Name;
                    Reject("contributor", contributor.ContributorId, new[] { first, type },
                        "Two contributors claim this id. Both were dropped; an id names ONE contributor.");
                    accepted.Remove(contributor.ContributorId);
                    usable.RemoveAll(item => ReferenceEquals(item, sameId));
                    continue;
                }

                accepted.Add(contributor.ContributorId, contributor);
                usable.Add(contributor);
            }

            // ---- names, across the contributors that survived the pass above ------------------------------------
            CollectTools(usable);
            CollectResources(usable);
            RejectShadowedAddresses();
        }

        public IReadOnlyCollection<McpPublishedTool> Tools => _tools.Values;

        public IReadOnlyCollection<McpPublishedResource> Resources => _resources.Values;

        /// <summary>What was dropped, and why. Empty on a healthy install.</summary>
        public IReadOnlyList<McpContributionConflict> Conflicts => _conflicts;

        /// <summary>Exact, ordinal: a name that is not the published name matches nothing.</summary>
        public bool TryGetTool(string? name, out McpPublishedTool tool, out IMcpToolInvoker invoker)
        {
            if (name is not null && _tools.TryGetValue(name, out tool!))
            {
                invoker = _invokers[name];
                return true;
            }

            tool = null!;
            invoker = null!;
            return false;
        }

        /// <summary>
        /// The descriptor that answers <paramref name="uri"/>: the plain URI equal to it, else the one template that
        /// matches it, with the template's arguments.
        /// </summary>
        public bool TryGetResource(string? uri, out McpPublishedResource resource, out IMcpResourceReader reader, out IReadOnlyDictionary<string, string> arguments)
        {
            arguments = EmptyArguments;
            if (uri is not null)
            {
                if (_resources.TryGetValue(uri, out resource!) && !resource.Descriptor.IsTemplate())
                {
                    reader = _readers[uri];
                    return true;
                }

                McpPublishedResource? matched = null;
                foreach (var candidate in _resources.Values)
                {
                    if (!candidate.Descriptor.IsTemplate() || McpUriTemplate.Match(candidate.Descriptor.Uri, uri) is not { } found)
                    {
                        continue;
                    }

                    if (matched is not null)
                    {
                        // Two templates claim this address. Neither answers: picking one would be first-wins again.
                        matched = null;
                        break;
                    }

                    matched = candidate;
                    arguments = found;
                }

                if (matched is not null)
                {
                    resource = matched;
                    reader = _readers[matched.Descriptor.Uri];
                    return true;
                }
            }

            resource = null!;
            reader = null!;
            arguments = EmptyArguments;
            return false;
        }

        private void CollectTools(IReadOnlyList<IMcpContributor> contributors)
        {
            foreach (var contributor in contributors)
            {
                var type = contributor.GetType().FullName ?? contributor.GetType().Name;
                foreach (var tool in contributor.Tools ?? Array.Empty<McpToolDescriptor>())
                {
                    if (tool is null)
                    {
                        Reject("tool", "(null)", new[] { type }, "The contributor published a null tool descriptor. It was dropped.");
                        continue;
                    }

                    if (_tools.TryGetValue(tool.Name, out var existing))
                    {
                        // BOTH sides go. A same-named tool must not be able to downgrade a gate or take the arguments.
                        var first = existing.Contributor.GetType().FullName ?? existing.Contributor.GetType().Name;
                        Reject("tool", tool.Name, new[] { first, type },
                            "Two contributors publish this tool. BOTH were dropped, so the name is not served at all.");
                        _tools.Remove(tool.Name);
                        _invokers.Remove(tool.Name);
                        continue;
                    }

                    // Already rejected once in this pass: a third contributor of the same name must not resurrect it.
                    if (_conflicts.Any(conflict => conflict.Kind == "tool" && conflict.Name == tool.Name))
                    {
                        Reject("tool", tool.Name, new[] { type }, "This tool name was already rejected as a collision. It stays dropped.");
                        continue;
                    }

                    if (contributor.InvokerFor(tool.Name) is not { } invoker)
                    {
                        Reject("tool", tool.Name, new[] { type }, "The contributor publishes this tool and has no invoker for it. It was dropped.");
                        continue;
                    }

                    _tools.Add(tool.Name, new McpPublishedTool(contributor, tool));
                    _invokers.Add(tool.Name, invoker);
                }
            }
        }

        private void CollectResources(IReadOnlyList<IMcpContributor> contributors)
        {
            foreach (var contributor in contributors)
            {
                var type = contributor.GetType().FullName ?? contributor.GetType().Name;
                foreach (var resource in contributor.Resources ?? Array.Empty<McpResourceDescriptor>())
                {
                    if (resource is null)
                    {
                        Reject("resource", "(null)", new[] { type }, "The contributor published a null resource descriptor. It was dropped.");
                        continue;
                    }

                    if (_resources.TryGetValue(resource.Uri, out var existing))
                    {
                        var first = existing.Contributor.GetType().FullName ?? existing.Contributor.GetType().Name;
                        Reject("resource", resource.Uri, new[] { first, type },
                            "Two contributors publish this resource. BOTH were dropped, so the URI is not served at all.");
                        _resources.Remove(resource.Uri);
                        _readers.Remove(resource.Uri);
                        continue;
                    }

                    if (_conflicts.Any(conflict => conflict.Kind == "resource" && conflict.Name == resource.Uri))
                    {
                        Reject("resource", resource.Uri, new[] { type }, "This resource URI was already rejected as a collision. It stays dropped.");
                        continue;
                    }

                    if (contributor.ReaderFor(resource.Uri) is not { } reader)
                    {
                        Reject("resource", resource.Uri, new[] { type }, "The contributor publishes this resource and has no reader for it. It was dropped.");
                        continue;
                    }

                    _resources.Add(resource.Uri, new McpPublishedResource(contributor, resource));
                    _readers.Add(resource.Uri, reader);
                }
            }
        }

        /// <summary>
        /// Two TEMPLATES, or a template and a plain URI, can claim the same concrete address without being the same
        /// string. Which one answers must not depend on registration order either, so BOTH are dropped.
        /// </summary>
        private void RejectShadowedAddresses()
        {
            var templates = _resources.Values.Where(item => item.Descriptor.IsTemplate()).ToList();
            foreach (var plain in _resources.Values.Where(item => !item.Descriptor.IsTemplate()).ToList())
            {
                var shadow = templates.FirstOrDefault(template => McpUriTemplate.Match(template.Descriptor.Uri, plain.Descriptor.Uri) is not null);
                if (shadow is null)
                {
                    continue;
                }

                Reject("resource", plain.Descriptor.Uri,
                    new[]
                    {
                        plain.Contributor.GetType().FullName ?? plain.Contributor.GetType().Name,
                        shadow.Contributor.GetType().FullName ?? shadow.Contributor.GetType().Name,
                    },
                    $"This address is also matched by the template '{shadow.Descriptor.Uri}'. BOTH were dropped; an address belongs to ONE descriptor.");

                _resources.Remove(plain.Descriptor.Uri);
                _readers.Remove(plain.Descriptor.Uri);
                _resources.Remove(shadow.Descriptor.Uri);
                _readers.Remove(shadow.Descriptor.Uri);
                templates.Remove(shadow);
            }
        }

        private void Reject(string kind, string name, IReadOnlyList<string> contributors, string reason) =>
            _conflicts.Add(new McpContributionConflict(kind, name, contributors, reason));

        private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>();
    }
}
