using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Infrastructure;

namespace Stomme.Sdk.Smoke;

/// <summary>
/// A trivial <see cref="IServerStartup"/> (from the <c>Stomme.Server</c> package) that registers the
/// aggregate's persistence through the STORAGE-BLIND helper — one call, no <c>TContext</c>, no EF
/// options lambda, no provider branch. This single line is the whole D78-D81 payoff: it compiles against
/// packages only, and at runtime the host binds the correct <c>IRepository&lt;TrivialAggregate, Guid&gt;</c>
/// for whichever provider the install selected.
/// </summary>
public sealed class SmokeServerStartup : IServerStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddModuleAggregatePersistence<TrivialAggregate, Guid>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // No middleware — this module renders through the host pipeline.
    }

    public void ConfigureMvc(IMvcBuilder mvcBuilder)
    {
        // No module controllers registered in the smoke.
    }
}
