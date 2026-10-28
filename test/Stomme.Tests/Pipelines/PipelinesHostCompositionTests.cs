#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Modules.Pipelines.Persistence.EfCore;
using Stomme.Modules.Pipelines.Startup;
using Stomme.Shared;
using Stomme.Tests.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95 increment (a), D111: the engine reaches the host only through <c>Pipelines:Enabled</c>. Three things a
    /// unit test on the stores cannot see: a STOCK host composes not one engine type (the switch is the only thing
    /// deciding, and the convention scan never registers the engine's hosted services on its own); an ENABLED host
    /// composes under the Development validation (ValidateOnBuild + ValidateScopes), every hosted service constructs,
    /// the stores are the Stomme-backed ones and a step runs; and every engine assembly carries the convention-scan
    /// opt-out the vendor script generates.
    /// </summary>
    public sealed class PipelinesHostCompositionTests : IDisposable
    {
        private readonly LicensingHostComposition _hosts = new();

        public void Dispose() => _hosts.Dispose();

        [Fact]
        public void A_stock_host_composes_no_engine_type_at_all()
        {
            _hosts.ComposeHostBuilder(new Dictionary<string, string?>());
            var descriptors = _hosts.ComposeHostBuilder(new Dictionary<string, string?>()).Builder.Services.ToList();

            var engine = descriptors.Where(IsEngineDescriptor).Select(d => $"{d.ServiceType} -> {d.ImplementationType}").ToList();
            Assert.True(engine.Count == 0, "A stock host registered engine types:" + Environment.NewLine + string.Join(Environment.NewLine, engine));
        }

        [Fact]
        public async Task An_enabled_host_composes_under_validation_resolves_the_Stomme_stores_and_runs_a_step()
        {
            var (builder, _) = _hosts.ComposeHostBuilder(new Dictionary<string, string?> { [ServerStartup.EnabledKey] = "true" });
            builder.Services.AddSingleton<ResultSink>();
            using var app = builder.Build();

            var hostedServices = app.Services.GetServices<IHostedService>().ToList();
            Assert.NotEmpty(hostedServices);
            foreach (var initializer in hostedServices.Where(s => s.GetType().Name == "ModuleAggregateStoreInitializer"))
            {
                await initializer.StartAsync(CancellationToken.None);
            }

            using var scope = app.Services.CreateScope();
            Assert.IsType<EfPipelineDefinitionStore>(scope.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>());
            Assert.IsType<EfPipelineInstanceStore>(scope.ServiceProvider.GetRequiredService<IPipelineInstanceStore>());

            var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
            var result = await runner.RunAsync(new SinkStep { Value = "composed" });
            Assert.Equal(PipelineStatus.Finished, result.PipelineState.Status);
            Assert.Equal("composed", app.Services.GetRequiredService<ResultSink>().Value);

            // BL-229 increment 9: the host's step pipeline tries a failed step again when the step says so.
            var sink = app.Services.GetRequiredService<ResultSink>();
            sink.Value = null;
            var flaky = new FailOnceSinkStep { Value = "second try" };
            flaky.CustomProperties[Stomme.Modules.Pipelines.Reliability.StepReliability.RetriesKey] = 1;
            var retried = await runner.RunAsync(flaky);
            Assert.Equal(PipelineStatus.Finished, retried.PipelineState.Status);
            Assert.Equal("second try", sink.Value);

            // BL-229 increment 5: the host composes the module's Template expression kind, so a definition holding one reads.
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<Stomme.Engine.Expressions.Contracts.IExpressionDescriptorRegistry>().Find(Stomme.Modules.Pipelines.Expressions.TemplateExpressionHandler.Kind));

            // BL-229: the activity ledger wraps the module's commands in the host - constructed under the host's validation - and
            // its store is the module seam's.
            Assert.Contains(scope.ServiceProvider.GetServices<Stomme.Mediator.IPipelineBehavior<Stomme.Modules.Pipelines.Application.PublishPipelineCommand, Stomme.Models.Pipelines.PublishResult>>(),
                behavior => behavior is Stomme.Modules.Pipelines.Audit.PipelineAuditBehavior<Stomme.Modules.Pipelines.Application.PublishPipelineCommand, Stomme.Models.Pipelines.PublishResult>);
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<Stomme.Modules.Pipelines.Audit.IPipelineAuditRepository>());
        }

        // BL-229, found driving "Ask for approval" over MCP: the module registers its handlers one by one (never AddMediator()),
        // so a new query or command with no line in the startup composes, builds, passes every handler-level test - and answers
        // "No handler registered" to every caller. Each request the module declares has its handler in an enabled host.
        [Fact]
        public void Every_request_the_module_declares_has_its_handler_registered()
        {
            var services = _hosts.ComposeHostBuilder(new Dictionary<string, string?> { [ServerStartup.EnabledKey] = "true" }).Builder.Services;
            var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();
            var requests = typeof(ServerStartup).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && (type.Namespace ?? "").StartsWith("Stomme.Modules.Pipelines", StringComparison.Ordinal))
                .Select(type => (Type: type, Response: type.GetInterfaces()
                    .FirstOrDefault(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(Stomme.Mediator.IRequest<>))?.GetGenericArguments()[0]))
                .Where(request => request.Response is not null)
                .ToList();

            Assert.True(requests.Count >= 25, $"expected the module's queries and commands, found {requests.Count}");
            Assert.Contains(requests, request => request.Type == typeof(Stomme.Modules.Pipelines.Application.DecideApprovalCommand));
            var missing = requests
                .Where(request => !registered.Contains(typeof(Stomme.Mediator.IRequestHandler<,>).MakeGenericType(request.Type, request.Response!)))
                .Select(request => request.Type.Name)
                .ToList();
            Assert.True(missing.Count == 0, "Requests with no handler registered in Pipelines' ServerStartup: " + string.Join(", ", missing));
        }

        [Fact]
        public void Every_engine_assembly_opts_out_of_the_convention_scan()
        {
            var probePath = Path.GetDirectoryName(typeof(PipelinesHostCompositionTests).Assembly.Location)!;
            var engineAssemblies = Directory.EnumerateFiles(probePath, "Stomme.Engine*.dll", SearchOption.TopDirectoryOnly)
                .Select(path => Assembly.LoadFrom(path))
                .Where(assembly => !assembly.GetName().Name!.EndsWith(".Tests", StringComparison.Ordinal))
                .ToList();

            // The eleven library projects under src/Engine; a floor, so a twelfth is caught rather than exempted.
            Assert.True(engineAssemblies.Count >= 11, $"expected at least 11 engine assemblies beside the tests, found {engineAssemblies.Count}");
            var unmarked = engineAssemblies.Where(assembly => !assembly.IsStommeFrameworkLibrary()).Select(assembly => assembly.GetName().Name).ToList();
            Assert.True(unmarked.Count == 0, "Engine assemblies without the [StommeFrameworkLibrary] opt-out (scripts/vendor-engine.ps1 step 4b): " + string.Join(", ", unmarked));
        }

        private static bool IsEngineDescriptor(ServiceDescriptor descriptor)
        {
            var implementation = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
                : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            return IsEngineType(descriptor.ServiceType) || (implementation is not null && IsEngineType(implementation));
        }

        private static bool IsEngineType(Type type) =>
            (type.Namespace ?? string.Empty).StartsWith("Stomme.Engine", StringComparison.Ordinal)
            || (type.Namespace ?? string.Empty).StartsWith("Stomme.Modules.Pipelines", StringComparison.Ordinal)
            || type.GenericTypeArguments.Any(IsEngineType);
    }
}
