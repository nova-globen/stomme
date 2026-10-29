using Microsoft.AspNetCore.Routing;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// A module's plain HTTP endpoints - minimal APIs, a redirect, an update manifest - mapped by the host on EVERY install
    /// (M2 row 3, ruling C33). <see cref="Stomme.Transport.IModuleEndpointContributor"/> is for hubs and gRPC and is called
    /// only while a transport is on; this one needs none. Found by the convention scan (a public parameterless class in a
    /// <c>*.Stomme</c> assembly), mapped beside the controllers, and switched off with the rest of its assembly by
    /// <c>module.&lt;id&gt;.enabled=false</c>.
    /// </summary>
    /// <remarks>
    /// The routes are the module's to choose and are HOST-wide: the request is resolved to a site first (a route like
    /// <c>/fravashi/api/...</c> resolves the root site), and the host's own routes win - <c>/healthz</c>, <c>/readyz</c>,
    /// <c>/connect/*</c>, <c>/.well-known/*</c> and <c>/api/*</c> are taken. Authorize what needs it yourself
    /// (<c>RequireAuthorization</c>, or a token check of your own for a cookie-less API).
    /// </remarks>
    public interface IModuleHttpEndpointContributor
    {
        void MapEndpoints(IEndpointRouteBuilder endpoints);
    }
}
