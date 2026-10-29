using System;
using System.Collections.Generic;
using Stomme.Guestbook.Persistence;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Modules.Guestbook.Application;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Modules.Guestbook.Services;
using Stomme.SharedKernel.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Startup
{
    /// <summary>
    /// Wires the Guestbook sample module into the host — and, in doing so, WIRES THE MEDIATOR LIVE. It
    /// is discovered by the framework's reflection scan the same way every other module's startup is, so the
    /// mediator and this module's CQRS/DDD stack plug in by convention rather than by editing the composition
    /// root <c>StommeServiceCollectionExtensions.cs</c>. Until this module, <c>Stomme.Mediator</c> was
    /// built but had ZERO live callers; this is its first activation in the host.
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
            // ---- The module's clean-arch store, STORAGE-BLIND by construction ----------------------
            // ONE call gives a working store on BOTH providers and the module authors ZERO EF code: no DbContext,
            // no value-object converter, no provider branch. On a relational install the HOST maps the aggregate
            // into the shared ModuleAggregatesDbContext (the AuthorName value object becomes a plain string column
            // by the IStringValueObject convention); on a RavenDB install it round-trips through the install's
            // document store. It reads Database:DefaultDBType once (compose time), registers the CLOSED
            // IRepository<GuestbookEntryAggregate, Guid> (never the open generic FeatureManagement owns),
            // wires the shared clean-arch pieces (tracker/dispatcher/unit-of-work) and, on a relational install,
            // an EnsureCreated schema initializer. The Raven support singletons it needs are already TryAdded by
            // AddRavenFrameworkStore in the composition root.
            services.AddModuleAggregatePersistence<GuestbookEntryAggregate, Guid>();

            // The module-specific read GetByModuleAsync is now PROVIDER-NEUTRAL too: it filters through
            // IRepository.FindAsync on an int equality (ModuleId — a SAFE FindAsync predicate), so the closed
            // IGuestbookRepository has ONE implementation on both providers and no longer needs the EF/Raven twin
            // + provider branch it used to. The single GuestbookRepository is a CLOSED registration in
            // Stomme.Guestbook.Persistence (outside Stomme.Modules.*), so it neither collides with
            // FeatureManagement's open generic nor trips the SEAM store parity gate.
            services.AddScoped<IGuestbookRepository, GuestbookRepository>();

            // ---- THE MEDIATOR, LIVE ---------------------------------------------------------------------
            // IMediator + the four pipeline behaviors. Behaviors compose in REGISTRATION ORDER outer-to-inner
            // (the dispatcher reverses the resolved list, so the first registered wraps the rest): exception
            // is the outermost boundary, then logging brackets everything, then performance times the
            // validation+handler, then validation runs last before the handler so an invalid command is
            // rejected as close to the handler as possible after being logged and timed.
            services.AddMediator();
            services.AddMediatorBehavior(typeof(ExceptionBehavior<,>));    // 1 — outermost
            services.AddMediatorBehavior(typeof(LoggingBehavior<,>));      // 2
            services.AddMediatorBehavior(typeof(PerformanceBehavior<,>));  // 3
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));   // 4 — innermost, closest to handler

            // The mediator handlers. AddMediator(assembly) could auto-discover these (they are public), but
            // they are registered explicitly here to keep the wiring visible in one place and to avoid
            // reflecting over the entire Stomme.Server assembly on every boot. The handlers are public
            // because the mediator dispatches to them by dynamic invoke, which requires them to be accessible
            // to the Stomme.Mediator assembly.
            services.AddScoped<IRequestHandler<SignGuestbookCommand, WireModel>, SignGuestbookCommandHandler>();
            services.AddScoped<IRequestHandler<ListGuestbookEntriesQuery, IReadOnlyList<WireModel>>, ListGuestbookEntriesQueryHandler>();
            services.AddScoped<IStreamRequestHandler<StreamGuestbookEntriesQuery, WireModel>, StreamGuestbookEntriesQueryHandler>();
            services.AddScoped<INotificationHandler<GuestbookSignedNotification>, GuestbookSignedNotificationHandler>();
            services.AddScoped<IValidator<SignGuestbookCommand>, SignGuestbookCommandValidator>();

            // The framework domain-event handler (dispatched by the unit of work), COEXISTING with the
            // mediator notification handler above — the two in-process fan-out paths this sample keeps separate.
            services.AddScoped<IDomainEventHandler<GuestbookEntrySignedDomainEvent>, GuestbookEntrySignedDomainEventHandler>();

            // ---- The module's server service (dispatches via IMediator) ---------------------------------
            // Explicit AddTransient so it wins over the client's HTTP implementation on the server, the same
            // pattern HtmlText uses.
            services.AddTransient<IGuestbookService, ServerGuestbookService>();

            // The relational store's EnsureCreated schema initializer is registered by
            // AddModuleAggregatePersistence (and only on a relational install — RavenDB is schemaless), so the
            // module no longer ships its own seeder — nor its own DbContext: the host maps the aggregate
            // into the shared ModuleAggregatesDbContext, so this module authors zero EF code.
        }
    }
}
