#nullable enable
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Startup;
using Stomme.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>
    /// Signs a personal MCP token's user in on the MCP endpoint (D101a, BL-101). A request to <c>Mcp:Path</c> (<c>/mcp/v1</c>)
    /// carrying <c>Authorization: Bearer smcp_…</c> and no signed-in session is resolved to the token's user ON THE REQUEST'S
    /// SITE - a token issued on another site, revoked, expired, or whose user is deleted signs no one in - and the principal is
    /// Stomme's own (<see cref="UserSecurity.CreateClaimsIdentity(Stomme.Models.Alias, Stomme.Models.User)"/>), so roles and
    /// permission rows are read per request, plus one <see cref="McpTokenScopes.ClaimType"/> claim per scope, which the catalog
    /// narrows by. Anything else leaves the request anonymous, and the endpoint answers 401. Nothing about a token is logged.
    /// Only this path: a token is not a key to the site's API or pages.
    /// </summary>
    internal sealed class McpPersonalTokenMiddleware(RequestDelegate next, PathString path)
    {
        public async Task Invoke(HttpContext context)
        {
            if (context.Request.Path.StartsWithSegments(path)
                && context.User?.Identity?.IsAuthenticated != true
                && Secret(context.Request) is { } secret)
            {
                try
                {
                    if (await ResolveAsync(context, secret) is { } principal)
                    {
                        context.User = principal;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Fail closed: a token that cannot be resolved signs no one in, and the endpoint answers 401.
                    context.RequestServices.GetService<ILogger<McpPersonalTokenMiddleware>>()?.LogError(exception, "A personal MCP token on {Path} could not be resolved; the request continues anonymous.", context.Request.Path);
                }
            }

            await next(context);
        }

        private static string? Secret(HttpRequest request)
        {
            var header = request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer " + Domain.McpPersonalTokenAggregate.SecretPrefix, StringComparison.OrdinalIgnoreCase)
                ? header.Substring("Bearer ".Length).Trim()
                : null;
        }

        private static async Task<ClaimsPrincipal?> ResolveAsync(HttpContext context, string secret)
        {
            var alias = context.GetAlias();
            var tokens = context.RequestServices.GetService<McpPersonalTokenService>();
            if (alias is null || tokens is null || await tokens.ResolveAsync(secret, alias.SiteId) is not { } token)
            {
                return null;
            }

            var user = await context.RequestServices.GetRequiredService<IUserManager>().GetUserAsync(token.UserId, alias.SiteId);
            if (user is null || user.IsDeleted)
            {
                return null;
            }

            var identity = UserSecurity.CreateClaimsIdentity(alias, user);
            foreach (var scope in token.ScopeList())
            {
                identity.AddClaim(new Claim(McpTokenScopes.ClaimType, scope));
            }

            return new ClaimsPrincipal(identity);
        }
    }

    /// <summary>
    /// Places <see cref="McpPersonalTokenMiddleware"/> after tenant resolution (the token is the site's) and before routing and
    /// authentication, which keep the principal it set when no session cookie came. Only while the server half is on.
    /// </summary>
    public sealed class McpPersonalTokenPipeline : IServerPipelineStartup
    {
        public ServerPipelineStage Stage => ServerPipelineStage.BeforeRouting;

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            var configuration = HostConfiguration.Resolve(app.ApplicationServices);
            if (McpSettings.ServerEnabled(configuration))
            {
                app.UseMiddleware<McpPersonalTokenMiddleware>(new PathString(McpSettings.Path(configuration)));
            }
        }
    }
}
