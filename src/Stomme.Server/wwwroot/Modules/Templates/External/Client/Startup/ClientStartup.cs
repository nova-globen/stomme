using System.Linq;
using Stomme.Services;
using Microsoft.Extensions.DependencyInjection;
using [Owner].Module.[Module].Services;

namespace [Owner].Module.[Module].Startup
{
    /// <summary>The browser's composition: the HTTP service, unless the server already registered its own.</summary>
    public class ClientStartup : IClientStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(I[Module]Service)))
            {
                services.AddScoped<I[Module]Service, [Module]Service>();
            }
        }
    }
}
