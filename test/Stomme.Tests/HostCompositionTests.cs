using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stomme.Infrastructure.Tenancy;
using Stomme.Persistence.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Rebus.Bus;
using Xunit;

namespace Stomme.Tests
{
    // The guard that was missing. Every other suite composes its OWN small container, so none of them ever
    // ran the framework's reflection scan over the real host wiring — which is exactly how a container that
    // could not be constructed in ANY configuration shipped with a clean build and a green suite (the composition rule
    // was violated; see src/Stomme.Server/README.md).
    //
    // These tests build the ACTUAL host container — the real AddStomme, the real assembly scan, the real
    // module startups — and assert it composes. They reproduce both observed failure modes:
    //   * Development died at builder.Build()     -> ValidateOnBuild
    //   * Production died at Host.StartAsync()    -> resolving every IHostedService
    // and, with messaging on, the lifetime bug that hid behind them (a scoped service resolved from the root
    // provider) -> ValidateScopes + actually resolving IBus, which is what forces Rebus to configure.
    public class HostCompositionTests : IDisposable
    {
        private readonly List<string> _tempRoots = new();

        // The stock path: an install that opted into nothing. This is what every existing deployment runs,
        // and it is the configuration that must never fail to boot.
        [Fact]
        public void Host_container_composes_with_the_default_configuration()
        {
            using var app = ComposeHost(new Dictionary<string, string?>());

            AssertEveryHostedServiceIsConstructible(app);
        }

        // The framework building blocks the Server references directly must OPT OUT of the module-convention
        // scan. The scan auto-registers by marker interface over every Stomme.* assembly it loads, so
        // a library that is composed EXPLICITLY - the mediator, the authorization bridge and its ports - has
        // to carry [StommeFrameworkLibrary] or a type added to it later is registered twice over, once by
        // hand and once by convention. The csproj comment beside these references asserts the opt-out; this is
        // the assertion.
        [Fact]
        public void Every_explicitly_composed_framework_library_opts_out_of_the_module_scan()
        {
            var libraries = new[]
            {
                typeof(Stomme.Mediator.IMediator).Assembly,
                typeof(Stomme.Authorization.AuthorizationBehavior<,>).Assembly,
                typeof(Stomme.Security.Abstractions.IAccessGuard).Assembly,
                // Composed explicitly (AddSecretProtection) for the shared per-site secret store.
                typeof(Stomme.Infrastructure.Security.Cryptography.SecretProtectionServiceExtensions).Assembly
            };

            var missing = libraries.Where(assembly => !assembly.IsStommeFrameworkLibrary()).Select(assembly => assembly.GetName().Name).ToList();

            Assert.Empty(missing);
        }
        // Every opt-in switch on at once. The switches are meant to gate their subsystems; this proves the
        // gated-on path composes too, and that the gates are the only thing deciding.
        [Fact]
        public void Host_container_composes_with_every_opt_in_switch_on()
        {
            using var app = ComposeHost(new Dictionary<string, string?>
            {
                ["Transport:Grpc:Enabled"] = "true",
                ["Transport:SignalR:Enabled"] = "true",
                ["Messaging:Enabled"] = "true",
                ["Messaging:Transport"] = "inmemory",
                ["Messaging:InputQueue"] = "stomme-host-composition-test",
                // The offline licence ring is an opt-in switch like the others.
                ["Deployment:Mode"] = "OnPrem",
            });

            AssertEveryHostedServiceIsConstructible(app);

            // Rebus builds its bus lazily from a factory, so neither ValidateOnBuild nor the hosted-service
            // resolve above reaches the bus configuration callback. Resolving IBus does — and that callback is
            // where AddMessaging used to resolve the SCOPED ITenantContext from the ROOT provider and throw
            // ("Cannot resolve scoped service ... from root provider", rewrapped by Rebus as "Error when
            // trying to resolve default bus instance!").
            var bus = app.Services.GetRequiredService<IBus>();
            Assert.NotNull(bus);
        }

