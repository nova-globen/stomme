#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stomme.Mcp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// BL-80 (D101), increment 1 - the MCP contribution seam: what a descriptor REFUSES to be.
    /// </summary>
    /// <remarks>
    /// Every refusal here answers something measured on the predecessor platform: an empty gate list meant "every authenticated principal
    /// on every site" and was the default; the one mutating tool arrived as Auto; a name decided by first-registration
    /// let a rogue module stand in front of a real handler. The last of those is the catalog's to enforce (a later
    /// increment); the first two are the constructor's, so they hold for a packaged module no scanner can see.
    /// </remarks>
    public class McpContractTests
    {
        private static readonly IReadOnlyList<McpAccessGate> Gated = new[] { McpAccessGate.ForPermission("mcp:view") };

        private readonly ITestOutputHelper _output;

        public McpContractTests(ITestOutputHelper output) => _output = output;

        // ---- gates ---------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData(" ", "\t")]
        public void A_gate_that_names_neither_axis_is_refused(string? feature, string? permission)
        {
            Assert.Throws<ArgumentException>(() => new McpAccessGate(feature, permission));
        }

        [Fact]
        public void A_gate_keeps_the_axes_it_was_given_and_blank_is_null()
        {
            var both = new McpAccessGate("mcp.server", "mcp:view");
            Assert.Equal(("mcp.server", "mcp:view"), (both.Feature, both.Permission));
            Assert.Null(McpAccessGate.ForPermission("mcp:view").Feature);
            Assert.Null(new McpAccessGate("mcp.server", "  ").Permission);
        }

        [Fact]
        public void A_descriptor_with_NO_gate_cannot_be_constructed()
        {
            // the predecessor platform: Requires was optional, defaulted to empty, and empty meant invokable by every authenticated principal.
            var noResourceGate = Assert.Throws<ArgumentException>(() => new McpResourceDescriptor("stomme://site", "Site", "", null, Array.Empty<McpAccessGate>()));
            var noToolGate = Assert.Throws<ArgumentException>(() => new McpToolDescriptor("list_things", "", "{}", Array.Empty<McpAccessGate>(), McpToolApproval.Auto));
            _output.WriteLine(noResourceGate.Message);
            _output.WriteLine(noToolGate.Message);

            Assert.Throws<ArgumentException>(() => new McpResourceDescriptor("stomme://site", "Site", "", null, null!));
            Assert.Throws<ArgumentException>(() => new McpToolDescriptor("list_things", "", "{}", null!, McpToolApproval.Auto));
            Assert.Throws<ArgumentException>(() => new McpToolDescriptor("list_things", "", "{}", new McpAccessGate[] { null! }, McpToolApproval.Auto));
        }

        [Fact]
        public void The_gates_are_COPIED_so_a_contributor_cannot_empty_them_afterwards()
        {
            var mutable = new List<McpAccessGate> { McpAccessGate.ForPermission("mcp:view") };
            var descriptor = new McpToolDescriptor("list_things", "", "{}", mutable, McpToolApproval.Auto);

            mutable.Clear();

            Assert.Single(descriptor.Requires);
            Assert.False(descriptor.Requires is List<McpAccessGate>, "the descriptor handed back a list a caller could cast and clear");
        }

        [Fact]
        public void No_public_constructor_in_the_seam_makes_the_gates_optional()
        {
            // The structural half: nobody can later add "= null" to Requires without this failing.
            foreach (var type in new[] { typeof(McpResourceDescriptor), typeof(McpToolDescriptor) })
            {
                foreach (var constructor in type.GetConstructors())
                {
                    var requires = constructor.GetParameters().Single(p => p.Name == "requires");
                    Assert.False(requires.HasDefaultValue, $"{type.Name}: 'requires' has a default value");
                    var approval = constructor.GetParameters().SingleOrDefault(p => p.Name == "approval");
                    Assert.False(approval?.HasDefaultValue ?? false, $"{type.Name}: 'approval' has a default value");
                }
            }
        }

        // ---- approval ------------------------------------------------------------------------------------------------

        [Fact]
        public void The_approval_nobody_set_is_DENY()
        {
            Assert.Equal(McpToolApproval.Deny, default(McpToolApproval));
            Assert.Equal(0, (int)McpToolApproval.Deny);
            Assert.Throws<ArgumentOutOfRangeException>(() => new McpToolDescriptor("list_things", "", "{}", Gated, (McpToolApproval)99));
        }

        [Theory]
        [InlineData("create_page")]
        [InlineData("update_page")]
        [InlineData("delete_page")]
        [InlineData("execute_pipeline")]
        [InlineData("run_job")]
        [InlineData("export_data")]
        [InlineData("send_mail")]
        public void A_tool_whose_name_says_it_CHANGES_something_may_not_be_Auto(string name)
        {
            var refused = Assert.Throws<ArgumentException>(() => new McpToolDescriptor(name, "", "{}", Gated, McpToolApproval.Auto));
            _output.WriteLine(refused.Message);

            // ...and may be Confirm or Deny.
            Assert.Equal(McpToolApproval.Confirm, new McpToolDescriptor(name, "", "{}", Gated, McpToolApproval.Confirm).Approval);
            Assert.Equal(McpToolApproval.Deny, new McpToolDescriptor(name, "", "{}", Gated, McpToolApproval.Deny).Approval);
        }

        [Theory]
        [InlineData("list_pages")]
        [InlineData("get_site")]
        [InlineData("executed_report")]   // not the prefix "execute_"
        [InlineData("runner.status")]
        public void A_reading_tool_may_be_Auto(string name)
        {
            Assert.Equal(McpToolApproval.Auto, new McpToolDescriptor(name, "", "{}", Gated, McpToolApproval.Auto).Approval);
        }

        [Theory]
        [InlineData("Execute_Pipeline")]    // the casing that would walk past a prefix test
        [InlineData("EXPORT_DATA")]
        // A surviving mutant found these: every case above STARTS upper-case, so the first-character rule caught them
        // and the per-character rule was exercised by nothing. This one starts lower-case, is not the "execute_" prefix,
        // and would therefore have been allowed to be Auto.
        [InlineData("eXecute_pipeline")]
        [InlineData("list_Things")]
        [InlineData("lïst_things")]          // a letter, but not an ASCII one
        [InlineData("list_things​")]    // a zero-width space: two names that look like one
        [InlineData(" export_data")]
        [InlineData("export data")]
        [InlineData("_private")]
        [InlineData("9lives")]
        [InlineData("")]
        [InlineData("tool/with/slash")]
        public void A_non_canonical_tool_name_is_REFUSED_never_rewritten(string name)
        {
            Assert.False(McpToolNames.IsCanonical(name));
            Assert.Throws<ArgumentException>(() => new McpToolDescriptor(name, "", "{}", Gated, McpToolApproval.Deny));
        }

        [Fact]
        public void A_tool_name_is_at_most_64_characters()
        {
            Assert.True(McpToolNames.IsCanonical(new string('a', 64)));
            Assert.False(McpToolNames.IsCanonical(new string('a', 65)));
        }

        // ---- descriptors ---------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData("site")]
        [InlineData("://site")]
        [InlineData(" stomme://site")]
        public void A_resource_URI_needs_a_scheme(string uri)
        {
            Assert.Throws<ArgumentException>(() => new McpResourceDescriptor(uri, "Site", "", null, Gated));
        }

        [Fact]
        public void A_descriptor_fills_in_what_may_be_absent_and_knows_a_template()
        {
            var plain = new McpResourceDescriptor("stomme://site", "Site", null!, " ", Gated);
            Assert.Equal(string.Empty, plain.Description);
            Assert.Null(plain.MimeType);
            Assert.False(plain.IsTemplate());
            Assert.True(new McpResourceDescriptor("stomme://pages/{pageId}", "Page", "", "application/json", Gated).IsTemplate());

            Assert.Equal("{\"type\":\"object\"}", new McpToolDescriptor("list_things", "", " ", Gated, McpToolApproval.Auto).ParametersJsonSchema);
            Assert.Equal("text/plain", new McpResourceContent("stomme://site", "", "x").MimeType);
        }

        // ---- the URI template ----------------------------------------------------------------------------------------

        [Fact]
        public void A_template_captures_whole_segments_and_compares_literals_case_insensitively()
        {
            var match = McpUriTemplate.Match("stomme://pages/{pageId}/modules/{moduleId}", "Stomme://Pages/42/modules/7");

            Assert.NotNull(match);
            Assert.Equal("42", match!["pageId"]);
            Assert.Equal("7", match["MODULEID"]);
            Assert.Equal(new[] { "pageId", "moduleId" }, McpUriTemplate.Parameters("stomme://pages/{pageId}/modules/{moduleId}"));

            Assert.Empty(McpUriTemplate.Match("stomme://site", "stomme://site")!);
            Assert.Empty(McpUriTemplate.Parameters("stomme://site"));
        }

        [Theory]
        [InlineData("stomme://pages/{id}", "stomme://pages/")]              // an empty value
        [InlineData("stomme://pages/{id}", "stomme://pages/1/extra")]       // more segments
        [InlineData("stomme://pages/{id}", "stomme://pages")]               // fewer
        [InlineData("stomme://pages/{id}", "stomme://posts/1")]             // another literal
        [InlineData("stomme://files/{name}", "stomme://files/..")]          // walks out of whatever the reader joins it to
        [InlineData("stomme://files/{name}", "stomme://files/.")]
        [InlineData("stomme://x/{id}/y/{id}", "stomme://x/1/y/2")]          // one name, two values
        [InlineData("", "stomme://site")]
        [InlineData("stomme://site", null)]
        public void A_template_does_not_match(string template, string? uri)
        {
            Assert.Null(McpUriTemplate.Match(template, uri));
        }

        [Fact]
        public void A_captured_value_is_returned_EXACTLY_as_sent_never_decoded()
        {
            // A matcher that decoded %2F would hand the reader a value containing the separator it was promised it could not.
            Assert.Equal("a%2Fb", McpUriTemplate.Match("stomme://files/{name}", "stomme://files/a%2Fb")!["name"]);
        }

        // ---- the seam needs nothing but Stomme.Shared ----------------------------------------------------------------

        [Fact]
        public void A_contributor_compiles_against_Stomme_Shared_and_the_BCL_alone()
        {
            const string contributor = """
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;
                using Stomme.Mcp;

                public sealed class PackagedContributor : IMcpContributor, IMcpResourceReader, IMcpToolInvoker
                {
                    private static readonly McpAccessGate[] Gate = { McpAccessGate.ForPermission("packaged:view") };

                    public string ContributorId => "packaged";
                    public IReadOnlyCollection<McpResourceDescriptor> Resources { get; } =
                        new[] { new McpResourceDescriptor("packaged://things/{id}", "Thing", "One thing.", "application/json", Gate) };
                    public IReadOnlyCollection<McpToolDescriptor> Tools { get; } =
                        new[] { new McpToolDescriptor("list_things", "Lists things.", "{}", Gate, McpToolApproval.Auto) };
                    public IMcpResourceReader? ReaderFor(string uriTemplate) => this;
                    public IMcpToolInvoker? InvokerFor(string toolName) => this;
                    public Task<McpResourceContent?> ReadAsync(string uri, IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default) =>
                        Task.FromResult<McpResourceContent?>(new McpResourceContent(uri, "application/json", "{}"));
                    public Task<McpToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
                        Task.FromResult(new McpToolResult("[]"));
                }
                """;

            var errors = Compile(contributor, withTheServer: false);
            _output.WriteLine(errors.Count == 0 ? "compiled clean" : string.Join(Environment.NewLine, errors));
            Assert.Empty(errors);

            // The other direction: the same check DOES refuse a contributor that reaches into the server...
            const string reaching = "public sealed class Reaching { public object Guard = typeof(Stomme.Infrastructure.HostConfiguration); }";
            Assert.NotEmpty(Compile(reaching, withTheServer: false));
            // ...and for the reason claimed.
            Assert.Empty(Compile(reaching, withTheServer: true));
        }

        [Fact]
        public void The_seam_names_no_type_outside_Stomme_Shared_and_the_BCL()
        {
            var shared = typeof(IMcpContributor).Assembly;
            var seam = shared.GetTypes().Where(type => type.Namespace == "Stomme.Mcp" && type.IsPublic).ToList();
            Assert.True(seam.Count >= 10, $"found only {seam.Count} public types in Stomme.Mcp - the sweep is looking at nothing");

            const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var foreign = seam
                .SelectMany(type => type.GetMembers(all).SelectMany(Named))
                .SelectMany(Flatten)
                .Where(type => type.Assembly != shared && type.Assembly != typeof(object).Assembly && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
                .Select(type => type.FullName)
                .Distinct()
                .ToList();

            Assert.Empty(foreign);
        }

        private static IEnumerable<Type> Named(MemberInfo member) => member switch
        {
            MethodBase method => method.GetParameters().Select(p => p.ParameterType).Concat(method is MethodInfo info ? new[] { info.ReturnType } : Array.Empty<Type>()),
            PropertyInfo property => new[] { property.PropertyType },
            FieldInfo field => new[] { field.FieldType },
            _ => Array.Empty<Type>(),
        };

        private static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.HasElementType)
            {
                foreach (var inner in Flatten(type.GetElementType()!))
                {
                    yield return inner;
                }
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
                {
                    yield return argument;
                }
            }
        }

        private static List<string> Compile(string source, bool withTheServer)
        {
            var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = Directory.GetFiles(frameworkDirectory, "*.dll")
                .Where(IsManaged)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();

            references.Add(MetadataReference.CreateFromFile(typeof(IMcpContributor).Assembly.Location));
            if (withTheServer)
            {
                references.Add(MetadataReference.CreateFromFile(typeof(Stomme.Infrastructure.HostConfiguration).Assembly.Location));
            }

            return CSharpCompilation.Create(
                    "McpSeamProbe",
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
