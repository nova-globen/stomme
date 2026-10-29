#nullable enable
using System;
using Stomme.Infrastructure;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Modules.AiAssistant.Domain;
using Stomme.Modules.AiAssistant.Infrastructure;
using Stomme.AiAssistant;
using Stomme.Mediator;
using FluentValidation;
using Stomme.AiAssistant.Persistence;
using Stomme.Modules.ContextualHelp.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.AiAssistant.Startup
{
    /// <summary>
    /// Wires the AI assistant: the conversation (storage-blind, tenant-owned), the installation
    /// switch and limits, the OpenAI-compatible provider, the per-site model access with its metered and quota-gated client,
    /// the guardrail, the erasure participant; then the agents over the workspace gateways, the appliers and the conversation
    /// service, and the gated requests behind <c>api/AiAssistant</c> and the <c>admin/assistant</c> chat panel. Everything is OFF
    /// until <c>AiAssistant:Enabled</c> and the site's own switch are on; with either off nothing reaches a model.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // no middleware
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // no MVC customization
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<AssistantConversationAggregate, Guid>();
            services.AddScoped<IAssistantConversationRepository, AssistantConversationRepository>();
            services.TryAddSingleton(TimeProvider.System);

            // AiAssistant (off by default) - from THIS host's configuration, the override file included.
            services.TryAddSingleton(provider =>
            {
                var options = new AssistantOptions();
                HostConfiguration.Resolve(provider)?.GetSection(AssistantOptions.SectionName).Bind(options);
                return options;
            });

            services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProvider, OpenAiCompatibleLlmProvider>());
            services.TryAddScoped<IAssistantCaller, AssistantCaller>();
            services.TryAddScoped<IAssistantSiteSettings, SettingAssistantSiteSettings>();
            services.TryAddScoped<IAssistantModelAccess, AssistantModelAccess>();

            // The guardrail keeps its rate windows for the process; the default authorizer refuses what it cannot check.
            services.TryAddSingleton<IAssistantAuthorizer, RefusingAssistantAuthorizer>();
            services.TryAddSingleton<IAssistantGuardrail>(provider => new AssistantGuardrail(provider.GetRequiredService<AssistantOptions>(), provider.GetRequiredService<IAssistantAuthorizer>()));

            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, AssistantConversationErasure>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, AssistantConversationSubjectContributor>();

            // The agents: they read and write ONLY through the owning modules' gated requests, as the user.
            services.TryAddScoped<IAssistantQueryGateway, MediatorAssistantQueryGateway>();
            services.TryAddScoped<IAssistantCommandGateway, MediatorAssistantCommandGateway>();
            services.TryAddScoped<IWidgetSpecGenerator, WidgetGeneratorAgent>();

            // The Landing module's designer: registered over the module's "none composed" default - a
            // plain Add, so it wins whichever startup runs first. It needs the landing sanitizer, which the Landing module owns.
            services.TryAddSingleton<Stomme.Modules.Landing.Infrastructure.LandingHtmlSanitizer>();
            services.AddScoped<Stomme.Modules.Landing.Application.ILandingPageDesigner, LandingPageDesignerAgent>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssistantSpecialist, ChatAssistantAgent>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssistantSpecialist, DashboardDesignerAgent>());

            // A specialist exists only while the module it works through is composed - the router never names one that
            // could only fail. Read from the host's own configuration at composition, the modules' own switches.
            if (Stomme.Modules.Pipelines.Startup.ServerStartup.IsEnabled(services))
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssistantSpecialist, PipelineArchitectAgent>());

                // The editor's AI builder - over the Pipelines module's "none composed" default, a plain Add as the Landing
                // designer's is, so it wins whichever startup runs first.
                services.AddScoped<Stomme.Modules.Pipelines.Application.IPipelineGraphDesigner, PipelineGraphDesignerAgent>();
                services.AddScoped<Stomme.Modules.Pipelines.Application.IPipelineScriptWriter, PipelineScriptWriterAgent>();
            }

            if (HelpSettings.Enabled(HelpSettings.ComposeTimeConfiguration(services)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssistantSpecialist, ContextualHelpAgent>());
            }

            services.TryAddScoped<IAssistantAgent, OrchestratorAgent>();
            services.TryAddScoped<PipelineArtifactApplier>();
            services.TryAddScoped<DashboardArtifactApplier>();
            services.TryAddScoped<AssistantConversationService>();

            // The surface: every request under assistant:use, decided before a handler runs.
            services.AddScoped<IRequestHandler<StartConversationCommand, AssistantConversationView>, StartConversationCommandHandler>();
            services.AddScoped<IRequestHandler<ListConversationsQuery, System.Collections.Generic.IReadOnlyList<AssistantConversationSummary>>, ListConversationsQueryHandler>();
            services.AddScoped<IRequestHandler<GetConversationQuery, AssistantConversationView?>, GetConversationQueryHandler>();
            services.AddScoped<IRequestHandler<SendAssistantMessageCommand, AssistantConversationView?>, SendAssistantMessageCommandHandler>();
            services.AddScoped<IRequestHandler<ApplyAssistantArtifactCommand, AssistantArtifactApplyResult?>, ApplyAssistantArtifactCommandHandler>();
            services.AddScoped<IRequestHandler<DiscardAssistantArtifactCommand, bool>, DiscardAssistantArtifactCommandHandler>();
            services.AddScoped<IValidator<SendAssistantMessageCommand>, SendAssistantMessageCommandValidator>();
            services.AddScoped<IRequestHandler<GetAssistantSettingsQuery, AssistantSettingsView>, GetAssistantSettingsQueryHandler>();
            services.AddScoped<IRequestHandler<SaveAssistantSettingsCommand, AssistantSettingsView>, SaveAssistantSettingsCommandHandler>();
            services.AddScoped<IValidator<SaveAssistantSettingsCommand>, SaveAssistantSettingsCommandValidator>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<Stomme.Modules.AiAssistant.Services.IAiAssistantService, Services.ServerAiAssistantService>();
        }
    }
}
