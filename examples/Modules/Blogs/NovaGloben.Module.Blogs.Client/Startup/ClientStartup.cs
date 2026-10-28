using System.Linq;
using Stomme.Services;
using Microsoft.Extensions.DependencyInjection;
using NovaGloben.Module.Blogs.Services;

namespace NovaGloben.Module.Blogs.Startup
{
    /// <summary>
    /// Registers the HTTP <see cref="BlogService"/> as <see cref="IBlogService"/> for the CLIENT. Needed
    /// explicitly because the split put the interface in the SHARED assembly while the implementation lives
    /// HERE: the framework's marker-interface auto-registration resolves <c>I{Name}</c> in the
    /// implementation's OWN assembly (<c>Type.GetType</c> on the assembly-qualified name), so a cross-assembly
    /// contract must be wired by an <see cref="IClientStartup"/> — the External template pattern.
    /// <para>
    /// The registration is guarded so the SERVER (which also runs client startups, for Blazor Server
    /// interactivity) is not fought over: whichever side registered <see cref="IBlogService"/> first wins the
    /// guard, and the Server's own <c>ServerStartup</c> adds <c>ServerBlogService</c> LAST-registration-wins
    /// regardless — so on the server the mediator-backed service resolves, and in the browser (where only the
    /// Client + Shared assemblies exist) this HTTP implementation does.
    /// </para>
    /// </summary>
    public class ClientStartup : IClientStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            if (!services.Any(s => s.ServiceType == typeof(IBlogService)))
            {
                services.AddScoped<IBlogService, BlogService>();
            }
        }
    }
}
