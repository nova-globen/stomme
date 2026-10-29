#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Tests.Fixtures.ExportTestModule;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The export contract is BCL types and <c>Stomme.Shared</c>, and nothing else.
    /// </summary>
    /// <remarks>
    /// Asserted by COMPILING the fixture source against exactly those references, rather than by scanning it for
    /// forbidden names: a scan knows the namespaces somebody thought of, a compiler knows every one. The same
    /// fixture is then put through the real registry and a real renderer, so "it compiles" is not the whole claim.
    /// </remarks>
    public class ExportContractDependencyTests
    {
        private readonly ITestOutputHelper _output;

        public ExportContractDependencyTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void A_source_compiles_against_Stomme_Shared_and_the_BCL_alone()
        {
            var errors = Compile(File.ReadAllText(FixtureFile()), withTheEngine: false);

            _output.WriteLine(errors.Count == 0 ? "compiled clean" : string.Join(Environment.NewLine, errors));
            Assert.Empty(errors);
        }

        [Fact]
        public void The_compile_check_DOES_refuse_a_source_that_reaches_into_the_engine()
        {
            const string planted = """
                using Stomme.Export;
                public sealed class Planted
                {
                    public object Registry = new Stomme.Modules.DataExport.Execution.ExportSourceRegistry(new IExportSource[0]);
                }
                """;

            // Refused for the reason claimed: the very same text compiles once the engine IS referenced.
            Assert.NotEmpty(Compile(planted, withTheEngine: false));
            Assert.Empty(Compile(planted, withTheEngine: true));
        }

        [Fact]
        public async Task The_fixture_is_accepted_by_the_real_registry_and_a_million_rows_render_through_a_real_renderer()
        {
            var registry = new ExportSourceRegistry(new IExportSource[] { new GeneratedRowsExportSource() });
            Assert.True(registry.TryGet(GeneratedRowsExportSource.SourceKey, out var source));

            var context = new ExportSourceContext(siteId: 3, requesterUserId: 9, "{\"rows\": 1000000}");
            Assert.Null(source.Validate(context));

            await using var document = await source.OpenAsync(context);
            var sink = new CountingSink();
            var outcome = await new CsvTabularRenderer().RenderAsync(document, sink, ExportRenderOptions.Default, new NoFormatter());

            _output.WriteLine($"rows={outcome.RowCount:N0} bytes={sink.Length:N0}");
            Assert.Equal(1_000_000, outcome.RowCount);
            Assert.True(sink.Length > 1_000_000);
        }

        [Theory]
        [InlineData("{\"rows\": -1}")]
        [InlineData("{\"rows\": 1000001}")]
        [InlineData("{\"rows\": 5, \"siteId\": 2}")]
        [InlineData("[5]")]
        [InlineData("not json")]
        public void The_fixture_rejects_what_it_does_not_understand_rather_than_ignoring_it(string arguments)
        {
            var rejection = new GeneratedRowsExportSource().Validate(new ExportSourceContext(1, 1, arguments));

            _output.WriteLine($"{arguments} -> {rejection}");
            Assert.False(string.IsNullOrEmpty(rejection));
        }

        [Fact]
        public void A_request_has_no_member_that_could_name_a_site_a_tenant_a_user_or_a_clock()
        {
            // The server resolves all four. A settable member here would be an invitation to trust it one day.
            Assert.Empty(SubjectMembers(typeof(ExportRequest)));

            // ...and the check can see one: the server-built context carries exactly those, and is found.
            var found = SubjectMembers(typeof(ExportSourceContext));
            Assert.Contains("SiteId", found);
            Assert.Contains("RequesterUserId", found);
        }

        private static List<string> SubjectMembers(Type type)
        {
            var forbidden = new[] { "Site", "Tenant", "User", "Requester", "Owner", "Clock", "Now", "Utc", "Date", "Time" };
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static List<string> Compile(string source, bool withTheEngine)
        {
            // The BCL: every assembly that ships in the shared framework directory, and nothing from this build.
            var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = Directory.GetFiles(frameworkDirectory, "*.dll")
                .Where(IsManaged)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();

            references.Add(MetadataReference.CreateFromFile(typeof(IExportSource).Assembly.Location));
            if (withTheEngine)
            {
                references.Add(MetadataReference.CreateFromFile(typeof(CsvTabularRenderer).Assembly.Location));
            }

            var compilation = CSharpCompilation.Create(
                "ExportContractProbe",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return compilation.GetDiagnostics()
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

        private static string FixtureFile()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "test", "Stomme.Tests", "Fixtures", "ExportTestModule", "GeneratedRowsExportSource.cs");
        }

        private sealed class NoFormatter : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => null;
        }

        /// <summary>Counts and discards: a million rows must not be held to be counted.</summary>
        private sealed class CountingSink : Stream
        {
            private long _length;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => _length;

            public override long Position { get => _length; set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => _length += count;
        }
    }
}
