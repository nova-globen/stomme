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

namespace NovaGloben.Module.KnowledgeBase.Drive
{
    /// <summary>
    /// BL-86 increment 7 (the Blogs example's BL-13 proof, carried by the exemplar now): the assemblies a WebAssembly site
    /// streams to the BROWSER - the Client and the Shared - reference no server-only assembly, read from the PACKED module's
    /// metadata. A browser assembly that references the server host makes <c>Assembly.GetTypes()</c> throw in the browser and
    /// the module's services silently under-register. The detector is validated on the Server assembly, which must fire.
    /// </summary>
    [Collection("KnowledgeBaseDrive")]
    public sealed class ClientAssemblyReferenceTests(ITestOutputHelper output)
    {
        // Never referenced by a browser-bound module assembly: the server host, the persistence stack, EF and the drivers.
        private static readonly string[] ServerOnlyPrefixes =
        {
            "Stomme.Server",
            "Stomme.Persistence",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Raven",
        };

        [Theory]
        [InlineData("NovaGloben.Module.KnowledgeBase.Client.Stomme.dll")]
        [InlineData("NovaGloben.Module.KnowledgeBase.Shared.Stomme.dll")]
        public void A_browser_bound_assembly_references_no_server_only_assembly(string dllName)
        {
            var references = ReadReferencesFromPackedNupkg(dllName);
            output.WriteLine($"{dllName} references: {string.Join(", ", references)}");

            // Vacuity floor: an empty parse passes the ban trivially.
            Assert.NotEmpty(references);
            if (dllName.Contains(".Client.", StringComparison.Ordinal))
            {
                Assert.Contains("NovaGloben.Module.KnowledgeBase.Shared.Stomme", references);
            }

            var hits = Detect(references);
            Assert.True(hits.Count == 0, $"{dllName} is streamed to the BROWSER but references server-only assemblies: {string.Join(", ", hits)}");
        }

        [Fact]
        public void The_detector_fires_on_the_server_assembly_the_known_dirty_control()
        {
            var hits = Detect(ReadReferencesFromPackedNupkg("NovaGloben.Module.KnowledgeBase.Server.Stomme.dll"));
            Assert.True(hits.Count > 0, "The detector found NOTHING on the Server assembly, which references Stomme.Server by construction - do not trust its green.");
            Assert.Contains("Stomme.Server", hits);
        }

        private static List<string> Detect(IReadOnlyCollection<string> references) =>
            references.Where(name => ServerOnlyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))).ToList();

        private static List<string> ReadReferencesFromPackedNupkg(string dllName)
        {
            var binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;
            var nupkg = Directory.GetFiles(binPath, "NovaGloben.Module.KnowledgeBase.*.nupkg").FirstOrDefault()
                ?? throw new FileNotFoundException("The packed module is not beside the harness. Run examples/Modules/KnowledgeBase/pack-knowledgebase.ps1 first.");
            using var archive = ZipFile.OpenRead(nupkg);
            var entry = archive.Entries.FirstOrDefault(candidate => candidate.FullName.Equals($"lib/net10.0/{dllName}", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"{dllName} is not packed under lib/net10.0 in {nupkg}.");
            using var memory = new MemoryStream();
            using (var stream = entry.Open())
            {
                stream.CopyTo(memory);
            }

            memory.Position = 0;
            using var pe = new PEReader(memory);
            var metadata = pe.GetMetadataReader();
            return metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToList();
        }
    }
}
