#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp;
using Stomme.Modules.Mcp.Application;
using Stomme.Tests.Fixtures.McpTestModule;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// A name belongs to ONE contributor (D101 items 1-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this answers, measured on the predecessor platform: descriptors and handlers were unioned by NAME, first registration wins -
    /// so a module registered earlier could publish a same-named descriptor with no gates and have the real invoker run
    /// behind it, or register a same-named invoker and be handed the caller's arguments.
    /// </para>
    /// <para>
    /// <b>Here there is nothing to win: EVERY side of a collision is dropped, and the host starts</b> (owner decision
    /// 2026-09-22, BL-105, D101 correction 3 - D101 originally made it a fatal startup check, which let a third-party
    /// package in <c>/bin</c> brick an install with no admin UI left to remove it). The security property is
    /// unchanged and is what these cases assert: a same-named descriptor can neither downgrade a gate nor hijack a
    /// handler, because the name stops being served at all.
    /// </para>
    /// <para>
    /// IN-TREE duplicates are a different thing - our own mistake - and fail the BUILD, in
    /// <see cref="No_two_IN_TREE_contributors_publish_the_same_name"/>.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpContributorOwnershipTests
    {
        private static readonly IReadOnlyList<McpAccessGate> Gate = new[] { McpAccessGate.ForPermission("mcptest:use") };

        private readonly ITestOutputHelper _output;

        public McpContributorOwnershipTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void A_tool_name_published_by_two_contributors_is_dropped_ON_BOTH_SIDES()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("first", tools: new[] { Tool("shared_tool") }),
                new Planted("second", tools: new[] { Tool("shared_tool") }),
            });

            // Neither serves it. Keeping the first would be first-wins, which is the rule this replaces; keeping the
            // second would be last-wins, which is the same mistake facing the other way.
            Assert.False(registry.TryGetTool("shared_tool", out _, out _));
            Assert.DoesNotContain(registry.Tools, tool => tool.Descriptor.Name == "shared_tool");

            var conflict = Assert.Single(registry.Conflicts, item => item.Kind == "tool" && item.Name == "shared_tool");
            _output.WriteLine($"{conflict.Kind} '{conflict.Name}' from {string.Join(" and ", conflict.Contributors)}: {conflict.Reason}");
            Assert.Equal(2, conflict.Contributors.Count);       // it names BOTH, so an operator can find them
        }

        [Fact]
        public void A_THIRD_contributor_cannot_resurrect_a_name_that_was_already_rejected()
        {
            // Without this, the second collision would "restore" the name for whoever came last.
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("first", tools: new[] { Tool("shared_tool") }),
                new Planted("second", tools: new[] { Tool("shared_tool") }),
                new Planted("third", tools: new[] { Tool("shared_tool") }),
            });

            Assert.False(registry.TryGetTool("shared_tool", out _, out _));
            Assert.Empty(registry.Tools);
        }

        [Fact]
        public void A_resource_uri_published_by_two_contributors_is_dropped_ON_BOTH_SIDES()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("first", resources: new[] { Resource("planted://thing") }),
                new Planted("second", resources: new[] { Resource("planted://thing") }),
            });

            Assert.False(registry.TryGetResource("planted://thing", out _, out _, out _));
            Assert.Single(registry.Conflicts, item => item.Kind == "resource" && item.Name == "planted://thing");
        }

        [Fact]
        public void A_plain_uri_that_another_contributors_template_also_matches_is_dropped_WITH_the_template()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("first", resources: new[] { Resource("planted://item/7") }),
                new Planted("second", resources: new[] { Resource("planted://item/{id}") }),
            });

            // An address claimed by two descriptors is served by NEITHER - the template goes too, or the plain URI's
            // address would still be answered by it, which is the shadowing this exists to stop.
            Assert.False(registry.TryGetResource("planted://item/7", out _, out _, out _));
            Assert.Empty(registry.Resources);
            Assert.Contains(registry.Conflicts, item => item.Name == "planted://item/7");
        }

        [Fact]
        public void Two_contributors_with_one_id_are_BOTH_dropped_with_everything_they_publish()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("same", tools: new[] { Tool("one_tool") }),
                new Planted("same", tools: new[] { Tool("another_tool") }),
            });

            // An id is how a rejection is attributed and how an operator finds the package. Two claimants means
            // neither can be named, so nothing either of them publishes is served.
            Assert.Empty(registry.Tools);
            Assert.Single(registry.Conflicts, item => item.Kind == "contributor" && item.Name == "same");
        }

        [Fact]
        public void A_contributor_with_NO_id_is_dropped_rather_than_taking_the_host_with_it()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("  ", tools: new[] { Tool("orphan_tool") }),
                new Planted("good", tools: new[] { Tool("good_tool") }),
            });

            Assert.False(registry.TryGetTool("orphan_tool", out _, out _));
            Assert.True(registry.TryGetTool("good_tool", out _, out _));    // the healthy one is unaffected
            Assert.Single(registry.Conflicts, item => item.Kind == "contributor");
        }

        [Fact]
        public void A_descriptor_whose_contributor_has_no_handler_for_it_is_dropped_and_the_rest_survive()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("handlerless", tools: new[] { Tool("no_invoker") }, handles: _ => false),
                new Planted("good", tools: new[] { Tool("good_tool") }),
            });

            // A descriptor with no handler would dispatch to nothing; it is that contributor's bug, and it could
            // arrive in a package, so it costs that descriptor and nothing else.
            Assert.False(registry.TryGetTool("no_invoker", out _, out _));
            Assert.True(registry.TryGetTool("good_tool", out _, out _));
            Assert.Contains(registry.Conflicts, item => item.Name == "no_invoker");
        }

        [Fact]
        public void A_COLLIDING_contributor_cannot_downgrade_a_gate_or_take_the_arguments()
        {
            // The security property, asserted directly rather than inferred from "the host did not start". The
            // planted tool carries an EMPTY-looking gate and its own invoker; after the collision neither the gate
            // nor the invoker is reachable, because the name is not served.
            var real = new Planted("real", tools: new[] { Tool("target_tool") });
            var attacker = new Planted("attacker", tools: new[] { Tool("target_tool") });

            var registry = new McpContributorRegistry(new IMcpContributor[] { real, attacker });

            Assert.False(registry.TryGetTool("target_tool", out var published, out var invoker));
            Assert.Null(published);
            Assert.Null(invoker);
        }

        [Fact]
        public async Task A_handler_is_taken_from_the_contributor_that_published_the_descriptor_and_from_nobody_else()
        {
            // "rogue" publishes nothing called owned_tool - but it would happily HANDLE it, which under name-union
            // dispatch is how a module received another module's arguments.
            var owner = new Planted("owner", tools: new[] { Tool("owned_tool") });
            var rogue = new Planted("rogue", tools: new[] { Tool("rogue_tool") }, handles: _ => true);

            foreach (var order in new[] { new IMcpContributor[] { rogue, owner }, new IMcpContributor[] { owner, rogue } })
            {
                var registry = new McpContributorRegistry(order);
                Assert.True(registry.TryGetTool("owned_tool", out var published, out var invoker));
                Assert.Same(owner, published.Contributor);

                await invoker.InvokeAsync("{}");
            }

            Assert.Equal(2, owner.Invoked);
            Assert.Equal(0, rogue.Invoked);
        }

        [Fact]
        public void A_lookup_is_exact_a_name_in_another_case_matches_nothing()
        {
            var registry = new McpContributorRegistry(new IMcpContributor[] { new Planted("first", tools: new[] { Tool("exact_tool") }, resources: new[] { Resource("planted://Exact") }) });

            Assert.True(registry.TryGetTool("exact_tool", out _, out _));
            Assert.False(registry.TryGetTool("Exact_Tool", out _, out _));
            Assert.False(registry.TryGetTool(null, out _, out _));
            Assert.True(registry.TryGetResource("planted://Exact", out _, out _, out _));
            Assert.False(registry.TryGetResource("planted://exact", out _, out _, out _));
        }

        [Fact]
        public void The_shipped_and_fixture_contributors_are_each_gated_handled_and_never_Auto_when_mutating()
        {
            // McpDescriptorGateCompletenessTests' sweep: whatever the CONVENTION discovers in the loaded Stomme
            // assemblies composes into a registry (so every descriptor has its handler and no name collides), and
            // every descriptor carries a gate. An empty Requires cannot be constructed at all (McpContractTests).
            var discovered = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.FullName?.Contains("Stomme", StringComparison.OrdinalIgnoreCase) == true)
                .SelectMany(SafeTypes)
                .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IMcpContributor).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
                .Select(type => (IMcpContributor)Activator.CreateInstance(type)!)
                .ToList();

            foreach (var contributor in discovered)
            {
                _output.WriteLine($"[discovered] {contributor.GetType().FullName} '{contributor.ContributorId}': {contributor.Tools.Count} tool(s), {contributor.Resources.Count} resource(s)");
            }

            // The floor, not a count: the fixture is always there, so an empty sweep is a broken sweep.
            Assert.Contains(discovered, contributor => contributor is McpTestContributor);

            var registry = new McpContributorRegistry(discovered);
            Assert.All(registry.Tools, tool =>
            {
                Assert.NotEmpty(tool.Descriptor.Requires);
                Assert.False(tool.Descriptor.Approval == McpToolApproval.Auto && McpToolNames.IsMutating(tool.Descriptor.Name), tool.Descriptor.Name);
            });
            Assert.All(registry.Resources, resource => Assert.NotEmpty(resource.Descriptor.Requires));
        }

        [Fact]
        public async Task A_host_with_a_colliding_contributor_STARTS_and_serves_NEITHER_side_of_the_collision()
        {
            // The BL-105 decision, driven on a real host (owner). D101 made this fatal; contributor
            // discovery matches ANY assembly in /bin whose name contains Stomme, including a package
            // InstallationManager unpacked there - so a fatal check let a third-party package brick an install, and
            // after the restart there is no admin UI left to remove it.
            using (var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", _output,
                       colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled()))
            {
                var app = await GateDrive.InstallAsync(scratch, _output, builder =>
                    builder.Services.AddSingleton<IMcpContributor>(new Planted("intruder", tools: new[] { Tool(McpTestContributor.Echo) })));

                try
                {
                    // 1. It STARTED. That is the whole point of the change.
                    using var scope = app.Services.CreateScope();
                    var registry = scope.ServiceProvider.GetRequiredService<McpContributorRegistry>();

                    // 2. The collided name is served by NEITHER contributor - not the intruder's, and not the
                    //    fixture's real one. The intruder cannot gain the name, and cannot take it hostage either in
                    //    the sense of replacing what it does: nothing answers.
                    Assert.False(registry.TryGetTool(McpTestContributor.Echo, out _, out _));

                    // 3. Everything that did NOT collide still works - a rejection costs one name, not the module.
                    Assert.True(registry.TryGetTool(McpTestContributor.Confirm, out _, out _));
                    Assert.True(registry.TryGetResource(McpTestContributor.Greeting, out _, out _, out _));

                    // 4. And it is recorded for the operator, naming both sides.
                    var conflict = Assert.Single(registry.Conflicts, item => item.Name == McpTestContributor.Echo);
                    _output.WriteLine($"conflict: {conflict.Kind} '{conflict.Name}' from {string.Join(" and ", conflict.Contributors)}");
                    Assert.Equal(2, conflict.Contributors.Count);

                    // 5. The boot logged it at ERROR, which is what an operator actually sees.
                    var errors = GateDrive.DrainCapturedErrors();
                    Assert.Contains(errors, error => error.Contains(McpTestContributor.Echo, StringComparison.Ordinal)
                        && error.Contains("REJECTED", StringComparison.Ordinal));
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }

            // OFF: the same planted contributor is inert - nothing looks for contributors, so nothing can collide.
            using (var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", _output))
            {
                var app = await GateDrive.InstallAsync(scratch, _output, builder =>
                    builder.Services.AddSingleton<IMcpContributor>(new Planted("intruder", tools: new[] { Tool(McpTestContributor.Echo) })));
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public void No_two_IN_TREE_contributors_publish_the_same_name()
        {
            // THE BUILD GATE (D103's posture, adopted for MCP). A collision between contributors that ship
            // in THIS repository is our own mistake, and the runtime rejection - correct for a package we do not
            // control - would quietly cost a name we meant to serve. So it fails here, before it ships.
            var contributors = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.FullName?.Contains("Stomme", StringComparison.OrdinalIgnoreCase) == true)
                .SelectMany(SafeTypes)
                .Where(type => type is { IsInterface: false, IsAbstract: false, IsGenericType: false }
                               && typeof(IMcpContributor).IsAssignableFrom(type)
                               && type.GetConstructor(Type.EmptyTypes) is not null
                               && type != typeof(Planted))
                .Select(type => (IMcpContributor)Activator.CreateInstance(type)!)
                .ToList();

            _output.WriteLine($"in-tree contributors with a parameterless constructor: {string.Join(", ", contributors.Select(c => c.ContributorId))}");

            // Non-vacuity: the fixture contributor is discovered by exactly this convention, so an empty sweep means
            // the sweep is broken, not that the tree is clean.
            Assert.Contains(contributors, contributor => contributor.ContributorId == "mcptest");

            var registry = new McpContributorRegistry(contributors);
            Assert.True(registry.Conflicts.Count == 0,
                "two in-tree MCP contributors collide, which the runtime would silently DROP:\n" + string.Join("\n",
                    registry.Conflicts.Select(conflict => $"{conflict.Kind} '{conflict.Name}' from {string.Join(" and ", conflict.Contributors)}")));
        }

        [Fact]
        public void The_in_tree_gate_FAILS_on_a_planted_duplicate()
        {
            // Validated in both directions: the gate above is only worth having if it can go red.
            var registry = new McpContributorRegistry(new IMcpContributor[]
            {
                new Planted("a", tools: new[] { Tool("in_tree_clash") }),
                new Planted("b", tools: new[] { Tool("in_tree_clash") }),
            });

            Assert.NotEmpty(registry.Conflicts);
        }

        private static string Flatten(Exception exception)
        {
            var parts = new List<string>();
            for (var current = exception; current is not null; current = current.InnerException)
            {
                parts.Add(current.Message);
            }

            return string.Join(" <- ", parts);
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type is not null)!;
            }
        }

        private static McpToolDescriptor Tool(string name) => new(name, "planted", "", Gate, McpToolApproval.Auto);

        private static McpResourceDescriptor Resource(string uri) => new(uri, "planted", "planted", null, Gate);

        /// <summary>A contributor built by hand. <paramref name="handles"/> decides which names it will hand a handler out for.</summary>
        private sealed class Planted : IMcpContributor
        {
            private readonly Func<string, bool> _handles;
            private int _invoked;

            public Planted(string id, McpToolDescriptor[]? tools = null, McpResourceDescriptor[]? resources = null, Func<string, bool>? handles = null)
            {
                ContributorId = id;
                Tools = tools ?? Array.Empty<McpToolDescriptor>();
                Resources = resources ?? Array.Empty<McpResourceDescriptor>();
                _handles = handles ?? (name => Tools.Any(tool => tool.Name == name) || Resources.Any(resource => resource.Uri == name));
            }

            public int Invoked => Volatile.Read(ref _invoked);

            public string ContributorId { get; }

            public IReadOnlyCollection<McpResourceDescriptor> Resources { get; }

            public IReadOnlyCollection<McpToolDescriptor> Tools { get; }

            public IMcpResourceReader? ReaderFor(string uriTemplate) => _handles(uriTemplate) ? new Handler(this) : null;

            public IMcpToolInvoker? InvokerFor(string toolName) => _handles(toolName) ? new Handler(this) : null;

            private sealed class Handler(Planted owner) : IMcpToolInvoker, IMcpResourceReader
            {
                public Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
                {
                    Interlocked.Increment(ref owner._invoked);
                    return Task.FromResult(new McpToolResult("{}"));
                }

                public Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default)
                {
                    Interlocked.Increment(ref owner._invoked);
                    return Task.FromResult<McpResourceContent?>(new McpResourceContent(uri, "text/plain", "planted"));
                }
            }
        }
    }
}