        // Deployment:Mode=OnPrem on its own composes the offline licence provider and its singleton state; the
        // composition must validate (ValidateOnBuild + ValidateScopes) and every hosted service must still construct.
        [Fact]
        public void Host_container_composes_under_OnPrem()
        {
            using var app = ComposeHost(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = "OnPrem",
            });

            AssertEveryHostedServiceIsConstructible(app);
        }

        // The tenancy adapter is a new composition path, so it gets the same guard the rest of the host
        // has. Two things are asserted that a unit test on the adapter itself cannot see:
        //   * the REAL adapter wins over Stomme.FeatureManagement's single-tenant DefaultTenantContext
        //     fallback. That fallback is a TryAdd, so winning depends entirely on AddStommeTenancy() running
        //     before the assembly scan invokes the module startups. Reorder those two calls and this is the
        //     only thing that notices — the app would still boot, and every tenant would quietly share
        //     tenant 1's flags.
        //   * the scoped ports resolve from a SCOPE and the singleton one from the ROOT. Under ValidateScopes,
        //     a captive dependency or a root-resolved scoped service fails here rather than in production —
        //     the exact failure mode that already shipped once (src/Stomme.Persistence/README.md, the scoped-from-root trap).
        [Fact]
        public void Host_composes_the_real_tenancy_adapter_over_the_single_tenant_fallback()
        {
            using var app = ComposeHost(new Dictionary<string, string?>());

            using var scope = app.Services.CreateScope();

            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            Assert.IsType<StommeTenantContext>(tenantContext);

            Assert.IsType<StommeTenantConnectionResolver>(scope.ServiceProvider.GetRequiredService<ITenantConnectionResolver>());
            Assert.IsType<StommeTenantDirectory>(scope.ServiceProvider.GetRequiredService<ITenantDirectory>());

            // Singleton on purpose: its callers (the flag seeder, a bus pipeline step) are singletons, so it
            // has to be resolvable from the root provider without tripping ValidateScopes.
            Assert.IsType<TenantScopeFactory>(app.Services.GetRequiredService<ITenantScopeFactory>());

            // Outside a request there is no alias, and the adapter must fail closed rather than claim system
            // scope (which the seam's query filter would read as "every tenant's rows").
            Assert.Null(tenantContext.CurrentTenantId);
            Assert.False(tenantContext.IsSystemScope);
        }

        // With messaging on, AddMessaging's tenant propagation only activates when BOTH tenancy ports are
        // registered — it asks IServiceProviderIsService. Now that the host registers them, that path is live
        // for the first time, so the all-switches-on composition covers it.
        [Fact]
        public void The_tenant_scope_flows_into_a_di_scope_created_inside_it()
        {
            using var app = ComposeHost(new Dictionary<string, string?>());

            var scopes = app.Services.GetRequiredService<ITenantScopeFactory>();

            // This is the seeder's exact shape: enter the tenant scope, THEN create the DI scope, and the
            // context resolved inside must see the tenant. If the ambient scope were per-DI-scope state rather
            // than AsyncLocal, this would silently read null and every per-tenant seed would land wrong.
            using (scopes.BeginScope(FrameworkTenantId.ToTenantId(2).Value))
            {
                using var scope = app.Services.CreateScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

                Assert.Equal(FrameworkTenantId.ToTenantId(2), tenantContext.CurrentTenantId);
                Assert.False(tenantContext.IsSystemScope);
            }
        }

