using System.Linq;
using Stomme.Application.Modules.Notes.Services;
using Stomme.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Modules.Notes.Startup
{
    /// <summary>
    /// Registers the HTTP service for the browser. On the server both startups run, assembly by assembly: if the server's
    /// ran first its service is there and this guard skips; if this one ran first, the server's later registration wins.
    /// </summary>
    public class NotesClientStartup : IClientStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotesService)))
            {
                services.AddScoped<INotesService, NotesService>();
            }
        }
    }
}
