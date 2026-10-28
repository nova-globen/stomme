using System;
using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    public interface ISiteTask
    {
        string ExecuteTask(IServiceProvider provider, Site site, string parameters);

        Task<string> ExecuteTaskAsync(IServiceProvider provider, Site site, string parameters);
    }
}