        /// <summary>
        /// Pins the DECIDED semantics of the host's <see cref="IDomainEventDispatcher"/>: exactly one
        /// registration, and it is the current-scope dispatcher, scoped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE DECISION IS MADE: CURRENT-SCOPE dispatch. Handlers resolve from the scope that raised the events,
        /// inside the caller's unit of work. Domain events stay inside one bounded context, and the
        /// outbox row inside the aggregate's transaction; a child scope would put the handler outside that
        /// transaction, which is what the outbox does deliberately, for integration events. Concretely,
        /// current scope is what carries the scoped tenant context into handlers without re-propagation, and
        /// what keeps the flag audit trail atomic with the change it audits.
        /// </para>
        /// <para>
        /// WHAT THIS CATCHES, AND WHAT IT NO LONGER HAS TO. The old pin had an honest hole: both
        /// registrations were TryAdd (first-caller-wins) over DIFFERENT implementations, so it only fired if
        /// AddDomainEvents ran BEFORE the FeatureManagement startup — called after, TryAdd left the rival
        /// untouched and the test stayed green over unchanged semantics. The design dissolved that hole rather than
        /// papering over it: both rival implementations are deleted and every registration path
        /// (AddDomainEvents, AddFeatureManagement) now TryAdds the SAME CurrentScopeDomainEventDispatcher, so
        /// registration order is semantically inert — TryAdd wins or loses with the same type either way.
        /// </para>
        /// <para>
        /// So this test now catches what remains catchable at the container: a SECOND rival implementation
        /// being introduced, or the dispatcher's lifetime drifting. It does NOT catch a new-scope dispatcher
        /// smuggled in by editing CurrentScopeDomainEventDispatcher itself — that is guarded by construction
        /// instead: the type takes IServiceProvider and has no IServiceScopeFactory to create a scope with, so
        /// a new-scope rewrite means changing its constructor and its name, which fails HERE. The handler-side
        /// half of the decision (no SaveChanges, no scope juggling) is pinned by
        /// DomainEventHandlerContractTests.
        /// </para>
        /// </remarks>
        [Fact]
        public void Host_registers_exactly_one_domain_event_dispatcher_and_it_dispatches_in_the_current_scope()
        {
            var builder = ComposeHostBuilder(new Dictionary<string, string?>());

            var dispatchers = builder.Services
                .Where(descriptor => descriptor.ServiceType == typeof(IDomainEventDispatcher))
                .ToList();

            var dispatcher = Assert.Single(dispatchers);

            Assert.Equal(typeof(CurrentScopeDomainEventDispatcher), dispatcher.ImplementationType);

            // Scoped: dispatch is per unit of work, its consumer (IUnitOfWork) is scoped, and so are handlers.
            Assert.Equal(ServiceLifetime.Scoped, dispatcher.Lifetime);

            // The decision, asserted on the type rather than on its name. Current-scope dispatch means taking
            // the AMBIENT provider; a dispatcher that takes IServiceScopeFactory is one that can open a child
            // scope, which is the semantics the design rejected. Pinning the constructor makes new-scope dispatch
            // unrepresentable rather than merely absent.
            var constructor = Assert.Single(typeof(CurrentScopeDomainEventDispatcher).GetConstructors());
            var parameter = Assert.Single(constructor.GetParameters());
            Assert.Equal(typeof(IServiceProvider), parameter.ParameterType);
        }

