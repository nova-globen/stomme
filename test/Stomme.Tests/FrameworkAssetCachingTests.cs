using AwesomeAssertions;
using Stomme.Infrastructure;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The two <c>Cache-Control</c> classes for Blazor's <c>/_framework/</c> assets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on the UAT deployment: Stomme sent NO <c>Cache-Control</c> for <c>/_framework/*</c>, so
    /// Cloudflare applied its default <c>max-age=14400</c> to the STABLE-named loaders. After a redeploy the edge
    /// kept serving the old <c>blazor.web.js</c>, which named a <c>Stomme.Client.&lt;old-hash&gt;.wasm</c> that
    /// no longer existed — 404, and the app never booted, for four hours.
    /// </para>
    /// <para>
    /// The asymmetry is the whole point, so it is asserted rather than assumed: a fingerprinted asset marked
    /// no-cache costs one revalidation, while a LOADER marked immutable bakes a broken app into caches for a
    /// year. Every ambiguous case below must therefore land on <c>no-cache</c>.
    /// </para>
    /// </remarks>
    public class FrameworkAssetCachingTests
    {
        // Real filenames taken from the deployed container's /app/wwwroot/_framework.
        [Theory]
        [InlineData("/_framework/Stomme.Client.fnusv4zmu6.wasm")]
        [InlineData("/_framework/Stomme.Shared.zlf522redv.wasm")]
        [InlineData("/_framework/dotnet.native.puryxhmhq9.js")]
        [InlineData("/_framework/dotnet.runtime.web2r9gqbh.js")]
        [InlineData("/_framework/dotnet.native.u31nt9bth6.wasm")]
        [InlineData("/_framework/Grpc.Net.Client.Web.7vb7d27ssg.wasm")]
        public void A_fingerprinted_asset_is_cached_immutably(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().Be(FrameworkAssetCaching.Immutable);
        }

        /// <summary>The pre-compressed twins are the same content under the same fingerprint.</summary>
        [Theory]
        [InlineData("/_framework/Stomme.Client.fnusv4zmu6.wasm.br")]
        [InlineData("/_framework/Stomme.Client.fnusv4zmu6.wasm.gz")]
        [InlineData("/_framework/dotnet.runtime.web2r9gqbh.js.br")]
        public void A_compressed_fingerprinted_asset_is_cached_immutably(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().Be(FrameworkAssetCaching.Immutable);
        }

        /// <summary>
        /// The files that keep a stable name across builds — the ones that actually broke. Note
        /// <c>blazor.webassembly.js</c>: its <c>webassembly</c> segment is an 11-character lowercase run, which a
        /// length-only fingerprint pattern would mistake for a hash. It is the reason the policy checks an
        /// explicit loader list AND requires a digit in the hash.
        /// </summary>
        [Theory]
        [InlineData("/_framework/blazor.web.js")]
        [InlineData("/_framework/blazor.server.js")]
        [InlineData("/_framework/blazor.webassembly.js")]
        [InlineData("/_framework/dotnet.js")]
        [InlineData("/_framework/blazor.boot.json")]
        [InlineData("/_framework/blazor.web.js.br")]
        [InlineData("/_FRAMEWORK/BLAZOR.WEB.JS")]
        public void A_stable_named_loader_is_never_cached_immutably(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().Be(FrameworkAssetCaching.NoCache);
        }

        /// <summary>
        /// The fail-safe direction: anything that does not positively look fingerprinted falls to no-cache,
        /// including a file this policy has never heard of. A future .NET adding a new stable-named loader is
        /// then merely slower, not broken.
        /// </summary>
        [Theory]
        [InlineData("/_framework/some-future-loader.js")]
        [InlineData("/_framework/dotnet.timezones.blat")]
        [InlineData("/_framework/icudt_CJK.dat")]
        [InlineData("/_framework/Stomme.Client.wasm")]
        [InlineData("/_framework/")]
        public void An_unrecognised_framework_file_falls_back_to_no_cache(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().Be(FrameworkAssetCaching.NoCache);
        }

        /// <summary>
        /// An all-letter segment is NOT treated as a hash, because that is indistinguishable from a word. The
        /// cost is a revalidation on the ~4% of base36 hashes that contain no digit — deliberately the cheap
        /// side of the trade.
        /// </summary>
        [Fact]
        public void An_all_letter_segment_is_not_mistaken_for_a_fingerprint()
        {
            FrameworkAssetCaching.Resolve("/_framework/Some.libraryname.js").Should().Be(FrameworkAssetCaching.NoCache);
        }

        /// <summary>
        /// Outside <c>/_framework/</c> and <c>/_content/</c> the policy declines to answer, so the site-wide
        /// <c>Caching:Cache-Control</c> knob keeps governing exactly as before.
        /// </summary>
        [Theory]
        [InlineData("/css/app.css")]
        [InlineData("/Modules/Blogs/module.js")]
        [InlineData("/")]
        [InlineData("")]
        [InlineData(null)]
        public void A_non_framework_path_is_left_to_the_site_wide_setting(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().BeNull();
        }

        /// <summary>
        /// A package's static web asset keeps its name across the package's versions, so it is revalidated: measured,
        /// a browser kept FluentUI 4.14.3's module after the host moved to 5.0.0 under the same URL, and no FluentUI 5 control
        /// started. A fingerprinted one is still immutable.
        /// </summary>
        [Theory]
        [InlineData("/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js")]
        [InlineData("/_content/Stomme.Theme.Aurora/Theme.css")]
        [InlineData("/tenant-a/_content/Stomme.Theme.Slate/Theme.css")]
        public void A_package_asset_is_revalidated_so_an_upgrade_reaches_the_browser(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().Be(FrameworkAssetCaching.NoCache);
        }

        /// <summary>A Blazor JS initializer keeps its name wherever it is served from; Stomme's own starts FluentUI 5.</summary>
        [Theory]
        [InlineData("/Stomme.Server.lib.module.js")]
        [InlineData("/tenant-a/Stomme.Server.lib.module.js")]
        [InlineData("/_content/Some.Package/Some.Package.lib.module.js")]
        public void A_js_initializer_is_revalidated_wherever_it_is_served_from(string path)
        {
            FrameworkAssetCaching.Resolve(path).Should().Be(FrameworkAssetCaching.NoCache);
        }

        [Fact]
        public void A_fingerprinted_package_asset_is_cached_immutably()
        {
            FrameworkAssetCaching.Resolve("/_content/Some.Package/app.k3j4h5g6f7.js").Should().Be(FrameworkAssetCaching.Immutable);
        }

        /// <summary>An alias-prefixed request still resolves — the tenant path segment precedes <c>/_framework/</c>.</summary>
        [Fact]
        public void An_alias_prefixed_framework_path_still_resolves()
        {
            FrameworkAssetCaching.Resolve("/tenant-a/_framework/Stomme.Client.fnusv4zmu6.wasm")
                .Should().Be(FrameworkAssetCaching.Immutable);
            FrameworkAssetCaching.Resolve("/tenant-a/_framework/blazor.web.js")
                .Should().Be(FrameworkAssetCaching.NoCache);
        }
    }
}
