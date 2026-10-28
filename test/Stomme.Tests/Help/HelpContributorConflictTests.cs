#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Infrastructure;
using Stomme.Modules.ContextualHelp.Contributions;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Stomme.Modules.ContextualHelp.Startup;
using Stomme.Shared;
using Stomme.Tests.Fixtures.HelpTestModule;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// An (anchor, locale) belongs to ONE contribution (BL-84 increment 3, D103 refutation 8, BL-105's rule).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>EVERY side of a collision is dropped, and the host starts.</b> Discovery matches any assembly in <c>/bin</c>
    /// whose name contains <c>Stomme</c>, including a package unpacked there, so a fatal check would let a third party
    /// brick an install. And there is no "the framework wins": a seeded row belongs to its contributor, so a package that
    /// could WIN a key could take a framework topic's row on a fresh site and publish its own text under the framework's
    /// anchor to anonymous readers. Rejecting both sides means the key has no contributed topic at all.
    /// </para>
    /// <para>
    /// IN-TREE duplicates are our own mistake and fail the BUILD, in <see cref="No_two_IN_TREE_contributors_ship_the_same_topic"/>.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HelpContributorConflictTests
    {
        private readonly ITestOutputHelper _output;

        public HelpContributorConflictTests(ITestOutputHelper output) => _output = output;

        // ---- the registry's rules ----------------------------------------------------------------------------------

        [Fact]
        public void A_topic_shipped_by_two_contributors_is_dropped_ON_BOTH_SIDES()
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[]
            {
                new Planted("first", Topic("page:shared")),
                new Planted("second", Topic("page:shared")),
            });

            Assert.False(registry.TryGet("page:shared", "en", out _));
            Assert.Empty(registry.Topics);

            var conflict = Assert.Single(registry.Conflicts);
            _output.WriteLine($"{conflict.Kind} '{conflict.Name}' from {string.Join(" and ", conflict.Contributors)}: {conflict.Reason}");
            Assert.Equal("topic", conflict.Kind);
            Assert.Equal(new[] { "first", "second" }, conflict.Contributors.OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public void A_THIRD_contributor_cannot_resurrect_a_topic_that_was_already_rejected()
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[]
            {
                new Planted("first", Topic("page:shared")),
                new Planted("second", Topic("page:shared")),
                new Planted("third", Topic("page:shared")),
            });

            Assert.False(registry.TryGet("page:shared", "en", out _));
            Assert.Empty(registry.Topics);
            Assert.Equal(2, registry.Conflicts.Count);
        }

        [Fact]
        public void ONE_contributor_shipping_the_same_topic_twice_loses_it_too()
        {
            // Which text would be the seed? Ambiguity is the same defect as a collision, so it gets the same rule.
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[]
            {
                new Planted("clumsy", Topic("page:twice", title: "One"), Topic("PAGE:Twice", title: "Two"), Topic("page:fine")),
            });

            Assert.False(registry.TryGet("page:twice", "en", out _));
            Assert.True(registry.TryGet("page:fine", "en", out _));
        }

        [Fact]
        public void The_same_anchor_in_two_LOCALES_is_two_topics_and_not_a_collision()
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[]
            {
                new Planted("first", Topic("page:x", "en")),
                new Planted("second", Topic("page:x", "sv-SE")),
            });

            Assert.Empty(registry.Conflicts);
            Assert.Equal("first", Get(registry, "page:x", "en").ContributorId);
            Assert.Equal("second", Get(registry, "page:x", "sv-se").ContributorId);
        }

        [Fact]
        public void Two_contributors_with_one_id_are_BOTH_dropped_with_everything_they_ship()
        {
            // The id OWNS every row it seeds. Two claimants means neither can own anything.
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[]
            {
                new Planted("same", Topic("page:one")),
                new Planted("SAME", Topic("page:two")),
                new Planted("Same ", Topic("page:three")),
            });

            Assert.Empty(registry.Topics);
            Assert.All(registry.Conflicts, conflict => Assert.Equal("contributor", conflict.Kind));
            Assert.Equal(2, registry.Conflicts.Count);
        }

        [Fact]
        public void A_contributor_with_NO_id_or_a_THROWING_getter_is_dropped_and_the_healthy_ones_survive()
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor?[]
            {
                new Planted("  ", Topic("page:orphan")),
                new Throwing("the package is broken"),
                null,
                new Planted("good", Topic("page:good")),
            }!);

            Assert.False(registry.TryGet("page:orphan", "en", out _));
            Assert.True(registry.TryGet("page:good", "en", out _));
            Assert.Equal(2, registry.Conflicts.Count(conflict => conflict.Kind == "contributor"));
            Assert.Contains(registry.Conflicts, conflict => conflict.Reason.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        }

        [Fact]
        public void A_NULL_topic_costs_that_entry_and_nothing_else()
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[]
            {
                new Planted("sloppy", null!, Topic("page:kept")),
            });

            Assert.True(registry.TryGet("page:kept", "en", out _));
            Assert.Single(registry.Conflicts, conflict => conflict.Name == "(null)");
        }

        [Fact]
        public void The_outcome_does_not_depend_on_DISCOVERY_ORDER()
        {
            // Assembly load order is not something an install controls, so a rule that depended on it would be a rule
            // an attacker could arrange to win.
            var contributors = new IHelpTopicContributor[]
            {
                new Planted("a", Topic("page:clash"), Topic("page:a")),
                new Planted("b", Topic("page:clash"), Topic("page:b")),
                new Planted("c", Topic("page:c")),
            };

            string Describe(IEnumerable<IHelpTopicContributor> order)
            {
                var registry = new HelpTopicContributorRegistry(order);
                return string.Join(";", registry.Topics.Select(topic => $"{topic.ContributorId}:{topic.Contribution.Anchor}"))
                       + " | " + string.Join(";", registry.Conflicts.Select(conflict => conflict.Name).OrderBy(name => name, StringComparer.Ordinal));
            }

            var expected = Describe(contributors);
            _output.WriteLine(expected);
            Assert.Equal("a:page:a;b:page:b;c:page:c | page:clash|en", expected);
            Assert.Equal(expected, Describe(contributors.Reverse()));
            Assert.Equal(expected, Describe(new[] { contributors[1], contributors[2], contributors[0] }));
        }

        [Theory]
        [InlineData(null, "en")]
        [InlineData("page:x", null)]
        [InlineData("has space", "en")]
        [InlineData("page:x", "not a locale")]
        public void A_lookup_with_wire_garbage_answers_no_topic_rather_than_throwing(string? anchor, string? locale)
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[] { new Planted("one", Topic("page:x")) });
            Assert.False(registry.TryGet(anchor, locale, out _));
        }

        [Fact]
        public void A_lookup_normalizes_the_key_exactly_as_a_contribution_does()
        {
            var registry = new HelpTopicContributorRegistry(new IHelpTopicContributor[] { new Planted("one", Topic("page:admin/users", "sv-SE")) });
            Assert.True(registry.TryGet("  PAGE:Admin/Users ", "SV-se", out var topic));
            Assert.Equal("one", topic.ContributorId);
        }

        // ---- the anchors the launcher will derive ------------------------------------------------------------------

        [Theory]
        [InlineData("Stomme.Modules.Admin.Users.Index, Stomme.Client", true, "stomme.modules.admin.users.index")]
        [InlineData("Stomme.Modules.Admin.Users.Edit, Stomme.Client", true, "stomme.modules.admin.users.edit")]
        [InlineData("Stomme.Modules.Admin.Users.Index", true, "stomme.modules.admin.users.index")]
        [InlineData("  My.Module.Index , My.Assembly", true, "my.module.index")]
        [InlineData("Outer+Nested, Assembly", false, "")]
        [InlineData("Generic`1, Assembly", false, "")]
        [InlineData("", false, "")]
        [InlineData("   ", false, "")]
        [InlineData(null, false, "")]
        public void A_module_anchor_keeps_the_TYPE_and_drops_the_assembly(string? moduleType, bool expected, string anchor)
        {
            Assert.Equal(expected, HelpKeys.TryModuleAnchor(moduleType, out var actual));
            Assert.Equal(anchor, actual);
        }

        [Fact]
        public void A_module_anchor_over_the_bound_is_refused_never_cut()
        {
            var tooLong = new string('a', HelpKeys.MaximumAnchorLength + 1) + ", Assembly";
            Assert.False(HelpKeys.TryModuleAnchor(tooLong, out _));
        }

        [Fact]
        public void Every_framework_control_anchor_is_what_the_ROUTER_would_produce_for_a_real_module_control()
        {
            // SiteRouter writes Utilities.GetFullTypeName(type.AssemblyQualifiedName) into PageState.Modules[].ModuleType.
            // A framework topic whose anchor the launcher can never derive is dead text; this is the check that it can.
            var controls = new[]
            {
                typeof(Stomme.Modules.Admin.Site.Index),
                typeof(Stomme.Modules.Admin.Pages.Index),
                typeof(Stomme.Modules.Admin.Users.Index),
                typeof(Stomme.Modules.Admin.ModuleDefinitions.Index),
                typeof(Stomme.Modules.Admin.Themes.Index),
            };

            var shipped = new StommeHelpContributor().Topics.Select(topic => topic.Anchor).ToHashSet(StringComparer.Ordinal);
            foreach (var control in controls)
            {
                Assert.True(typeof(Stomme.Modules.IModuleControl).IsAssignableFrom(control), control.FullName);
                Assert.True(HelpKeys.TryModuleAnchor(Utilities.GetFullTypeName(control.AssemblyQualifiedName!), out var routed));
                Assert.Contains(routed, shipped);
            }

            // ...and the page anchor for the admin dashboard.
            Assert.True(HelpKeys.TryPageAnchor("/admin/", out var dashboard));
            Assert.Contains(dashboard, shipped);
            // ...and the one FIELD anchor a Label names (HelpAnchorCoverageTests holds every label to a shipped topic).
            Assert.Contains(StommeHelpContributor.SiteRenderModeAnchor, shipped);
            Assert.Equal(controls.Length + 2, shipped.Count);
            Assert.All(new StommeHelpContributor().Topics, topic => Assert.Equal("en", topic.Locale));
        }

        // ---- the build gate ----------------------------------------------------------------------------------------

        [Fact]
        public void No_two_IN_TREE_contributors_ship_the_same_topic()
        {
            // THE BUILD GATE. It sweeps exactly what the host's convention registers (the same method), so the two
            // cannot drift. A collision between contributors that ship in this repository is our own mistake, and the
            // runtime rejection - correct for a package we do not control - would quietly cost a topic we meant to ship.
            var contributors = ConventionContributorDiscovery.Discover<IHelpTopicContributor>()
                .Select(type => (IHelpTopicContributor)Activator.CreateInstance(type)!)
                .ToList();

            _output.WriteLine("in-tree contributors: " + string.Join(", ", contributors.Select(contributor => contributor.ContributorId)));

            // Non-vacuity, as a floor: both are found by exactly this convention.
            Assert.Contains(contributors, contributor => contributor is StommeHelpContributor);
            Assert.Contains(contributors, contributor => contributor is HelpTestContributor);

            AssertNoConflicts(contributors);
        }

        [Fact]
        public void The_in_tree_gate_FAILS_on_a_planted_duplicate_of_a_FRAMEWORK_topic()
        {
            // Validated the other way: the same assertion, handed the real in-tree set PLUS a copy of one framework
            // topic under another id, must go red.
            var framework = new StommeHelpContributor();
            var copy = framework.Topics.First();
            var contributors = new List<IHelpTopicContributor> { framework, new HelpTestContributor(), new Planted("copycat", copy) };

            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoConflicts(contributors));
            _output.WriteLine(failure.Message);
            Assert.Contains(copy.Anchor, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_fixture_contributor_names_nothing_outside_Stomme_Shared_and_the_BCL()
        {
            // A packaged module must be able to contribute help by referencing Stomme.Shared and stopping (D103
            // item 2). The Roslyn compile probe in HelpContractTests is the strong form; this holds the FIXTURE to it.
            Assert.Empty(ForeignAssemblies(typeof(HelpTestContributor)));

            // Validated the other way: a type in the module does reach outside, and the same check sees it.
            Assert.Contains("Stomme.Server", ForeignAssemblies(typeof(HelpTopicContributorRegistry)));
        }

        // ---- composition: off means ABSENT --------------------------------------------------------------------------

        [Fact]
        public void With_Help_OFF_no_contributor_is_looked_for_and_nothing_renders()
        {
            var services = Compose(enabled: null);

            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHelpTopicContributor));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(HelpTopicContributorRegistry));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(HelpMarkdownRenderer));

            // "false" is off too, and so is anything that is not a boolean.
            Assert.DoesNotContain(Compose(enabled: "false"), descriptor => descriptor.ServiceType == typeof(IHelpTopicContributor));
            Assert.DoesNotContain(Compose(enabled: "yes"), descriptor => descriptor.ServiceType == typeof(IHelpTopicContributor));
        }

        [Fact]
        public void With_Help_ON_the_registry_composes_the_framework_and_fixture_topics_with_no_conflict()
        {
            using var provider = Compose(enabled: "true").BuildServiceProvider();
            using var scope = provider.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<HelpTopicContributorRegistry>();

            Assert.Empty(registry.Conflicts);
            Assert.Equal(StommeHelpContributor.Id, Get(registry, "page:admin", "en").ContributorId);
            Assert.Equal(HelpTestContributor.Id, Get(registry, HelpTestContributor.Anchor, "sv-SE").ContributorId);
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<HelpMarkdownRenderer>());
        }

        // ---- a real host ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_host_with_a_colliding_contributor_STARTS_and_neither_side_keeps_the_topic()
        {
            // The rule, driven on a real host: a planted contributor (standing in for a package unpacked into /bin)
            // ships the fixture's English topic under its own id.
            var intruder = new Planted("intruder", new HelpTopicContribution(HelpTestContributor.Anchor, "en", "Hijacked", "Not the real text."));

            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", _output,
                colocateModuleAggregates: false, extraSettings: HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
                builder.Services.AddSingleton<IHelpTopicContributor>(intruder));

            try
            {
                // 1. It STARTED.
                using var scope = app.Services.CreateScope();
                var registry = scope.ServiceProvider.GetRequiredService<HelpTopicContributorRegistry>();

                // 2. The collided key belongs to NEITHER - not the intruder, and not the fixture's real topic.
                Assert.False(registry.TryGet(HelpTestContributor.Anchor, "en", out _));

                // 3. What did not collide is untouched: the fixture's Swedish topic and every framework topic.
                Assert.Equal(HelpTestContributor.Id, Get(registry, HelpTestContributor.Anchor, "sv-SE").ContributorId);
                Assert.Equal(StommeHelpContributor.Id, Get(registry, "page:admin", "en").ContributorId);

                // 4. Recorded for the operator, naming both sides...
                var conflict = Assert.Single(registry.Conflicts);
                Assert.Equal(new[] { HelpTestContributor.Id, "intruder" }, conflict.Contributors.OrderBy(name => name, StringComparer.Ordinal));

                // 5. ...and logged at ERROR at boot, which is what an operator actually sees.
                var errors = GateDrive.DrainCapturedErrors();
                Assert.Contains(errors, error => error.Contains(HelpTestContributor.Anchor, StringComparison.Ordinal)
                                                 && error.Contains("REJECTED", StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        internal static JsonObject HelpEnabled() => new()
        {
            ["Help"] = new JsonObject { ["Enabled"] = true },
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Warning" } },
        };

        // ---- helpers ------------------------------------------------------------------------------------------------

        private static void AssertNoConflicts(IEnumerable<IHelpTopicContributor> contributors)
        {
            var registry = new HelpTopicContributorRegistry(contributors);
            Assert.True(registry.Conflicts.Count == 0,
                "two in-tree help contributors collide, which the runtime would silently DROP:\n" + string.Join("\n",
                    registry.Conflicts.Select(conflict => $"{conflict.Kind} '{conflict.Name}' from {string.Join(" and ", conflict.Contributors)}")));
        }

        private static IServiceCollection Compose(string? enabled)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                ["ConnectionStrings:ModuleAggregates"] = "Data Source=:memory:",
            };
            if (enabled is not null)
            {
                settings[HelpSettings.EnabledKey] = enabled;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            services.AddScoped<SiteState>();
            new ServerStartup().ConfigureServices(services);
            return services;
        }

        private static HelpContributedTopic Get(HelpTopicContributorRegistry registry, string anchor, string locale)
        {
            Assert.True(registry.TryGet(anchor, locale, out var topic), $"no contributed topic for {anchor} / {locale}");
            return topic;
        }

        /// <summary>Assemblies a type's PUBLIC surface names, other than Stomme.Shared and the BCL.</summary>
        private static IReadOnlyList<string> ForeignAssemblies(Type type)
        {
            var named = new List<Type> { type.BaseType ?? typeof(object) };
            named.AddRange(type.GetInterfaces());
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case PropertyInfo property:
                        named.Add(property.PropertyType);
                        break;
                    case MethodInfo method:
                        named.Add(method.ReturnType);
                        named.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
                        break;
                    case ConstructorInfo constructor:
                        named.AddRange(constructor.GetParameters().Select(parameter => parameter.ParameterType));
                        break;
                    case FieldInfo field:
                        named.Add(field.FieldType);
                        break;
                }
            }

            return named
                .SelectMany(Flatten)
                .Select(item => item.Assembly.GetName().Name ?? string.Empty)
                .Where(name => name != "Stomme.Shared" && !name.StartsWith("System", StringComparison.Ordinal) && name != "mscorlib" && name != "netstandard")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            static IEnumerable<Type> Flatten(Type item) =>
                item.IsGenericType
                    ? new[] { item.GetGenericTypeDefinition() }.Concat(item.GetGenericArguments().SelectMany(Flatten))
                    : item.HasElementType ? Flatten(item.GetElementType()!) : new[] { item };
        }

        private static HelpTopicContribution Topic(string anchor, string locale = "en", string title = "Title") =>
            new(anchor, locale, title, "Body for " + anchor + ".");

        /// <summary>A contributor built by hand, standing in for a package.</summary>
        private sealed class Planted : IHelpTopicContributor
        {
            public Planted(string id, params HelpTopicContribution[] topics)
            {
                ContributorId = id;
                Topics = topics;
            }

            public string ContributorId { get; }

            public IReadOnlyCollection<HelpTopicContribution> Topics { get; }
        }

        /// <summary>A package whose getter throws - a malformed anchor in a static initializer is the likely cause.</summary>
        /// <remarks>
        /// The constructor takes a parameter ON PURPOSE: with a parameterless one, the convention would DISCOVER this type
        /// and plant it in the in-tree gate and in every Help-enabled test host.
        /// </remarks>
        private sealed class Throwing : IHelpTopicContributor
        {
            public Throwing(string reason) => Reason = reason;

            public string Reason { get; }

            public string ContributorId => "throwing";

            public IReadOnlyCollection<HelpTopicContribution> Topics => throw new InvalidOperationException(Reason);
        }
    }
}
