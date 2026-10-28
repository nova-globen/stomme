using System;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using [Owner].Module.[Module].Domain;
using [Owner].Module.[Module].Persistence;
using [Owner].Module.[Module].Services;

namespace [Owner].Module.[Module].Startup
{
    /// <summary>
    /// The module's server composition, found by the host's convention scan (an <see cref="IServerStartup"/> in a
    /// <c>*.Stomme</c> assembly). It names NO store: <c>AddModuleAggregatePersistence</c> puts the aggregate on whichever
    /// store the installation chose - SQLite, PostgreSQL, SQL Server, RavenDB or the file store - and into the installation's
    /// backups (D79, D80, D106).
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.TryAddSingleton(TimeProvider.System);

            services.AddModuleAggregatePersistence<[Module]Entry, Guid>();
            services.AddScoped<I[Module]EntryRepository, [Module]EntryRepository>();

            // The commands and queries in this assembly.
            services.AddMediator<ServerStartup>();

            // Last registration wins over the browser's HTTP service, which the host also composes for Blazor Server.
            services.AddTransient<I[Module]Service, Server[Module]Service>();
        }
    }
}