        /// <summary>
        /// Pins the OBSERVED <see cref="IUnitOfWork"/> composition of the real host: exactly one registration,
        /// and it is the single collapsed <c>Stomme.Persistence.Abstractions.UnitOfWork</c>, scoped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THIS PINS A COLLAPSE, NOT A LATENCY. Two rival <see cref="IUnitOfWork"/> implementations USED to
        /// exist — <c>Stomme.Persistence.UnitOfWork</c> (registered by <c>AddDomainEvents</c>, clear before
        /// dispatch) and <c>Stomme.FeatureManagement.Application.FeatureUnitOfWork</c> (registered by
        /// <c>AddFeatureManagement</c>, dispatch before clear) — and BOTH registered via <c>TryAddScoped</c>,
        /// so registration order silently decided the re-dispatch semantics. That was the same rival-TryAdd
        /// shape the design dissolved for <c>IDomainEventDispatcher</c>, left standing one line below it.
        /// </para>
        /// <para>
        /// They were collapsed. There is now ONE implementation — the clear-before-dispatch
        /// <c>Stomme.Persistence.Abstractions.UnitOfWork</c>, in the one assembly both registration paths
        /// reference — and both <c>AddDomainEvents</c> and <c>AddFeatureManagement</c> <c>TryAddScoped</c> that
        /// SAME type. So the order-dependence is gone: whichever <c>Add*()</c> runs first (or a host that calls
        /// both, as <c>AddOutbox</c> does via <c>AddDomainEvents</c>) resolves the identical impl. The live host
        /// composes <c>AddFeatureManagement</c>; this test proves the composition is one type, and that neither
        /// deleted rival type name is present.
        /// </para>
        /// </remarks>
        [Fact]
        public void Host_registers_exactly_one_unit_of_work_and_the_rival_is_unreachable()
        {
            var builder = ComposeHostBuilder(new Dictionary<string, string?>());

            var unitsOfWork = builder.Services
                .Where(descriptor => descriptor.ServiceType == typeof(IUnitOfWork))
                .ToList();

            var unitOfWork = Assert.Single(unitsOfWork);

            // The single collapsed impl. By full name for parity with the guard below; typeof() would
            // also work now that the type is public in Stomme.Persistence.Abstractions.
            Assert.Equal(
                "Stomme.Persistence.Abstractions.UnitOfWork",
                unitOfWork.ImplementationType?.FullName);

            Assert.Equal(ServiceLifetime.Scoped, unitOfWork.Lifetime);

            // The other half of the observation, asserted rather than narrated: neither retired rival is in the
            // host's composition at all. If either concrete type is ever resurrected and re-registered — the
            // exact order-dependent divergence that was removed — THIS is the line that notices.
            Assert.DoesNotContain(
                builder.Services,
                descriptor => descriptor.ImplementationType?.FullName is
                    "Stomme.Persistence.UnitOfWork" or
                    "Stomme.FeatureManagement.Application.FeatureUnitOfWork");
        }

        /// <summary>
        /// Pins the DELIBERATE current state: the composed host registers NO <see cref="IDomainEventSerializer"/>.
        /// This is the outbox footgun turned into a red build.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="IDomainEventSerializer"/> is a PROCESS-GLOBAL singleton switch. <c>EfRepository</c> and its
        /// Raven twin take <c>IEnumerable&lt;IDomainEventSerializer&gt;</c> and call <c>FirstOrDefault()</c>;
        /// the instant ONE is registered, EVERY repository over EVERY DbContext in the process flips from
        /// in-process domain-event dispatch to writing the transactional outbox (EfRepository.AfterPersist).
        /// That silently stops feature-flag domain events reaching <c>FeatureFlagAuditHandler</c> and kills
        /// the audit trail — no compile error, no failing existing test, just a dead handler.
        /// </para>
        /// <para>
        /// <c>AddOutbox</c> (PersistenceServiceExtensions.cs) is the only thing that registers the serializer,
        /// and it has ZERO call sites in <c>src/</c> today — the outbox is intentionally unwired. The
        /// activation MODEL (per-context switch vs. routing the FM audit through the outbox) is an UNRESOLVED
        /// decision. This guard makes a future agent who adds <c>AddOutbox</c> to "finish the outbox wiring" get a
        /// RED BUILD here instead of a silently-dead audit trail.
        /// </para>
        /// </remarks>
        [Fact]
        public void Host_registers_no_global_domain_event_serializer_so_the_outbox_stays_unwired()
        {
            using var app = ComposeHost(new Dictionary<string, string?>());

            var serializers = app.Services.GetServices<IDomainEventSerializer>().ToList();

            Assert.True(
                serializers.Count == 0,
                $"The composed host registered {serializers.Count} IDomainEventSerializer(s): " +
                $"[{string.Join(", ", serializers.Select(s => s.GetType().FullName))}]. " +
                "IDomainEventSerializer is the transactional-outbox serializer and a PROCESS-GLOBAL singleton " +
                "switch: registering even one flips EVERY EfRepository over EVERY DbContext from in-process " +
                "domain-event dispatch to outbox capture (EfRepository.AfterPersist), which SILENTLY kills the " +
                "feature-flag audit trail (FeatureFlagAuditHandler). Wiring AddOutbox is therefore NOT a " +
                "paperwork step to 'finish DoD #3'. The activation model (per-context switch vs. routing the FM " +
                "audit through the outbox) is an UNRESOLVED design decision. If you " +
                "are intentionally activating the outbox, you must FIRST resolve that design and THEN update " +
                "this guard; do not delete it to go green.");
        }

