#nullable enable
using System;
using Stomme.Health;
using Stomme.Modules.Health.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Health.Infrastructure
{
    /// <summary>
    /// The two anonymous endpoints an orchestrator needs: liveness and readiness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neither endpoint executes a probe. Ever.</b> The obvious implementation — map the health checks and let
    /// them run per request — means an unauthenticated caller can drive a database round-trip, a cache write (to
    /// L2 Redis where configured) and a TCP connect to the site's configured SMTP host, at whatever rate they can
    /// issue requests. That is an amplification vector originating from the site's own IP, and the response time
    /// is a timing oracle for how the dependencies are doing. Liveness answers from the process; readiness answers
    /// from the last completed JOB tick, held in memory.
    /// </para>
    /// <para>
    /// <b><c>/healthz</c> must answer while the installation is UNINSTALLED, and that is the whole point of it.</b>
    /// With no alias there is no tenant, and asking a tenant-dependent question throws — which the probe engine
    /// would faithfully turn into Unhealthy, into a 503, and a container orchestrator would restart the host. A
    /// fresh deployment would then restart-loop forever, before anyone could reach the web installer. So liveness
    /// asks one question — is this process running — and the Docker HEALTHCHECK uses only this endpoint.
    /// </para>
    /// <para>
    /// <b>Readiness reports 503 before the first tick, and that is correct.</b> A host that has not checked
    /// anything does not know it is ready. Degraded is ready: taking a host out of rotation because a dependency
    /// is slow turns "the database is slow" into "there is no capacity left".
    /// </para>
    /// <para>
    /// <b>The bodies are one word.</b> No probe name, no dependency, no host, no connection string, no exception
    /// text — an anonymous caller learns whether to send traffic here and nothing whatever about why not.
    /// </para>
    /// </remarks>
    public static class HealthEndpoints
    {
        public const string LivenessPath = "/healthz";
        public const string ReadinessPath = "/readyz";

        /// <summary>Whether <paramref name="path"/> is one of the health endpoints.</summary>
        /// <remarks>
        /// Consulted by the status-code middleware's skip list. Without it
        /// <c>UseStatusCodePagesWithReExecute("/404", …)</c> runs BEFORE the endpoints and re-executes a 503 into
        /// the site's 404 page — so readiness would answer 404 with a page of HTML instead of 503 with one word,
        /// and every orchestrator reading it would conclude the host is fine.
        /// </remarks>
        public static bool IsHealthEndpoint(string path) =>
            string.Equals(path, LivenessPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, ReadinessPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="path"/> is one of the health endpoints AS MAPPED - the defaults or the paths configured
        /// under <c>Health:LivenessPath</c> / <c>Health:ReadinessPath</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pipeline asks this BEFORE tenant resolution and skips it for these paths. Neither endpoint needs a tenant,
        /// and resolving one for them did harm: a container healthcheck or an external monitor sends a Host that names no
        /// site (<c>127.0.0.1:8080</c>). Tenant resolution answers such a Host with a 404 (<c>UnknownHostTests</c>); before
        /// that, on an installation whose aliases all point at ONE site, it registered any unknown host as a new alias - so
        /// every probe from a new address wrote an alias row (measured, <c>HealthEndpointAnyHostTests</c>).
        /// </para>
        /// <para>
        /// It also exempts them from the HTTPS redirect (<c>Installation:UseHttpsRedirection</c>): a probe of
        /// <c>http://127.0.0.1:8080/healthz</c> inside the container must get its answer, not a redirect to a port nobody
        /// listens on there.
        /// </para>
        /// </remarks>
        public static Func<PathString, bool> Matcher(IConfiguration? configuration)
        {
            var liveness = new PathString(Path(configuration, "Health:LivenessPath", LivenessPath));
            var readiness = new PathString(Path(configuration, "Health:ReadinessPath", ReadinessPath));
            return path =>
                path.Equals(liveness, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(readiness, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Maps the liveness and readiness endpoints. Paths are configurable; both are anonymous.</summary>
        public static IEndpointRouteBuilder MapStommeHealthEndpoints(this IEndpointRouteBuilder endpoints, IConfiguration? configuration)
        {
            var liveness = Path(configuration, "Health:LivenessPath", LivenessPath);
            var readiness = Path(configuration, "Health:ReadinessPath", ReadinessPath);

            // Liveness: the process answered, so the process is alive. No dependency is consulted, deliberately.
            endpoints.MapGet(liveness, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Text("alive", "text/plain");
            }).AllowAnonymous();

            // Readiness: the LAST TICK's verdict, read from memory.
            endpoints.MapGet(readiness, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";

                var snapshot = context.RequestServices.GetService<HealthSnapshot>();
                if (snapshot?.Last is null)
                {
                    // Not installed, or not yet ticked. Either way this host does not know that it is ready, and
                    // saying so is not a restart signal - only /healthz is.
                    return Results.Text("not-ready", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return snapshot.IsReady
                    ? Results.Text("ready", "text/plain")
                    : Results.Text("not-ready", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }).AllowAnonymous();

            return endpoints;
        }

        private static string Path(IConfiguration? configuration, string key, string fallback)
        {
            var configured = configuration?[key];
            return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        }
    }
}
