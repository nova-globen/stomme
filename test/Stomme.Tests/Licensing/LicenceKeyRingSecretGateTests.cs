using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Stomme.Infrastructure.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// The compiled-in ring holds PUBLIC keys only, the production and
    /// test rings share nothing, and no private-key PEM armour exists in the code trees or in any loaded framework
    /// assembly's embedded resources. The scanner is validated in BOTH directions — 0 on the real tree AND &gt;0 on a
    /// planted string and a planted file fed through the SAME directory walker.
    /// </summary>
    /// <remarks>
    /// The scan matches the FULL armour, dashes included — never the bare words — because the spec itself
    /// (<c>docs/backlog.md</c>) legitimately names the bare token; a bare-token scan is red at birth and any docs carve-out is
    /// the path carve-out item 12 forbids. <c>docs/</c> and <c>test/</c> are therefore outside the walked roots, and
    /// there are NO path carve-outs inside them (only build output: <c>bin</c>/<c>obj</c>).
    /// </remarks>
    public sealed class LicenceKeyRingSecretGateTests
    {
        private static readonly string[] ScannedRoots = { "src", "tools", "setup", "examples", "scripts", ".github" };

        private readonly ITestOutputHelper _output;

        public LicenceKeyRingSecretGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Every_production_ring_entry_is_a_P256_public_key_with_no_private_half()
        {
            Assert.True(LicenceKeyRing.Production.Count >= 1,
                "The production licence ring is EMPTY — the gate would pass vacuously and no SLK1 key could ever verify.");

            foreach (var (kid, spki) in LicenceKeyRing.Production)
            {
                Assert.False(string.IsNullOrWhiteSpace(kid));
                Assert.DoesNotContain("test", kid, StringComparison.OrdinalIgnoreCase);

                using var key = LicenceKeyRing.LoadPublic(spki);
                Assert.Equal(256, key.KeySize);
                Assert.ThrowsAny<CryptographicException>(() => key.ExportParameters(includePrivateParameters: true));
                _output.WriteLine($"production kid '{kid}': P-256 SPKI, {spki.Length} bytes, no private half");
            }
        }

        [Fact]
        public void LoadPublic_rejects_a_curve_other_than_P256()
        {
            using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            Assert.ThrowsAny<CryptographicException>(() => LicenceKeyRing.LoadPublic(p384.ExportSubjectPublicKeyInfo()));

            using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trailing = p256.ExportSubjectPublicKeyInfo().Concat(new byte[] { 0 }).ToArray();
            Assert.ThrowsAny<CryptographicException>(() => LicenceKeyRing.LoadPublic(trailing));
        }

        [Fact]
        public void The_production_and_test_rings_share_no_kid_and_no_key_bytes()
        {
            using var issuer = new TestLicenceIssuer();

            Assert.Empty(SharedMaterial(LicenceKeyRing.Production, issuer.Ring));
        }

        [Fact]
        public void The_shared_material_check_is_not_vacuous()
        {
            // Direction 2 of the ring-disjointness gate: a ring compared with itself (and a ring re-using a production
            // key under a different kid) MUST be reported, or the gate above could never fail.
            Assert.NotEmpty(SharedMaterial(LicenceKeyRing.Production, LicenceKeyRing.Production));

            var first = LicenceKeyRing.Production.First();
            var reused = new Dictionary<string, byte[]> { ["test-ephemeral-reuse"] = first.Value.ToArray() };
            Assert.NotEmpty(SharedMaterial(LicenceKeyRing.Production, reused));
        }

        [Fact]
        public void The_code_trees_and_embedded_resources_carry_no_private_key_armour()
        {
            var repoRoot = FindRepositoryRoot();
            var hits = new List<string>();
            var filesScanned = 0;

            foreach (var name in ScannedRoots)
            {
                var path = Path.Combine(repoRoot, name);
                if (Directory.Exists(path))
                {
                    hits.AddRange(PrivateKeyMaterialScanner.ScanDirectory(path, ref filesScanned));
                }
            }

            hits.AddRange(PrivateKeyMaterialScanner.ScanFiles(Directory.EnumerateFiles(repoRoot), ref filesScanned));

            var resourcesScanned = 0;
            foreach (var assembly in LoadedFrameworkAssemblies())
            {
                foreach (var resource in assembly.GetManifestResourceNames())
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null)
                    {
                        continue;
                    }

                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    resourcesScanned++;
                    if (PrivateKeyMaterialScanner.FindPrivateKeyMaterial(Encoding.Latin1.GetString(buffer.ToArray())) > 0)
                    {
                        hits.Add($"{assembly.GetName().Name}!{resource}");
                    }
                }
            }

            _output.WriteLine($"scanned {filesScanned} text files and {resourcesScanned} embedded resources");
            Assert.True(filesScanned > 1000, $"The walker scanned only {filesScanned} files — the scan roots were not found.");
            Assert.True(LoadedFrameworkAssemblies().Count >= 3, "The framework assemblies were not loaded, so no resource was scanned.");
            Assert.True(hits.Count == 0, "Private-key PEM armour found:\n" + string.Join("\n", hits));
        }

        [Fact]
        public void The_scanner_fires_on_planted_armour_in_a_string_and_in_a_walked_file()
        {
            // Built at runtime so this source file never carries the armour itself.
            var armour = "-----BEGIN " + "PRIVATE KEY-----";
            Assert.Equal(1, PrivateKeyMaterialScanner.FindPrivateKeyMaterial("header\n" + armour + "\nMIGHAgEAMBMG\n"));
            Assert.Equal(1, PrivateKeyMaterialScanner.FindPrivateKeyMaterial("-----BEGIN " + "EC PRIVATE KEY-----"));
            Assert.Equal(1, PrivateKeyMaterialScanner.FindPrivateKeyMaterial("-----BEGIN " + "ENCRYPTED PRIVATE KEY-----"));
            Assert.Equal(1, PrivateKeyMaterialScanner.FindPrivateKeyMaterial("-----BEGIN " + "RSA PRIVATE KEY-----"));
            Assert.Equal(0, PrivateKeyMaterialScanner.FindPrivateKeyMaterial("the words PRIVATE KEY in prose"));
            Assert.Equal(0, PrivateKeyMaterialScanner.FindPrivateKeyMaterial("-----BEGIN " + "PUBLIC KEY-----"));

            var temp = Path.Combine(Path.GetTempPath(), "stomme-secret-gate-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(temp, "deep", "er"));
                File.WriteAllText(Path.Combine(temp, "clean.cs"), "namespace Clean { }");
                File.WriteAllText(Path.Combine(temp, "deep", "er", "signing.txt"), "x\n" + armour + "\nMIGHAgEAMBMG\n");

                var scanned = 0;
                var hits = PrivateKeyMaterialScanner.ScanDirectory(temp, ref scanned);

                Assert.Equal(2, scanned);
                var hit = Assert.Single(hits);
                Assert.EndsWith("signing.txt", hit);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        private static List<string> SharedMaterial(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b)
        {
            var shared = new List<string>();
            foreach (var kid in a.Keys.Intersect(b.Keys, StringComparer.Ordinal))
            {
                shared.Add("kid " + kid);
            }

            foreach (var left in a)
            {
                foreach (var right in b)
                {
                    if (left.Value.AsSpan().SequenceEqual(right.Value))
                    {
                        shared.Add($"key bytes {left.Key} == {right.Key}");
                    }
                }
            }

            return shared;
        }

        private static List<Assembly> LoadedFrameworkAssemblies()
        {
            _ = typeof(Stomme.Server.Program).Assembly;
            _ = typeof(Stomme.Modules.Admin.Users.ModuleInfo).Assembly;
            _ = typeof(Stomme.Models.Site).Assembly;

            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .Where(assembly =>
                {
                    var name = assembly.GetName().Name ?? string.Empty;
                    return name.StartsWith("Stomme", StringComparison.Ordinal)
                        && !name.Contains(".Tests", StringComparison.Ordinal)
                        && !name.EndsWith(".Tests", StringComparison.Ordinal);
                })
                .ToList();
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx) from " + AppContext.BaseDirectory);
            return directory!.FullName;
        }
    }

    /// <summary>The private-key armour scanner of <see cref="LicenceKeyRingSecretGateTests"/>.</summary>
    internal static class PrivateKeyMaterialScanner
    {
        private static readonly Regex Armour = new(
            "-----BEGIN (?:EC |ENCRYPTED |RSA )?PRIVATE KEY-----",
            RegexOptions.CultureInvariant);

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".nupkg", ".snupkg", ".zip", ".gz", ".png", ".jpg", ".jpeg", ".gif", ".ico",
            ".webp", ".bmp", ".woff", ".woff2", ".ttf", ".eot", ".otf", ".db", ".sqlite", ".mp4", ".mp3", ".pdf",
        };

        /// <summary>Pure: the number of private-key PEM armour lines in <paramref name="text"/>.</summary>
        public static int FindPrivateKeyMaterial(string text) => Armour.Matches(text).Count;

        /// <summary>Walks <paramref name="root"/> recursively, skipping only <c>bin</c>/<c>obj</c> build output.</summary>
        public static List<string> ScanDirectory(string root, ref int filesScanned)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                files.AddRange(Directory.EnumerateFiles(current));
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(child);
                    if (!string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(child);
                    }
                }
            }

            return ScanFiles(files, ref filesScanned);
        }

        public static List<string> ScanFiles(IEnumerable<string> files, ref int filesScanned)
        {
            var hits = new List<string>();
            foreach (var file in files)
            {
                if (BinaryExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(file);
                if (Array.IndexOf(bytes, (byte)0) >= 0 && !LooksLikeUtf16(bytes))
                {
                    continue; // binary content under a text-looking name
                }

                filesScanned++;
                var text = LooksLikeUtf16(bytes) ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
                if (FindPrivateKeyMaterial(text) > 0)
                {
                    hits.Add(file);
                }
            }

            return hits;
        }

        private static bool LooksLikeUtf16(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
    }
}