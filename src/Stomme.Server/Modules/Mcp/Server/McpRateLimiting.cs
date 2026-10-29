#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Mcp.Server
{
    /// <summary>
    /// The MCP endpoint's rate limit: one fixed window per caller, per minute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists only when the endpoint does, and only when it is configured.</b> The limiter middleware is the host's
    /// (BL-121); this policy is added to it only then. <c>Mcp:Server:RequestsPerMinute</c> at <c>0</c> means no MCP policy
    /// at all - not a limiter set to zero, which would refuse everything.
    /// </para>
    /// <para>
    /// <b>The partition is the CALLER AND THE SITE, which is more than D101 asked for and is the same rule as
    /// everywhere else in this module.</b> Per-principal alone would let a user on one site spend another site's
    /// budget on a multi-tenant install - the class of mistake the whole of item 4 is about. An UNAUTHENTICATED
    /// request is partitioned by remote address instead: it is refused by the route a moment later, but it reaches
    /// the limiter first, and putting every anonymous caller in one partition would let one of them exhaust it for
    /// all of them.
    /// </para>
    /// <para>
    /// <b>A rejected request answers 429</b> and nothing else - no body, no header saying which partition or how much
    /// budget is left, because that is a description of other callers' traffic.
    /// </para>
    /// </remarks>
    internal static class McpRateLimiting
    {
        public const string PolicyName = "mcp-server";

        public const string RequestsPerMinuteKey = "Mcp:Server:RequestsPerMinute";

        public const int DefaultRequestsPerMinute = 60;

        /// <summary>The configured limit, or 0 for "no limiter". A negative or unparseable value reads as the default.</summary>
        public static int RequestsPerMinute(Microsoft.Extensions.Configuration.IConfiguration? configuration)
        {
            var configured = configuration?[RequestsPerMinuteKey];
            if (string.IsNullOrWhiteSpace(configured))
            {
                return DefaultRequestsPerMinute;
            }

            return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : DefaultRequestsPerMinute;
        }

        public static void AddPolicy(IServiceCollection services, int requestsPerMinute)
        {
            if (requestsPerMinute <= 0)
            {
                return;
            }

            // The rejection answer (429, completed at the limiter) is the HOST's since BL-121 -
            // Stomme.Infrastructure.Security.AccountRateLimiting.WriteRejectionAsync, which carries the measured reason the
            // response must be completed there. This adds only the policy.
            services.AddRateLimiter(options =>
            {
                options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = requestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    }));
            });
        }

        /// <summary>The caller and the site - see the type remarks for why both.</summary>
        private static string PartitionKey(HttpContext context)
        {
            var site = context.RequestServices.GetService<SiteState>()?.Alias?.SiteKey ?? context.Request.Host.Host;
            var user = context.User?.Identity?.IsAuthenticated == true
                ? "u:" + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.Identity.Name ?? "?")
                : "a:" + (context.Connection.RemoteIpAddress?.ToString() ?? "?");

            return site + "|" + user;
        }
    }
}
