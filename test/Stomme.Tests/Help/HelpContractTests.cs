#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stomme.Help;
using Stomme.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// The contextual-help contribution SEAM: what it refuses, and what it refuses
    /// to depend on.
    /// </summary>
    /// <remarks>
    /// The seam is NEW design, not a lift - D103 falsified "its topics travel with their modules" (0 of the predecessor platform's 36
    /// modules shipped one), and what the predecessor platform called a contribution seam was a two-method read port. So every refusal
    /// here answers something the design decided, and each one says which.
    /// </remarks>
    public class HelpContractTests
    {
        private readonly ITestOutputHelper _output;

        public HelpContractTests(ITestOutputHelper output) => _output = output;

        // ---- the anchor and locale normalizers: NON-THROWING, because the wire supplies them --------------------

        [Theory]
        [InlineData("page:admin/users", "page:admin/users")]
        [InlineData("  Page:Admin/Users  ", "page:admin/users")]
        [InlineData("Stomme.Modules.Admin.Users", "stomme.modules.admin.users")]
        [InlineData("admin-users_2", "admin-users_2")]
        public void An_anchor_is_normalized_to_lower_case_and_trimmed(string input, string expected)
        {
            Assert.True(HelpKeys.TryNormalizeAnchor(input, out var normalized));
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("has space")]
        [InlineData("has\"quote")]
        [InlineData("has<angle>")]
        [InlineData("has%encoded")]
        [InlineData("has\nnewline")]
        public void An_unusable_anchor_is_REFUSED_and_does_not_throw(string? input)
        {
            // D103 refutation 10: this input arrives as ?anchor= on an endpoint an ANONYMOUS visitor can reach.
            // the predecessor platform's factory threw, which turns a malformed query string into a 500. "No topic" is the answer.
            Assert.False(HelpKeys.TryNormalizeAnchor(input, out var normalized));
            Assert.Equal(string.Empty, normalized);
        }

        [Fact]
        public void An_over_long_anchor_is_refused_rather_than_CUT()
        {
            // Cutting would silently map two different anchors onto one topic.
            var atTheLimit = new string('a', HelpKeys.MaximumAnchorLength);
            Assert.True(HelpKeys.TryNormalizeAnchor(atTheLimit, out _));
            Assert.False(HelpKeys.TryNormalizeAnchor(atTheLimit + "a", out _));
        }

        [Theory]
        [InlineData("en", "en")]
        [InlineData("EN", "en")]
        [InlineData(" en-GB ", "en-gb")]
        [InlineData("sv-SE", "sv-se")]
        public void A_locale_is_normalized_through_CultureInfo(string input, string expected)
        {
            Assert.True(HelpKeys.TryNormalizeLocale(input, out var normalized));
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a locale")]
        [InlineData("this-is-far-too-long-to-be-a-locale")]
        public void An_unusable_locale_is_REFUSED_and_does_not_throw(string? input) =>
            Assert.False(HelpKeys.TryNormalizeLocale(input, out _));

        [Fact]
        public void CultureInfo_ACCEPTS_a_well_formed_tag_it_has_never_heard_of_and_that_is_left_alone()
        {
            // MEASURED, and not what the first version of this test assumed: GetCultureInfo builds a CUSTOM culture
            // for any well-formed tag, so "zz-ZZ" is accepted. That is deliberately not tightened with
            // predefinedOnly - the risk of refusing a legitimate tag this platform happens not to predefine is worse
            // than the cost of accepting an exotic one, which is simply a topic nothing ever resolves to. The bound
            // that matters is the LENGTH, which is enforced.
            Assert.True(HelpKeys.TryNormalizeLocale("zz-ZZ", out var normalized));
            Assert.Equal("zz-zz", normalized);
        }

        [Fact]
        public void The_INVARIANT_culture_is_not_a_locale_a_topic_can_be_written_in()
        {
            // "" resolves to the invariant culture, whose Name is "" - which would key every topic under the empty
            // string and make the fallback chain meaningless.
            Assert.False(HelpKeys.TryNormalizeLocale(string.Empty, out _));
        }

        [Fact]
        public void A_page_anchor_carries_the_prefix_and_survives_stray_slashes()
        {
            Assert.True(HelpKeys.TryPageAnchor("/admin/users/", out var anchor));
            Assert.Equal("page:admin/users", anchor);

            // The site root is a page too, and it has an anchor.
            Assert.True(HelpKeys.TryPageAnchor("", out var root));
            Assert.Equal("page:", root);
        }

        // ---- the contribution: a contributor is CODE, so it is refused loudly ------------------------------------

        [Fact]
        public void A_contribution_normalizes_what_it_is_given()
        {
            var topic = new HelpTopicContribution("  Page:Admin/Users ", "EN", "  Users  ", "Manage **users**.", 3);

            Assert.Equal("page:admin/users", topic.Anchor);
            Assert.Equal("en", topic.Locale);
            Assert.Equal("Users", topic.Title);
            Assert.Equal("Manage **users**.", topic.Markdown);   // the BODY is not trimmed - leading space can be syntax
            Assert.Equal(3, topic.Order);
        }

        [Theory]
        [InlineData("", "en", "Title", "Body")]
        [InlineData("has space", "en", "Title", "Body")]
        [InlineData("page:x", "not a locale", "Title", "Body")]
        [InlineData("page:x", "en", "", "Body")]
        [InlineData("page:x", "en", "Title", "")]
        public void A_contribution_with_unusable_input_THROWS(string anchor, string locale, string title, string markdown)
        {
            // The opposite of the wire: a contributor is in-process code shipping a constant, so a malformed anchor is
            // a bug where it is written and should be loud there.
            Assert.Throws<ArgumentException>(() => new HelpTopicContribution(anchor, locale, title, markdown));
        }

        [Fact]
        public void A_body_is_bounded_in_CHARACTERS_and_the_column_is_not()
        {
            var atTheLimit = new string('x', HelpTopicContribution.MaximumMarkdownCharacters);
            Assert.Equal(atTheLimit, new HelpTopicContribution("page:x", "en", "Title", atTheLimit).Markdown);
            Assert.Throws<ArgumentException>(() => new HelpTopicContribution("page:x", "en", "Title", atTheLimit + "x"));

            // 32 000 is not a valid nvarchar length on SQL Server, so the bound lives in the domain and the column
            // stays unbounded. A MaxLength const here would invite somebody to map it.
            Assert.Empty(typeof(HelpTopicContribution).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.Name.Contains("MaxLength", StringComparison.Ordinal)));
        }

        // ---- the deterministic id ---------------------------------------------------------------------------------

        /// <summary>The single-tenant install's tenant (<c>TenantId.Default</c>), as the seam receives it.</summary>
        private static readonly Guid Tenant = new("00000000-0000-0000-0000-000000000001");

        private static readonly Guid OtherTenant = new("7c0b1d2e-0000-4000-8000-000000000002");

        [Fact]
        public void The_id_is_the_SAME_for_the_same_tenant_site_anchor_and_locale()
        {
            var first = HelpTopicIds.For(Tenant, 7, "page:admin/users", "en");
            var second = HelpTopicIds.For(Tenant, 7, "  PAGE:Admin/Users ", "EN");

            Assert.Equal(first, second);
            Assert.NotEqual(Guid.Empty, first);
            _output.WriteLine($"id(default tenant, 7, page:admin/users, en) = {first}");

            // It is a version-5 name-based UUID with the RFC 4122 variant, written big-endian - so it does not depend
            // on Guid's own little-endian layout, which is the trap a naive new Guid(hash) falls into.
            var bytes = first.ToByteArray();
            Assert.Equal(0x50, bytes[7] & 0xF0);                 // version nibble, after the endian swap
            Assert.Equal(0x80, bytes[8] & 0xC0);                 // variant
        }

        [Fact]
        public void The_id_DIFFERS_by_tenant_by_site_by_anchor_and_by_locale()
        {
            var baseline = HelpTopicIds.For(Tenant, 7, "page:admin/users", "en");

            // The TENANT first, because it is the one that was missing (BL-151's class): site ids repeat across tenants,
            // and the module store is one store for the whole install.
            Assert.NotEqual(baseline, HelpTopicIds.For(OtherTenant, 7, "page:admin/users", "en"));
            Assert.NotEqual(baseline, HelpTopicIds.For(Tenant, 8, "page:admin/users", "en"));
            Assert.NotEqual(baseline, HelpTopicIds.For(Tenant, 7, "page:admin/pages", "en"));
            Assert.NotEqual(baseline, HelpTopicIds.For(Tenant, 7, "page:admin/users", "sv"));
        }

        [Fact]
        public void The_separator_cannot_be_moved_between_the_parts()
        {
            // ("a|b", "c") and ("a", "b|c") must not collide - which they would if the separator could appear inside
            // a normalized part. It cannot, and this asserts the consequence rather than the rule.
            Assert.False(HelpKeys.TryNormalizeAnchor("a|b", out _));

            // "a" + "en-gb" and "a-en" + "gb" would collide if the separator could be absorbed into a part.
            Assert.NotEqual(HelpTopicIds.For(Tenant, 1, "a", "en-GB"), HelpTopicIds.For(Tenant, 1, "a-en", "gb"));
        }

        [Fact]
        public void The_id_refuses_what_it_cannot_key_on()
        {
            Assert.Throws<ArgumentException>(() => HelpTopicIds.For(Guid.Empty, 1, "page:x", "en"));
            Assert.Throws<ArgumentOutOfRangeException>(() => HelpTopicIds.For(Tenant, 0, "page:x", "en"));
            Assert.Throws<ArgumentOutOfRangeException>(() => HelpTopicIds.For(Tenant, -1, "page:x", "en"));
            Assert.Throws<ArgumentException>(() => HelpTopicIds.For(Tenant, 1, "has space", "en"));
            Assert.Throws<ArgumentException>(() => HelpTopicIds.For(Tenant, 1, "page:x", "not a locale"));
        }

        [Fact]
        public void The_id_is_STABLE_against_a_recorded_value()
        {
            // A regression here re-keys every topic in every install, which is a migration and not an edit. Recorded
            // so that a change to the namespace or the construction cannot be made by accident. The value was computed
            // by an INDEPENDENT re-implementation of RFC 4122 section 4.3 (PowerShell, 2026-09-23), which also reproduced
            // the previous tenant-less value exactly - so it is an oracle, not a copy of this code's own output.
            // CHANGED 2026-09-23 from 52582910-f71f-5b88-b8d2-646369a2281f when the tenant joined the key; no install
            // held a help topic yet (the reconciler had not been built), so nothing was re-keyed.
            Assert.Equal(Guid.Parse("9ad4cf9d-8e3a-5cf8-8514-2506c9b0b66e"), HelpTopicIds.For(Tenant, 1, "page:admin", "en"));
        }

        // ---- the permission keys ---------------------------------------------------------------------------------

        [Fact]
        public void Three_keys_and_only_VIEW_is_held_by_everyone()
        {
            // D103 refutation 15: the UNIFORM Declare applies one role segment to every key, so declaring these with
            // Everyone would seed help:manage to All Users - and UserSecurity.IsAuthorized admits All Users at
            // userId -1, so an ANONYMOUS visitor would author and manage help.
            Assert.Equal(new[] { HelpPermissions.View, HelpPermissions.Author, HelpPermissions.Manage }, HelpPermissions.All);

            var declarations = HelpPermissions.Declarations.ToDictionary(entry => entry.Key, entry => entry.Roles);
            Assert.Equal(new[] { RoleNames.Everyone }, declarations[HelpPermissions.View]);
            Assert.Empty(declarations[HelpPermissions.Author]);
            Assert.Empty(declarations[HelpPermissions.Manage]);
        }

        // ---- the seam names nothing it must not ------------------------------------------------------------------

        [Fact]
        public void A_contributor_compiles_against_Stomme_Shared_AND_THE_BCL_ALONE()
        {
            const string source = @"
using System.Collections.Generic;
using Stomme.Help;
using Stomme.Shared;

public sealed class PackagedHelpContributor : IHelpTopicContributor
{
    public string ContributorId => ""packaged"";

    public IReadOnlyCollection<HelpTopicContribution> Topics { get; } = new[]
    {
        new HelpTopicContribution(""page:example"", ""en"", ""Example"", ""An **example**."")
    };
}";

            var errors = Compile(source, withTheServer: false);
            Assert.True(errors.Count == 0, "a contributor must compile against Stomme.Shared alone:\n" + string.Join("\n", errors));
        }

        [Fact]
        public void The_compile_probe_REALLY_REFUSES_the_server_which_is_what_makes_the_case_above_mean_something()
        {
            // Validated in both directions: without this, the case above would pass just as well if the probe silently
            // referenced everything.
            const string source = @"
using Stomme.Infrastructure;

public sealed class ReachesIntoTheServer
{
    public object Reach() => HostConfiguration.Resolve(null!);
}";

            // MEASURED: the error is CS0234 ("the namespace Stomme.Infrastructure does not exist"), and WITH the
            // server reference it becomes CS0012 - a TRANSITIVE assembly the probe does not carry - which means the
            // namespace was found. That discrimination is the assertion. A clean compile with the server would be
            // asserting something else entirely: the probe references the BCL and one Stomme assembly, never the
            // server's own dependency graph.
            const string missing = "CS0234";

            var without = Compile(source, withTheServer: false);
            var with = Compile(source, withTheServer: true);
            _output.WriteLine($"without the server: {without.Count} error(s); with it: {with.Count}");

            _output.WriteLine("without: " + string.Join(" | ", without.Take(3)));
            _output.WriteLine("with: " + string.Join(" | ", with.Take(3)));
            Assert.Contains(without, error => error.Contains(missing, StringComparison.Ordinal));
            Assert.DoesNotContain(with, error => error.Contains(missing, StringComparison.Ordinal));
        }

        [Fact]
        public void The_seam_names_no_type_from_outside_Stomme_Shared_or_the_BCL()
        {
            // The reflective half of the same claim, with a FLOOR so it cannot sweep nothing.
            var seam = new[]
            {
                typeof(IHelpTopicContributor), typeof(IHelpTopicProvider), typeof(HelpTopicContribution),
                typeof(HelpTopicView), typeof(HelpTopicSummary), typeof(HelpKeys), typeof(HelpTopicIds),
                typeof(HelpPermissions),
            };

            var named = new List<Type>();
            foreach (var type in seam)
            {
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    named.AddRange(Referenced(member));
                }
            }

            Assert.True(named.Count >= 20, $"the sweep found only {named.Count} referenced types - it is not sweeping the seam");

            var foreign = named
                .Where(type => type.Assembly != typeof(IHelpTopicContributor).Assembly && !IsFrameworkAssembly(type.Assembly))
                .Select(type => type.FullName!)
                .Distinct()
                .ToList();

            _output.WriteLine($"swept {named.Count} referenced types across {seam.Length} seam types");
            Assert.True(foreign.Count == 0, "the seam names types outside Stomme.Shared and the BCL:\n" + string.Join("\n", foreign));
        }

        private static IEnumerable<Type> Referenced(MemberInfo member) => member switch
        {
            MethodInfo method => method.GetParameters().Select(parameter => Unwrap(parameter.ParameterType)).Append(Unwrap(method.ReturnType)),
            PropertyInfo property => new[] { Unwrap(property.PropertyType) },
            FieldInfo field => new[] { Unwrap(field.FieldType) },
            ConstructorInfo constructor => constructor.GetParameters().Select(parameter => Unwrap(parameter.ParameterType)),
            _ => Array.Empty<Type>(),
        };

        private static Type Unwrap(Type type)
        {
            while (type.IsByRef || type.IsArray || type.IsPointer)
            {
                type = type.GetElementType()!;
            }

            return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        }

        private static bool IsFrameworkAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? string.Empty;
            return name is "System.Private.CoreLib" or "System.Runtime" or "netstandard"
                || name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal);
        }

        private static List<string> Compile(string source, bool withTheServer)
        {
            var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = Directory.GetFiles(frameworkDirectory, "*.dll")
                .Where(IsManaged)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();

            references.Add(MetadataReference.CreateFromFile(typeof(IHelpTopicContributor).Assembly.Location));
            if (withTheServer)
            {
                references.Add(MetadataReference.CreateFromFile(typeof(Stomme.Infrastructure.HostConfiguration).Assembly.Location));
            }

            return CSharpCompilation.Create(
                    "HelpSeamProbe",
                    new[] { CSharpSyntaxTree.ParseText(source) },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString())
                .ToList();
        }

        private static bool IsManaged(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
    }
}
