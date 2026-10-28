using Stomme.Connectivity;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace NovaGloben.Connector.Template.Startup
{
    /// <summary>
    /// The one registration an external connector needs: discovered by the host's convention scan (the assembly name ends in
    /// "Stomme"), it adds the connector to the host's catalog - which refuses a type key another connector already owns.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IConnector, TemplateConnector>();
    }
}
