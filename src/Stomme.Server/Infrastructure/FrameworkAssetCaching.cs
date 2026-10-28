using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Decides the <c>Cache-Control</c> for a Blazor <c>/_framework/</c> asset and a package's <c>/_content/</c> asset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes (measured 2026-07-26 on the UAT deployment).</b> Stomme served
    /// <c>/_framework/*</c> with NO <c>Cache-Control</c> at all, so every CDN and proxy invented its own —
    /// Cloudflare applied its default <c>.js</c> TTL of <c>max-age=14400</c>. Blazor fingerprints its assemblies
    /// (<c>Stomme.Client.&lt;hash&gt;.wasm</c>) but NOT its loaders (<c>blazor.web.js</c>, <c>dotnet.js</c>),
    /// which keep stable names. After a redeploy the edge therefore kept serving the OLD loader, which named a
    /// fingerprinted assembly that no longer existed: 404, ~23 console errors, and the WASM app never booted —
    /// for four hours, until the cache expired.
    /// </para>
    /// <para>
    /// The fingerprinted assets were never the problem; their URL changes every build. The unversioned loader
    /// that NAMES them is. So the two classes need opposite policies, and getting them from the origin fixes
    /// every CDN and browser at once — no CDN configuration, and no version string anyone has to remember to
    /// bump. (A <c>?v=</c> on the <c>&lt;script src="_framework/blazor.web.js"&gt;</c> tag would only half-work:
    /// <c>blazor.web.js</c> fetches <c>dotnet.js</c> itself, the query does not propagate, and <c>dotnet.js</c> is
    /// exactly the file embedding the asset names.)
    /// </para>
    /// <para>
    /// <b>Every ambiguity resolves toward <see cref="NoCache"/>.</b> Mislabelling a fingerprinted asset as
    /// no-cache costs one revalidation; mislabelling a LOADER as immutable bakes a broken app into caches for a
    /// year. So a file is treated as immutable only when it positively looks fingerprinted, and the known stable
    /// loader names are rejected first regardless — <c>blazor.webassembly.js</c> is the reason both checks exist,
    /// since its <c>webassembly</c> segment is an 11-character lowercase run that a length-only pattern would
    /// happily mistake for a hash.
    /// </para>
    /// <para>
    /// <b>The same class under <c>/_content/</c> (BL-192, measured 2026-10-02).</b> A package's static web assets keep their
    /// names across the package's versions: FluentUI's <c>Microsoft.FluentUI.AspNetCore.Components.lib.module.js</c> is one
    /// URL under 4.14.3 and 5.0.0. Served with no <c>Cache-Control</c>, a browser that had loaded the 4.x file kept running it
    /// after the host moved to 5.0.0 - the origin answered 457,618 bytes of 5.x, the browser used its 395,429 bytes of 4.x -
    /// and every FluentUI 5 control failed to start. A framework upgrade must not depend on the visitor's cache expiring, so
    /// <c>/_content/</c> takes the same per-file policy: immutable only when fingerprinted, otherwise revalidated.
    /// </para>
    /// </remarks>
    internal static class FrameworkAssetCaching
    {
        /// <summary>The path prefixes this policy governs. Everything else is left to the <c>Caching:Cache-Control</c> knob.</summary>
        public const string FrameworkPath = "/_framework/";

        /// <summary>Packages' static web assets (BL-192): stable names whose content changes with the package version.</summary>
        public const string ContentPath = "/_content/";

        /// <summary>A year, and never revalidated — safe only because the URL itself changes when the content does.</summary>
        public const string Immutable = "public, max-age=31536000, immutable";

        /// <summary>
        /// Store it, but revalidate every time. Not <c>no-store</c>: the responses carry an ETag, so a
        /// revalidation is a cheap 304 rather than a full re-download.
        /// </summary>
        public const string NoCache = "no-cache";

        /// <summary>
        /// Loaders that keep a STABLE name across builds. These must never be cached immutably, whatever they
        /// look like. Compared case-insensitively, after any compression suffix is stripped.
        /// </summary>
        private static readonly HashSet<string> StableLoaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "blazor.web.js",
            "blazor.server.js",
            "blazor.webassembly.js",
            "blazor.boot.json",
            "dotnet.js",
            "service-worker.js",
            "service-worker-assets.js"
        };

        /// <summary>Pre-compressed variants are served under the same fingerprinting rules as their source.</summary>
        private static readonly string[] CompressionSuffixes = { ".br", ".gz" };

        /// <summary>
        /// A Blazor fingerprint: a run of lowercase letters/digits sitting between two dots, immediately before
        /// the extension. The digit is REQUIRED — it is what separates a base36 hash from an English word such as
        /// <c>webassembly</c>. A hash that happens to be all letters (~4% of them) simply falls through to
        /// <see cref="NoCache"/>, which costs a revalidation and nothing else.
        /// </summary>
        private static readonly Regex Fingerprint =
            new(@"\.(?=[a-z0-9]{8,16}\.)[a-z]*[0-9][a-z0-9]*\.[A-Za-z0-9]+$", RegexOptions.Compiled);

        /// <summary>
        /// The <c>Cache-Control</c> for the given request path, or <c>null</c> when this policy does not apply
        /// (i.e. the path is not a framework asset) and the caller should fall back to its own configuration.
        /// </summary>
        public static string Resolve(string path)
        {
            // A Blazor JS initializer keeps its "<assembly>.lib.module.js" name wherever it is served from - Stomme's own sits
            // at the root - and BL-192 made Stomme's the one that starts FluentUI 5, so a stale copy is a broken page.
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".lib.module.js", StringComparison.OrdinalIgnoreCase))
            {
                return NoCache;
            }

            if (string.IsNullOrEmpty(path) ||
                (path.IndexOf(FrameworkPath, StringComparison.OrdinalIgnoreCase) < 0 &&
                 path.IndexOf(ContentPath, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return null;
            }

            var fileName = path.Substring(path.LastIndexOf('/') + 1);
            foreach (var suffix in CompressionSuffixes)
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = fileName.Substring(0, fileName.Length - suffix.Length);
                    break;
                }
            }

            if (fileName.Length == 0 || StableLoaders.Contains(fileName))
            {
                return NoCache;
            }

            return Fingerprint.IsMatch(fileName) ? Immutable : NoCache;
        }
    }
}
