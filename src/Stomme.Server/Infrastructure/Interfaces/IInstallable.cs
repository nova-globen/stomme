using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    public interface IInstallable
    {
        Task<bool> InstallAsync(Tenant tenant, string version);

        Task<bool> UninstallAsync(Tenant tenant);
    }
}
