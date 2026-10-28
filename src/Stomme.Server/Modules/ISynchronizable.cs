using System;
using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Modules
{
    public interface ISynchronizable
    {
        // You Must Set The "ServerManagerType" In Your IModule Interface

        Task<string> ExtractModuleAsync(Module module, DateTime lastSynchronizedOn);

        Task LoadModuleAsync(Module module, string content);
    }
}
