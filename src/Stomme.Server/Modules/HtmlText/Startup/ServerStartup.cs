using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Infrastructure;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Modules.HtmlText.Services;

namespace Stomme.Modules.HtmlText.Startup
{
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // not implemented
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // not implemented
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTransient<IHtmlTextService, ServerHtmlTextService>();
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Manager.HtmlTextErasureParticipant>(); // The author's username on content rows

            // The module's own EF context, still the relational migration target and the EF store/session factory's
            // backing context. Transient factory for the tenant-correctness reason documented on the framework's
            // EfSiteStore.
            services.AddDbContextFactory<HtmlTextContext>(opt => { }, ServiceLifetime.Transient);

            // The module's store on the persistence seam: both provider twins, collapsed to a resolve-time
            // selection keyed on the configured database type. Runs AFTER the composition root's
            // AddRavenFrameworkStore, so the RavenDB twin's host singletons are already registered. The framework's
            // own SelectFrameworkStoreProviderByConfig cannot serve a module — it only scans Stomme.Repository.Stores
            // and runs before any module startup — so the module registers its own selector here.
            services.AddHtmlTextStore();
        }
    }
}
