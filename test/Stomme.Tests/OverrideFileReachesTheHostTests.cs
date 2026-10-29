#nullable enable
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The installation's <c>appsettings.override.json</c> reaches logging and the container's
    /// <see cref="IConfiguration"/>, not only the host's own root. Logging levels set there changed nothing (found by the
    /// app-guidance writers), because the web builder's configuration - which logging and the container read - never had
    /// the file. Driven: with <c>Logging:LogLevel:Default = Warning</c> in the override file, a category logs no Information,
    /// and <see cref="IConfiguration"/> answers the override's value for a key the shipped file does not hold.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OverrideFileReachesTheHostTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task Logging_levels_and_the_containers_configuration_follow_the_override_file()
        {
            var settings = new JsonObject
            {
                ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Warning", ["BL251.Probe"] = "Error" } },
                ["BL251"] = new JsonObject { ["Marker"] = "from-the-override" },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), "localhost", settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                Assert.Equal("from-the-override", app.Services.GetRequiredService<IConfiguration>()["BL251:Marker"]);
                // The rules logging applies, read where the logger factory reads them. (IsEnabled would also answer for the test
                // harness's own capturing provider, whose provider-specific rule wins over these.)
                var rules = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoggerFilterOptions>>().Value.Rules;
                Assert.Contains(rules, rule => rule.ProviderName is null && rule.CategoryName == "BL251.Probe" && rule.LogLevel == LogLevel.Error);
                Assert.Contains(rules, rule => rule.ProviderName is null && rule.CategoryName is null && rule.LogLevel == LogLevel.Warning);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
