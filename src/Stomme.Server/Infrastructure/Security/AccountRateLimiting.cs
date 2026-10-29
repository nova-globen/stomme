#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// The account endpoints' rate limits: the host's ONE rate limiter, and the two policies it applies to the
    /// sign-in and account-email endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Per-account lockout stops guessing ONE password; it does nothing about spraying one password across
    /// many accounts, and nothing about hammering the endpoints that SEND mail - password reset, forgot-username, the
    /// login link, registration - each of which writes a live token or a message. the predecessor platform's host limited sign-in per client
    /// address; D98 dropped that host, and Stomme had no limiter at all.
    /// </para>
    /// <para>
    /// <b>Two policies, each a fixed window per (site, client address):</b>
    /// <list type="bullet">
    /// <item><see cref="SignInPolicy"/> - every credential or token ATTEMPT: <c>/pages/login</c>, <c>/pages/loginlink</c>,
    /// <c>api/User/signin</c>, <c>twofactor</c>, <c>reset</c>, <c>verify</c> and the external-login link. Default 10 per 60
    /// seconds (the predecessor platform's number).</item>
    /// <item><see cref="AccountEmailPolicy"/> - every request that makes the host SEND something: <c>forgotpassword</c>,
    /// <c>forgotusername</c> and <c>loginlink</c>; and, as <see cref="RegistrationPolicy"/>, anonymous registration
    /// (<c>POST api/User</c>). Default 5 per 300 seconds.</item>
    /// </list>
    /// A permit limit of <c>0</c> switches that policy off (no limiter, not a limiter that refuses everything). Configured
    /// under <c>RateLimiting:Account</c> - <c>SignIn:PermitLimit</c>, <c>SignIn:WindowSeconds</c>, <c>Email:PermitLimit</c>,
    /// <c>Email:WindowSeconds</c>.
    /// </para>
    /// <para>
    /// <b>The partition is the SITE and the CLIENT ADDRESS.</b> The site, so one site's traffic cannot spend another
    /// site's budget on a multi-tenant install (the MCP limiter's rule). The address is <c>Connection.RemoteIpAddress</c>
    /// AFTER <c>UseForwardedHeaders</c>, which this host configures to trust any proxy: behind the reverse proxy a
    /// deployment is meant to have (nginx, the Cloudflare tunnel), that is the client the proxy saw; on a host exposed
    /// DIRECTLY, a caller can choose its own <c>X-Forwarded-For</c> and with it a fresh partition. That is a property of the
    /// forwarded-headers configuration, not of this limiter, and it is stated here so nobody reads this as cover it is not.
    /// </para>
    /// <para>
    /// <b>A refusal is 429 with <c>Retry-After</c>, and says nothing else</b> - no partition, no remaining budget. A form
    /// POST from a browser gets one plain sentence; an API caller gets <c>{"error":"Too many requests."}</c>.
    /// </para>
    /// </remarks>
    public static class AccountRateLimiting
    {
        public const string SignInPolicy = "account-sign-in";

        public const string AccountEmailPolicy = "account-email";

        /// <summary>
        /// Registration - <c>POST api/User</c> - on the EMAIL budget, for ANONYMOUS callers only: the same action is the
        /// admin's "Add User", and an administrator adding a sixth user in five minutes is not an attack.
        /// </summary>
        public const string RegistrationPolicy = "account-registration";

        public const string SectionName = "RateLimiting:Account";

        public static readonly (int PermitLimit, int WindowSeconds) SignInDefault = (10, 60);

        public static readonly (int PermitLimit, int WindowSeconds) EmailDefault = (5, 300);

        /// <summary>
        /// Registers the host's rate limiter with the account policies. Always: the policies are ON by default, and a
        /// module that adds its own policy (MCP) adds it to these options rather than to a second limiter.
        /// </summary>
        public static IServiceCollection AddAccountRateLimiting(this IServiceCollection services, IConfiguration? configuration)
        {
            var signIn = Read(configuration, "SignIn", SignInDefault);
            var email = Read(configuration, "Email", EmailDefault);

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = WriteRejectionAsync;
                options.AddPolicy(SignInPolicy, context => Partition(context, signIn));
                options.AddPolicy(AccountEmailPolicy, context => Partition(context, email));
                options.AddPolicy(RegistrationPolicy, context => context.User?.Identity?.IsAuthenticated == true
                    ? RateLimitPartition.GetNoLimiter("authenticated")
                    : Partition(context, email));
            });

            return services;
        }

        /// <summary>A configured limit; a missing, negative or unparseable value reads as the default.</summary>
        public static (int PermitLimit, int WindowSeconds) Read(IConfiguration? configuration, string name, (int PermitLimit, int WindowSeconds) fallback)
        {
            var section = configuration?.GetSection(SectionName).GetSection(name);
            return (Positive(section?["PermitLimit"], fallback.PermitLimit, allowZero: true), Positive(section?["WindowSeconds"], fallback.WindowSeconds, allowZero: false));
        }

        /// <summary>The site and the client address - see the type remarks for both halves.</summary>
        public static string PartitionKey(HttpContext context)
        {
            var site = context.RequestServices.GetService<SiteState>()?.Alias?.SiteKey ?? context.Request.Host.Host;
            return site + "|" + (context.Connection.RemoteIpAddress?.ToString() ?? "?");
        }

        /// <summary>
        /// The ONE rejection answer for every policy on this host (the MCP policy included). The response is COMPLETED
        /// here: MCP measured that a limiter which only sets the status lets the antiforgery middleware rewrite the empty
        /// response on the way out.
        /// </summary>
        public static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            if (IsBrowserForm(context.HttpContext.Request))
            {
                response.ContentType = "text/plain; charset=utf-8";
                await response.WriteAsync("Too many attempts. Please wait a minute and try again.", cancellationToken);
            }
            else
            {
                response.ContentType = "application/json";
                await response.WriteAsync("{\"error\":\"Too many requests.\"}", cancellationToken);
            }
        }

        private static RateLimitPartition<string> Partition(HttpContext context, (int PermitLimit, int WindowSeconds) limit) =>
            limit.PermitLimit <= 0
                ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit.PermitLimit,
                    Window = TimeSpan.FromSeconds(limit.WindowSeconds),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });

        private static bool IsBrowserForm(HttpRequest request) =>
            request.HasFormContentType || request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

        private static int Positive(string? configured, int fallback, bool allowZero) =>
            int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && (value > 0 || (allowZero && value == 0))
                ? value
                : fallback;
    }
}
