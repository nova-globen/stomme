#nullable enable
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Installer.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The installer's SQLite default lands in the host's data directory, not the process's working directory.
    /// </summary>
    /// <remarks>
    /// Reported 2026-10-02 by the Didin session: the wizard proposed <c>Data Source=Stomme-&lt;ts&gt;.db;</c>, a path relative
    /// to the working directory, so in a container (WORKDIR <c>/app</c>, the data mounted at <c>/app/Data</c>) the database
    /// went into the image's layer and was lost when the container was recreated.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SqliteDataDirectoryInstallTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public SqliteDataDirectoryInstallTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData("site.db", "Data Source=|DataDirectory|/site.db;")]
        [InlineData("  site.db ", "Data Source=|DataDirectory|/site.db;")]
        [InlineData("db/site.db", "Data Source=|DataDirectory|/db/site.db;")]
        [InlineData("|DataDirectory|/site.db", "Data Source=|DataDirectory|/site.db;")]
        [InlineData("/var/lib/app/site.db", "Data Source=/var/lib/app/site.db;")]
        [InlineData("C:\\data\\site.db", "Data Source=C:\\data\\site.db;")]
        [InlineData("", "")]
        public void A_file_name_is_placed_in_the_data_directory_and_a_rooted_path_kept(string entered, string expected) =>
            Assert.Equal(expected, SqliteConfig.ConnectionStringFor(entered));

        [Fact]
        public async Task The_wizards_default_installs_into_the_data_directory()
        {
            var connectionString = SqliteConfig.ConnectionStringFor("bl194.db");
            using var scratch = new ScratchGateInstall(
                new SqliteGateStore(), Host,
                new JsonObject { ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString } });
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                Assert.True(alias.SiteId > 0);
                Assert.True(File.Exists(Path.Combine(scratch.ContentRoot, "Data", "bl194.db")),
                    "the installed database is not in the host's data directory");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                ScratchSqlite.ReleaseInstall(scratch.ContentRoot);
            }
        }
    }
}
