using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Modules
{
    public interface IPortable
    {
        // You Must Set The "ServerManagerType" In Your IModule Interface

        Task<string> ExportModuleAsync(Module module);

        Task ImportModuleAsync(Module module, string content, string version);
    }
}
