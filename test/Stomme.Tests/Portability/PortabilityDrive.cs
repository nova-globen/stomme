#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Domain;
using Stomme.Modules.Portability.Execution;
using Stomme.Portability.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// Runs the REAL build job until the site's queue is empty. On a STARTED host the scheduled job ticks on its own and
    /// holds the per-site lock while it does, so a single manual run can find the site taken and build nothing (measured on
    /// RavenDB: the notes said the site was held and the rows stayed Building). Running again until nothing is Building is
    /// what a deployed install does every minute.
    /// </summary>
    internal static class PortabilityDrive
    {
        public static async Task BuildQueuedAsync(WebApplication app, string host, ITestOutputHelper output)
        {
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                output.WriteLine($"[job {attempt}] " + await GateDrive.InTenantAsync(app, host, (sp, _) =>
                    new PortabilityBuildJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp)));

                var building = await GateDrive.InTenantAsync(app, host, async (sp, alias) =>
                    (await sp.GetRequiredService<IPortabilityPackageRepository>().ListByStatusAsync(alias.SiteId, PortabilityPackageStatus.Building)).Count);
                if (building == 0)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            throw new TimeoutException("Packages were still Building after 20 runs of the build job.");
        }
    }
}