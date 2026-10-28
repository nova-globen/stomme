using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Where in the host's request pipeline an <see cref="IServerPipelineStartup"/> adds its middleware. Every stage is
    /// EARLIER than <see cref="IServerStartup.Configure"/>, which still runs where it always has (after authentication,
    /// authorization and antiforgery, just before the endpoints).
    /// </summary>
    public enum ServerPipelineStage
    {
        /// <summary>
        /// Before the static files. Sees EVERY request, including the ones a static file answers - the place for
        /// response headers that must be on assets too (security headers). No tenant is resolved yet.
        /// </summary>
        BeforeStaticFiles = 0,

        /// <summary>
        /// After the static files and the exception handler, before tenant resolution. No alias, site or tenant is
        /// known yet - the place to answer a request outright (a 301 for a legacy URL) or to rewrite its path before the
        /// host resolves the site from it.
        /// </summary>
        BeforeTenantResolution = 1,

        /// <summary>
        /// After tenant resolution (the alias and the site settings are on the <c>HttpContext</c>) and before the host's
        /// site-JWT middleware and authentication - the place to move a token that arrived some other way (a
        /// <c>?access_token=</c> query value a <c>&lt;video&gt;</c> element sends) into the <c>Authorization</c> header.
        /// </summary>
        BeforeJwt = 2,

        /// <summary>
        /// After the site-JWT middleware, before routing: the tenant is known and a site JWT has been read, no endpoint
        /// has been selected yet.
        /// </summary>
        BeforeRouting = 3
    }

    /// <summary>
    /// An OPT-IN way for an app or module to put middleware earlier in the host's pipeline than
    /// <see cref="IServerStartup.Configure"/>. Discovered like <see cref="IServerStartup"/> - a public or internal,
    /// non-abstract class with a parameterless constructor in a loaded Stomme assembly - and called once, while the
    /// pipeline is built, at its <see cref="Stage"/>. Within a stage the hooks run by <see cref="Order"/> (lower
    /// first), then by the type's full name, so the order never depends on assembly load order.
    /// </summary>
    public interface IServerPipelineStartup
    {
        /// <summary>The point in the pipeline where <see cref="Configure"/> runs.</summary>
        ServerPipelineStage Stage { get; }

        /// <summary>Order within <see cref="Stage"/>; lower runs first. Defaults to 0.</summary>
        int Order => 0;

        /// <summary>Adds middleware to <paramref name="app"/> at <see cref="Stage"/>.</summary>
        void Configure(IApplicationBuilder app, IWebHostEnvironment env);
    }
}