        // The Production failure mode: validate-on-build is off there, so the container built fine and the
        // host died later, on the first thing that actually resolves them. Resolving IHostedService is exactly
        // what Host.StartAsync does.
        private static void AssertEveryHostedServiceIsConstructible(WebApplication app)
        {
            var hostedServices = app.Services.GetServices<IHostedService>().ToList();

            Assert.NotEmpty(hostedServices);
            // A framework building-block library must not have had its hosted services convention-registered:
            // they belong to an opt-in Add*() and only that call may register them.
            Assert.DoesNotContain(hostedServices, s => s.GetType().Name == "OutboxBackgroundService");
        }

        // Mirrors Program.Main: same builder, same DataDirectory contract, same AddStomme call — but with
        // the DI validation the real host only gets in Development, and pointed at a throwaway content root so
        // the scan's Packages folder and the flag store's SQLite file never land in the repo.
        private WebApplication ComposeHost(Dictionary<string, string?> settings) =>
            ComposeHostBuilder(settings).Build();

        // The same composition as ComposeHost, stopped one step earlier: the descriptors are still on the
        // collection and can be enumerated. Building resolves them away behind IServiceProvider, which can
        // answer "what do I get?" but not "how many registrations are there, and whose?".
        private WebApplicationBuilder ComposeHostBuilder(Dictionary<string, string?> settings)
        {
            LoadFrameworkAssemblies();

            var contentRoot = CreateTempContentRoot();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = contentRoot,
                WebRootPath = "wwwroot",
                EnvironmentName = Environments.Development,
                ApplicationName = typeof(Stomme.Server.Program).Assembly.GetName().Name,
            });

            builder.Logging.ClearProviders();

            // ValidateOnBuild reproduces the Development crash; ValidateScopes turns a captive/root-resolved
            // scoped dependency into a test failure instead of a runtime surprise.
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(contentRoot, "Data"));

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            builder.Services.AddStomme(configuration, builder.Environment);

            return builder;
        }

        // In the real host, LoadAssemblies() pulls every Stomme*.dll out of /bin into the default load
        // context before the scan runs. A test process only loads what it has touched, so force the same set
        // in — otherwise the scan sees a subset and the test would pass vacuously on the very assemblies
        // (Stomme.Persistence, Stomme.Infrastructure.EventBus) whose registration broke the host.
        private static void LoadFrameworkAssemblies()
        {
            var probePath = Path.GetDirectoryName(typeof(HostCompositionTests).Assembly.Location)!;

            foreach (var dll in Directory.EnumerateFiles(probePath, "Stomme*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Assembly.Load(AssemblyName.GetAssemblyName(dll));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    // Not a managed Stomme assembly, or already loaded from elsewhere — the scan is
                    // name-based and tolerates this the same way the framework's loader does.
                }
            }
        }

        private string CreateTempContentRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-host-composition", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            _tempRoots.Add(root);
            return root;
        }

        public void Dispose()
        {
            foreach (var root in _tempRoots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // A file the host still holds open; the OS reclaims the temp folder.
                }
            }
        }
    }
}
