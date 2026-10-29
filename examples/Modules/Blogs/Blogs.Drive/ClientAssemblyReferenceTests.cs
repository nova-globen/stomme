using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace NovaGloben.Module.Blogs.Drive
{
    /// <summary>
    /// The WASM-download-safety REFERENCE check, run against the EXACT shipped artifact (the DLLs
    /// inside the packed nupkg, read with System.Reflection.Metadata — nothing is loaded). The browser only
    /// receives the Client + Shared assemblies, so neither may reference any server-only assembly: a single
    /// leaked reference reproduces the 1.0.0 failure mode, where resolving the module's types threw
    /// ReflectionTypeLoadException in the browser and Stomme.Client's Program.cs swallowed it, silently
    /// under-registering the module's services.
    /// <para>
    /// The detector is validated in BOTH directions (the instruments-have-lied rule): the SERVER assembly in
    /// the same nupkg MUST trip it (it references Stomme.Server by construction — its ServerStartup
    /// implements Stomme.Infrastructure.IServerStartup), so a scanner that reports 0 hits on the Server DLL
    /// is broken, not clean. (The retired 1.0.0 single-assembly DLL was the original known-dirty control:
    /// built from HEAD~ it references Stomme.Server, Stomme.SharedKernel and Stomme.Mediator — the same
    /// detector fired on it, recorded in the BL-13 report.)
    /// </para>
    /// </summary>
    [Collection("BlogsDrive")]
    public sealed class ClientAssemblyReferenceTests
    {
        // Assemblies that must NEVER be referenced by browser-bound module assemblies: the server host,
        // the persistence stack, EF and the database drivers.
        private static readonly string[] ServerOnlyPrefixes =
        {
            "Stomme.Server",
            "Stomme.Persistence",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Raven"
        };

        private readonly ITestOutputHelper _output;

        public ClientAssemblyReferenceTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("NovaGloben.Module.Blogs.Client.Stomme.dll")]
        [InlineData("NovaGloben.Module.Blogs.Shared.Stomme.dll")]
        public void Browser_bound_assembly_references_no_server_only_assembly(string dllName)
        {
            var references = ReadReferencesFromPackedNupkg(dllName);
            _output.WriteLine($"{dllName} references: {string.Join(", ", references)}");

            // Vacuity floor: an empty parse would pass the ban trivially — require the reference list to be
            // non-empty and (for the Client) to include the Shared assembly it links.
            Assert.NotEmpty(references);
            if (dllName.Contains(".Client.", StringComparison.Ordinal))
            {
                Assert.Contains("NovaGloben.Module.Blogs.Shared.Stomme", references);
            }

            var hits = Detect(references);
            Assert.True(hits.Count == 0,
                $"{dllName} is streamed to the BROWSER but references server-only assemblies: {string.Join(", ", hits)}");
        }

        [Fact]
        public void Detector_fires_on_the_server_assembly_the_known_dirty_control()
        {
            var references = ReadReferencesFromPackedNupkg("NovaGloben.Module.Blogs.Server.Stomme.dll");
            _output.WriteLine($"Server references: {string.Join(", ", references)}");

            var hits = Detect(references);
            Assert.True(hits.Count > 0,
                "The server-only reference detector found NOTHING on the Server assembly, which references " +
                "Stomme.Server by construction — the scanner is broken (0-on-dirty), do not trust its green.");
            Assert.Contains("Stomme.Server", hits);
        }

        private static List<string> Detect(IReadOnlyCollection<string> references) =>
            references
                .Where(name => ServerOnlyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                .ToList();

        /// <summary>
        /// Reads the referenced-assembly names of one DLL inside the packed module nupkg (staged beside the
        /// harness by the csproj), via PEReader/MetadataReader — the artifact the installer ships, not a
        /// loose build output, and no assembly loading involved.
        /// </summary>
        private static List<string> ReadReferencesFromPackedNupkg(string dllName)
        {
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            var nupkg = Directory.GetFiles(binPath, "NovaGloben.Module.Blogs.*.nupkg").FirstOrDefault()
                        ?? throw new FileNotFoundException(
                            "The packaged module nupkg was not found beside the harness. Run examples/Modules/Blogs/pack-blogs.ps1 first.");

            using var archive = ZipFile.OpenRead(nupkg);
            var entry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.Equals($"lib/net10.0/{dllName}", StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"{dllName} is not packed under lib/net10.0 in {nupkg}.");

            using var memory = new MemoryStream();
            using (var stream = entry.Open())
            {
                stream.CopyTo(memory);
            }
            memory.Position = 0;

            using var peReader = new PEReader(memory);
            var metadata = peReader.GetMetadataReader();
            return metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
