using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Contributions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-218: the <c>files</c> contributor's Site-scope probe names the file rows whose file is gone from disk. Driven in
    /// an installed SQLite host, through the contributors the host's container hands the probe pass: a folder holding one
    /// file that is there and one whose file was deleted under its row answers Degraded and names the second; with the file
    /// put back it answers Healthy again.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MissingFilesHealthProbeTests : IClassFixture<FileIngestionHostFixture>
    {
        private readonly FileIngestionHostFixture _fixture;
        private readonly ITestOutputHelper _output;

        public MissingFilesHealthProbeTests(FileIngestionHostFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public async Task A_file_row_whose_file_is_gone_is_named_and_the_site_is_degraded_until_it_is_back()
        {
            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);
            var folder = await drive.NewFolderAsync();
            await drive.PlaceFileAsync(folder, "present.txt", FileIngestionDrive.Text("here"));
            await drive.PlaceFileAsync(folder, "lost.png", FileIngestionDrive.Text("gone soon"));
            var lostPath = Path.Combine(await drive.FolderPathAsync(folder), "lost.png");
            System.IO.File.Delete(lostPath);

            MissingFilesContributor.ForgetAnswers();
            var degraded = await ProbeAsync();
            _output.WriteLine($"{degraded.State}: {degraded.Description}");
            Assert.Equal(HealthState.Degraded, degraded.State);
            Assert.NotNull(degraded.Data);
            Assert.Contains(folder.Path + "lost.png", degraded.Data!.Keys);
            Assert.Contains("need uploading again: " + folder.Path + "lost.png (", degraded.Description); // the page shows the description
            Assert.DoesNotContain(folder.Path + "present.txt", degraded.Data.Keys);

            // Kept for the interval: a second tick does not walk the disk again.
            await System.IO.File.WriteAllBytesAsync(lostPath, FileIngestionDrive.Text("back"));
            Assert.Equal(HealthState.Degraded, (await ProbeAsync()).State);

            MissingFilesContributor.ForgetAnswers();
            var healthy = await ProbeAsync();
            _output.WriteLine($"{healthy.State}: {healthy.Description}");
            Assert.Equal(HealthState.Healthy, healthy.State);
        }

        private Task<HealthProbeResult> ProbeAsync() =>
            GateDrive.InTenantAsync(_fixture.App, FileIngestionHostFixture.Host, async (sp, alias) =>
            {
                var contributor = sp.GetServices<IHealthContributor>().Single(candidate => candidate.ContributorId == MissingFilesContributor.Id);
                var probe = contributor.Probes.Single(candidate => candidate.Name == MissingFilesContributor.ProbeName);
                Assert.Equal(HealthProbeScope.Site, probe.Scope);
                return await probe.CheckAsync(new HealthProbeContext(alias.SiteId));
            });
    }
}
