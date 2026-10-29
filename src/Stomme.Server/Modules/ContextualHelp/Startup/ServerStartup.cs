#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Mediator;
using Stomme.Models.Help;
using Stomme.Modules.ContextualHelp.Application;
using Stomme.Help;
using Stomme.Help.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.ContextualHelp.Startup
{
    /// <summary>
    /// Wires contextual help: the aggregate and its closed repository (always), and - only while
    /// <c>Help:Enabled</c> - the contributors and their registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is not built yet is not half-registered either.</b> The controller, the client service and the launcher
    /// arrive with their own increments. The host VALIDATES its container at build, so a service registered ahead of one of its
    /// dependencies is a host that does not boot.
    /// </para>
    /// <para>
    /// <b>The aggregate's persistence is registered UNCONDITIONALLY, and the feature is not.</b> A switch turned off
    /// must not take the DATA with it: an install that enables help, writes topics, and then disables it must still
    /// have them when it is switched back on. A stock install gains one empty table in the module store and nothing
    /// else - no page, no route, no reconciler pass, no launcher output.
    /// </para>
    /// </remarks>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // no middleware and no route - both arrive with the increments that need them

            if (!HelpSettings.Enabled(HostConfiguration.Resolve(app.ApplicationServices)))
            {
                return;
            }

            // The registry is resolved ONCE here, while the pipeline is built, so whatever it REJECTED is in the boot log
            // before the first reader asks for help - the MCP module's pattern. A rejection never stops the host.
            using var scope = app.ApplicationServices.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<HelpTopicContributorRegistry>();
            var logger = scope.ServiceProvider.GetService<ILogger<HelpTopicContributorRegistry>>();
            foreach (var conflict in registry.Conflicts)
            {
                logger?.LogError(
                    "Help contribution REJECTED - {Kind} '{Name}' from {Contributors}: {Reason} " +
                    "Remove or rename the conflicting contribution to restore it.",
                    conflict.Kind, conflict.Name, string.Join(" and ", conflict.Contributors), conflict.Reason);
            }
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // no MVC customization
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<HelpTopicAggregate, Guid>();
            services.AddScoped<IHelpTopicRepository, HelpTopicRepository>();

            // The SERVER side of IHelpService, UNCONDITIONALLY: the module definition is discovered on every install, so a
            // control an operator places by hand must be able to resolve its service - it answers "nothing" while
            // Help:Enabled is off rather than failing to compose (the MCP module's pattern).
            services.AddTransient<Modules.ContextualHelp.Services.IHelpService, Services.ServerHelpService>();

            // Off means ABSENT: no contributor is even looked for, so a stock install composes nothing more than the
            // table above (HelpDisabledSurfaceTests).
            if (!HelpSettings.Enabled(HelpSettings.ComposeTimeConfiguration(services)))
            {
                return;
            }

            // The IFeatureContributor convention - which also finds the framework's own StommeHelpContributor, so it
            // lands in the registry exactly like a package's and is checked for collision the same way.
            ConventionContributorDiscovery.AddDiscovered<IHelpTopicContributor>(services);

            // SCOPED: a contributor registered explicitly may need scoped services, and a singleton cannot hold those.
            services.TryAddScoped<HelpTopicContributorRegistry>();

            // Stateless, so singletons: the fixed Markdig pipeline and the allowlist behind it.
            services.TryAddSingleton<HelpHtmlSanitizer>();
            services.TryAddSingleton<HelpMarkdownRenderer>();

            // The boot/upgrade path's writer - run by the framework's SiteContentReconciliationRunner, which enters each
            // tenant's scope BEFORE it creates the site's DI scope and contains a throw to this reconciler on that site
            // (D103 refutations 1 and 2). It is the ONLY thing that writes help on a boot.
            services.AddScoped<ISiteContentReconciler, HelpTopicReconciler>();

            // The read port (the launcher, the controller and BL-97's assistant): PUBLISHED topics of the CURRENT site
            // only, by construction (D103 refutation 12). Scoped, because it reads the site this request resolved.
            services.TryAddScoped<IHelpTopicProvider, HelpTopicProvider>();

            // The gated surface. Handlers are registered EXPLICITLY - never AddMediator() here: an earlier
            // startup already composed the mediator, and a second call would double every pipeline behaviour (BL-78
            // F13). Found by driving: without these every api/Help call was a 500 - the mediator had no handler.
            services.AddScoped<IRequestHandler<ResolveHelpTopicQuery, HelpTopicView?>, ResolveHelpTopicQueryHandler>();
            services.AddScoped<IRequestHandler<ListPublishedHelpTopicsQuery, IReadOnlyList<HelpTopicSummary>>, ListPublishedHelpTopicsQueryHandler>();
            services.AddScoped<IRequestHandler<GetHelpTopicForEditQuery, HelpTopicEditView?>, GetHelpTopicForEditQueryHandler>();
            services.AddScoped<IRequestHandler<SaveHelpTopicCommand, Guid>, SaveHelpTopicCommandHandler>();
            services.AddScoped<IRequestHandler<PublishHelpTopicCommand, Unit>, PublishHelpTopicCommandHandler>();
            services.AddScoped<IRequestHandler<PreviewHelpMarkdownQuery, string>, PreviewHelpMarkdownQueryHandler>();
            services.AddScoped<IRequestHandler<ListAllHelpTopicsQuery, IReadOnlyList<HelpTopicSummary>>, ListAllHelpTopicsQueryHandler>();
            services.AddScoped<IRequestHandler<ListHelpContributionConflictsQuery, IReadOnlyList<HelpConflictView>>, ListHelpContributionConflictsQueryHandler>();
            services.AddScoped<IRequestHandler<DeleteHelpTopicCommand, Unit>, DeleteHelpTopicCommandHandler>();
            services.AddScoped<IRequestHandler<ResetHelpTopicToSeedCommand, Unit>, ResetHelpTopicToSeedCommandHandler>();
        }
    }

    /// <summary>Contextual help's switches, read from the configuration THIS host runs on.</summary>
    public static class HelpSettings
    {
        /// <summary>
        /// The feature's own switch. <b>Default FALSE</b> (D103 refutation 14): the <c>Messaging:Enabled</c> idiom
        /// this copies has no default, upgraded installs carry no <c>Help</c> section at all, and the shipped
        /// framework topics DO render on admin pages - so a default of true would change every existing install's
        /// admin surface on upgrade without anyone asking for it.
        /// </summary>
        public const string EnabledKey = "Help:Enabled";

        public static bool Enabled(IConfiguration? configuration) =>
            bool.TryParse(configuration?[EnabledKey], out var enabled) && enabled;

        /// <summary>
        /// The merged root while the container is still being composed: <c>AddStomme</c> registers it as an INSTANCE
        /// before any module startup runs, so it can be read off the service collection.
        /// </summary>
        public static IConfiguration? ComposeTimeConfiguration(IServiceCollection services) =>
            services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?.ImplementationInstance as IConfigurationRoot
            ?? services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration;
    }
}
