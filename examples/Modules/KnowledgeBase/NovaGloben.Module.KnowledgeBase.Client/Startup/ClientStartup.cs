using System.Linq;
using Stomme.Services;
using Microsoft.Extensions.DependencyInjection;
using NovaGloben.Module.KnowledgeBase.Services;

namespace NovaGloben.Module.KnowledgeBase.Startup
{
    /// <summary>
    /// Registers the HTTP <see cref="KnowledgeBaseService"/> for the BROWSER. Explicit because the contract lives in the
    /// Shared assembly and the implementation here: the framework's marker-interface auto-registration only pairs an
    /// implementation with an <c>I{Name}</c> in its own assembly. Guarded, and the server's own startup registers its
    /// mediator-backed service last, so on the server that one resolves and in the browser this one does.
    /// </summary>
    public class ClientStartup : IClientStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(IKnowledgeBaseService)))
            {
                services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
            }
        }
    }
}
