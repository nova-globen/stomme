using System;
using System.Linq;
using System.Reflection;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    // Found by the 2026-08-09 admin interaction pass: the SHIPPED v10.2.2 self-reported 10.2.1 on
    // admin/system, because Constants.Version is a hand-maintained string the release ritual forgot.
    // That string is not cosmetic: the installer stamps InstallationVersion from it, the marketplace
    // framework gate compares nuspec Stomme.Framework minimums against it (a package requiring the
    // REAL shipped version silently refuses to install), and admin/update offers the current version as
    // an "upgrade". These pins make the drift impossible to ship again.
    //
    // BL-192 (2026-10-02): a PREVIEW's packages carry a label (10.5.0-preview.1) that System.Version refuses, and the
    // release number is parsed at boot (every admin module's version), by the upgrade ledger and by Create Module. So
    // Constants.Version is the release number, and Constants.PackageVersion is the build's version with its label.
    public class VersionConsistencyTests
    {
        private static string BuildVersion()
        {
            // Directory.Build.props <Version> stamps AssemblyInformationalVersion at build time — the
            // build's own record of the version, with any "+<sha>" suffix stripped.
            var informational = typeof(Constants).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational.Substring(0, plus) : informational;
        }

        [Fact]
        public void Constants_Version_equals_the_build_version()
        {
            var build = BuildVersion();
            var dash = build.IndexOf('-');
            Assert.Equal(dash >= 0 ? build.Substring(0, dash) : build, Constants.Version);
        }

        [Fact]
        public void Constants_PackageVersion_is_the_build_version_with_its_label()
        {
            Assert.Equal(BuildVersion(), Constants.PackageVersion);
            Assert.StartsWith(Constants.Version, Constants.PackageVersion, StringComparison.Ordinal);
        }

        [Fact]
        public void The_release_number_is_numeric_because_the_host_parses_it()
        {
            Assert.Equal(Version.Parse(Constants.Version).ToString(3), Constants.Version);
        }

        [Fact]
        public void The_release_ledger_ends_at_the_current_version()
        {
            var last = Constants.ReleaseVersions.Split(',', StringSplitOptions.RemoveEmptyEntries).Last();
            Assert.Equal(Constants.Version, last);
        }

        [Theory]
        [InlineData("10.5.0", "10.5.0")]
        [InlineData("10.5.0-preview.1", "10.5.0")]
        [InlineData("10.5.0+5f2a91c", "10.5.0")]
        [InlineData("10.5.0-preview.1+5f2a91c", "10.5.0")]
        [InlineData("2.0", "2.0")]
        public void A_package_version_is_compared_by_its_release_number(string version, string release)
        {
            Assert.Equal(Version.Parse(release), Utilities.ParseReleaseNumber(version));
        }

        [Fact]
        public void A_preview_of_a_release_needs_that_release_and_no_later()
        {
            // InstallationManager compares a package's framework version by its release number. A package type cannot carry a
            // label (NuGet refuses it, NU5000) but a framework package's own version can: 10.5.0-preview.1 needs 10.5.0, so the
            // preview's host (which reports its release number) meets it and 10.4.1 does not.
            Assert.True(Version.Parse("10.5.0").CompareTo(Utilities.ParseReleaseNumber("10.5.0-preview.1")) >= 0);
            Assert.False(Version.Parse("10.4.1").CompareTo(Utilities.ParseReleaseNumber("10.5.0-preview.1")) >= 0);
            Assert.Throws<ArgumentException>(() => Utilities.ParseReleaseNumber(" "));
        }
    }
}
