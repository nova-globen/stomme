#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Infrastructure;
using Stomme.Mcp;
using Stomme.Mcp.Persistence;
using Stomme.Modules.Mcp.Application;
using Stomme.Modules.Mcp.Contributions;
using Stomme.Modules.Mcp.Domain;
using Stomme.Modules.Mcp.Infrastructure;
using Stomme.Modules.Mcp.Server;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Mcp.Startup
{
    /// <summary>
    /// Wires the MCP module: its two storage-blind aggregates and their closed repositories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovered by the framework's assembly scan like every other module startup. Each aggregate takes ONE
    /// <c>AddModuleAggregatePersistence</c> call and the module authors no persistence code; the registration
    /// is the NON-tracking one, so a page in a long-lived circuit reads what the database says.
    /// </para>
    /// <para>
    /// <b>What is NOT here yet is not half-registered either.</b> The catalog, the endpoint, the client connector and the
    /// retention purge arrive with their own increments. The host VALIDATES its container at build, so a service
    /// registered ahead of one of its dependencies is a host that does not boot - this file registers only what is
    /// complete. A stock install gains two empty tables in the module store and nothing else: no route, no job, no page.
    /// </para>
    /// </remarks>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // no middleware - the endpoint is mapped by a later increment, behind Mcp:Server:Enabled

            if (!McpSettings.ServerEnabled(HostConfiguration.Resolve(app.ApplicationServices)))
            {
                return;
            }

            // The registry is resolved ONCE here, while the pipeline is built, so that whatever it REJECTED is in the
            // boot log before the first request rather than discovered by a caller an hour later.
            //
            // It does not stop the host, and this is why: contributor
            // discovery matches any assembly in /bin whose name contains Stomme, INCLUDING a package
            // InstallationManager unpacked there - so a fatal check let a third-party package brick an install, with no
            // admin UI left to remove it. Every side of a collision is dropped instead, which keeps the security
            // property intact: a same-named descriptor still cannot downgrade a gate or hijack a handler, because the
            // name stops being served at all.
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var registry = scope.ServiceProvider.GetRequiredService<McpContributorRegistry>();
                var logger = scope.ServiceProvider.GetService<ILogger<McpContributorRegistry>>();
                foreach (var conflict in registry.Conflicts)
                {
                    logger?.LogError(
                        "MCP contribution REJECTED - {Kind} '{Name}' from {Contributors}: {Reason} " +
                        "It is not served. Remove or rename the conflicting contribution to restore it.",
                        conflict.Kind, conflict.Name, string.Join(" and ", conflict.Contributors), conflict.Reason);
                }
            }

            // AFTER UseTenantResolution: the catalog reads the alias this request resolved, and a route
            // mapped before it would see none and deny everything. IServerStartup.Configure runs after the framework's
            // own UseTenantResolution/UseAuthentication/UseAuthorization, which is what makes this the right place.
            //
            // RequireAuthorization with no policy = RequireAuthenticatedUser against the DEFAULT scheme, which is the
            // Identity cookie - nobody else is admitted, and DefaultScheme is NOT touched.
            if (app is IEndpointRouteBuilder routes)
            {
                var configuration = HostConfiguration.Resolve(app.ApplicationServices);
                var perMinute = McpRateLimiting.RequestsPerMinute(configuration);

                var endpoint = routes.MapMcp(McpSettings.Path(configuration))
                    .RequireAuthorization()
                    // So the cookie challenge answers 401 here instead of this host's usual bare 403. See McpEndpointMarker.
                    .WithMetadata(new McpEndpointMarker());

                if (perMinute > 0)
                {
                    // The middleware is the HOST's, placed once in UseStomme, which gives the host a limiter of its
                    // own, so the endpoint only opts in. (A second UseRateLimiter here would be redundant rather than
                    // harmful - MEASURED by mutation: the request is still counted once.)
                    endpoint.RequireRateLimiting(McpRateLimiting.PolicyName);
                }
            }
            else
            {
                // A host composed without endpoint routing cannot serve the endpoint, and must not pretend to.
                throw new InvalidOperationException(
                    "Mcp:Server:Enabled is true but this application builder is not an IEndpointRouteBuilder, so /mcp cannot be mapped.");
            }
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // no MVC customization
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<McpServerRegistrationAggregate, Guid>();
            services.AddModuleAggregatePersistence<McpAuditEntryAggregate, Guid>();

            services.AddScoped<IMcpServerRegistrationRepository, McpServerRegistrationRepository>();
            services.AddScoped<IMcpAuditRepository, McpAuditRepository>();
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Application.McpAuditErasureParticipant>(); // A security record, retained one year then deleted

            // The clock is shared by both halves, so it is registered before either switch is read.
            services.TryAddSingleton<IMcpClock, SystemMcpClock>();

            // The SERVER side of IMcpService, registered UNCONDITIONALLY and before either switch. The framework scans
            // IClientService only in the WebAssembly client's Program, so without this a site whose Runtime is Server -
            // the default for a fresh install and for every site the admin UI creates - has nothing to resolve for the
            // control's @inject, and the admin page cannot render. It takes both halves as OPTIONAL constructor
            // parameters, so it composes whatever the switches say and answers "not enabled" rather than failing to
            // compose and taking the host with it.
            services.AddTransient<Services.IMcpService, Services.ServerMcpService>();

            var configuration = McpSettings.ComposeTimeConfiguration(services);

            // The CLIENT half (Mcp:Enabled) - external-server registrations and what they offer. A SEPARATE switch from
            // the server half: conflating them hides the admin page behind the endpoint's flag, and
            // an install that only CONSUMES external servers publishes nothing of its own.
            if (McpSettings.ClientEnabled(configuration))
            {
                services.TryAddScoped<IMcpEndpointGuard, McpEndpointGuard>();
                services.TryAddScoped<IMcpClientConnector, McpClientConnector>();
                services.TryAddScoped<IMcpClientToolSource, McpClientToolSource>();
                services.TryAddScoped<McpRegistrationService>();
            }

            // The SERVER half is off by default, and off means absent: no contributor is even looked for, no catalog is
            // registered (McpDisabledSurfaceTests).
            if (!McpSettings.ServerEnabled(configuration))
            {
                return;
            }

            // The audit ledger's retention pass, contributed to the framework's PurgeJob rather than shipped as a job
            // of its own. Registered with the SERVER half because that is the half that WRITES the rows:
            // a stock install composes no extra pass, and so its purge notes are unchanged. An install that switches
            // the server half OFF keeps its existing ledger un-aged, which is the safe side of that trade - it is a
            // security record, and it can gain no new rows while the endpoint is gone.
            services.AddScoped<IRetentionPurger, McpAuditRetentionPurger>();

            McpRateLimiting.AddPolicy(services, McpRateLimiting.RequestsPerMinute(configuration));

            // The IFeatureContributor convention: public parameterless constructors in Stomme assemblies.
            ConventionContributorDiscovery.AddDiscovered<IMcpContributor>(services);

            // The framework's OWN contribution needs the site this request resolved, so it is SCOPED and registered
            // explicitly - the path reserved for a contributor with dependencies, since the convention scan only
            // finds public parameterless constructors. It lands in the same registry as every discovered one, so its
            // names are checked for collision exactly like a third-party module's: the framework is not privileged.
            services.AddScoped<IMcpContributor, StommeSiteContributor>();
            services.TryAddScoped<McpContributorRegistry>();
            // Registered as the CONCRETE type with the interface resolving to the SAME scoped instance: the seam
            // (Stomme.Shared) carries what a contributor and the endpoint need, and the admin page additionally needs
            // the effective approval - a site's override applied - which is this implementation's to answer and nobody
            // else's. Resolving the concrete type is also how the controller's action constraint asks "is the server
            // half on?" without reading configuration a second time.
            services.TryAddScoped<McpServerCatalog>();
            services.TryAddScoped<IMcpServerCatalog>(sp => sp.GetRequiredService<McpServerCatalog>());

            // The SDK server, with a scope per request: the catalog is scoped and reads the alias this request
            // resolved. Stateless HTTP - a Stomme host may be several instances behind one hostname, and a session
            // the next instance has never heard of is a 404 nobody can act on.
            services.AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "Stomme",
                        Version = typeof(ServerStartup).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    };
                    options.ScopeRequests = true;
                })
                .WithHttpTransport(transport => transport.Stateless = true)
                .WithListToolsHandler(McpServerBridge.ListToolsAsync)
                .WithCallToolHandler(McpServerBridge.CallToolAsync)
                .WithListResourcesHandler(McpServerBridge.ListResourcesAsync)
                .WithListResourceTemplatesHandler(McpServerBridge.ListResourceTemplatesAsync)
                .WithReadResourceHandler(McpServerBridge.ReadResourceAsync);
        }
    }

    /// <summary>The module's switches, read from the configuration THIS host runs on.</summary>
    public static class McpSettings
    {
        /// <summary>The CLIENT half: the external-server registrations, their admin page, and the tool source.</summary>
        public const string ClientEnabledKey = "Mcp:Enabled";

        public const string ServerEnabledKey = "Mcp:Server:Enabled";

        public const string PathKey = "Mcp:Server:Path";

        public const string DefaultPath = "/mcp/v1";

        /// <summary>
        /// The route the endpoint is mapped at. A configured value must be an absolute path - a relative one would map
        /// somewhere nobody intended, and silently.
        /// </summary>
        public static string Path(IConfiguration? configuration)
        {
            var configured = configuration?[PathKey];
            return string.IsNullOrWhiteSpace(configured) ? DefaultPath
                : configured!.StartsWith("/", StringComparison.Ordinal) ? configured
                : throw new InvalidOperationException($"{PathKey} is '{configured}'; an MCP route is an absolute path such as {DefaultPath}.");
        }

        public static bool ServerEnabled(IConfiguration? configuration) =>
            bool.TryParse(configuration?[ServerEnabledKey], out var enabled) && enabled;

        /// <summary>
        /// The client half. <b>Two switches, not one</b>: <see cref="ServerEnabledKey"/> decides whether this
        /// install PUBLISHES over MCP; this one decides whether it may REGISTER external servers. Reading one for the
        /// other is a mistake - the client-half admin page must not hide behind the endpoint's flag.
        /// </summary>
        public static bool ClientEnabled(IConfiguration? configuration) =>
            bool.TryParse(configuration?[ClientEnabledKey], out var enabled) && enabled;

        /// <summary>
        /// The merged root while the container is still being composed: <c>AddStomme</c> registers it as an INSTANCE
        /// before any module startup runs, so it can be read off the service collection.
        /// </summary>
        public static IConfiguration? ComposeTimeConfiguration(IServiceCollection services) =>
            services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?.ImplementationInstance as IConfigurationRoot
            ?? services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
    }
}
