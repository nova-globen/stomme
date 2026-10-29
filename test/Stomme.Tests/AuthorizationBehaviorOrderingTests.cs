using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Authorization;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Pins the ORDER of the authorization bridge in the REAL host (F4): the
    /// <see cref="AuthorizationBehavior{TRequest,TResponse}"/> is the FIRST pipeline behavior — hence the
    /// OUTERMOST, because <c>Mediator.Send</c> reverses the resolved behaviors and wraps once per behavior —
    /// on a STOCK install (no lifted module, no opt-in switch), and an un-annotated request passes through it
    /// untouched. The spike registered the behavior innermost and only when a lifted module existed; a
    /// reorder of the composition root (registering after <c>AddStommeAssemblies()</c>, whose module
    /// startups register the exception/logging/performance/validation chain) is what this notices.
    /// </summary>
    public class AuthorizationBehaviorOrderingTests : IDisposable
    {
        private readonly List<string> _tempRoots = new();

        [Fact]
        public void The_first_pipeline_behavior_on_a_stock_install_is_the_authorization_behavior()
        {
            var builder = ComposeHostBuilder();

            var behaviors = builder.Services
                .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
                .ToList();

            // Not vacuous: the stock host carries the Guestbook module's own chain after ours.
            Assert.True(behaviors.Count >= 2, "expected the authorization behavior AND at least one module behavior on a stock install");
            Assert.Equal(typeof(AuthorizationBehavior<,>), behaviors[0].ImplementationType);
            Assert.Contains(behaviors.Skip(1), descriptor => descriptor.ImplementationType == typeof(ExceptionBehavior<,>));
            Assert.Single(behaviors, descriptor => descriptor.ImplementationType == typeof(AuthorizationBehavior<,>));
        }

        [Fact]
        public void The_resolved_pipeline_for_a_request_starts_with_the_authorization_behavior()
        {
            using var app = ComposeHost();
            using var scope = app.Services.CreateScope();

            var pipeline = scope.ServiceProvider.GetServices<IPipelineBehavior<UngatedProbe, string>>().ToList();

            Assert.True(pipeline.Count >= 2);
            Assert.IsType<AuthorizationBehavior<UngatedProbe, string>>(pipeline[0]);
        }

        [Fact]
        public async Task An_un_annotated_request_passes_through_the_composed_host_untouched()
        {
            using var app = ComposeHost();
            using var scope = app.Services.CreateScope();

            // No HttpContext at all (a headless caller): an un-annotated request has nothing to gate.
            var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new UngatedProbe());

            Assert.Equal("handled", result);
        }

        [Fact]
        public async Task A_gated_request_outside_a_request_is_denied_before_its_handler_runs()
        {
            using var app = ComposeHost();
            using var scope = app.Services.CreateScope();

            var handler = scope.ServiceProvider.GetRequiredService<ProbeHandler>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            await Assert.ThrowsAsync<AccessDeniedException>(() => mediator.Send(new GatedProbe()));
            Assert.False(handler.Ran);
        }

        // The ports themselves are present on a stock install — one registration each.
        [Fact]
        public void The_three_ports_are_registered_once_on_a_stock_install()
        {
            var builder = ComposeHostBuilder();

            Assert.Single(builder.Services, d => d.ServiceType == typeof(IAccessGuard));
            Assert.Single(builder.Services, d => d.ServiceType == typeof(ICurrentUser));
            Assert.Single(builder.Services, d => d.ServiceType == typeof(IFeatureEvaluator));
        }

        public sealed class UngatedProbe : IRequest<string>;

        [Requires("gatetest:manage")]
        public sealed class GatedProbe : IRequest<string>;

        public sealed class ProbeHandler : IRequestHandler<UngatedProbe, string>, IRequestHandler<GatedProbe, string>
        {
            public bool Ran { get; private set; }

            public Task<string> Handle(UngatedProbe request, CancellationToken ct)
            {
                Ran = true;
                return Task.FromResult("handled");
            }

            public Task<string> Handle(GatedProbe request, CancellationToken ct)
            {
                Ran = true;
                return Task.FromResult("handled");
            }
        }

        // Mirrors HostCompositionTests.ComposeHost: the REAL AddStomme over the real assembly scan, with the
        // DI validation the host only gets in Development, on a throwaway content root. The probe handlers
        // are added AFTER AddStomme, exactly as a module startup's handlers are — they cannot move the
        // behavior order.
        private WebApplication ComposeHost() => ComposeHostBuilder().Build();

        private WebApplicationBuilder ComposeHostBuilder()
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
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(contentRoot, "Data"));

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            builder.Services.AddStomme(configuration, builder.Environment);

            builder.Services.AddScoped<ProbeHandler>();
            builder.Services.AddScoped<IRequestHandler<UngatedProbe, string>>(sp => sp.GetRequiredService<ProbeHandler>());
            builder.Services.AddScoped<IRequestHandler<GatedProbe, string>>(sp => sp.GetRequiredService<ProbeHandler>());

            return builder;
        }

        private static void LoadFrameworkAssemblies()
        {
            var probePath = Path.GetDirectoryName(typeof(AuthorizationBehaviorOrderingTests).Assembly.Location)!;

            foreach (var dll in Directory.EnumerateFiles(probePath, "Stomme*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Assembly.Load(AssemblyName.GetAssemblyName(dll));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                }
            }
        }

        private string CreateTempContentRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "stomme-authz-ordering", Guid.NewGuid().ToString("N"));
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
                }
            }
        }
    }
}
