using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stomme.Extensions;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Transport;
using Xunit;

namespace Stomme.Tests
{
    // A module can declare a transport requirement (ModuleDefinition.Transports, populated from its
    // IModule exactly like Runtimes), and a declared-but-disabled transport produces a LOUD, attributable
    // error log at startup — deliberately NOT the silent-vanish tone Runtimes enforcement uses. The unit
    // half drives the checker directly; the end-to-end half drives the REAL MapStommeTransport path with a
    // REAL IModule discovered by the assembly scan.
    public class TransportRequirementTests
    {
        // A real IModule in a Stomme-named assembly: the module-definition scan
        // (ModuleDefinitionRepository.GetModuleDefinitionsFromAssemblies) discovers it as a headless module
        // and carries its declared Transports, exactly as it does Runtimes. The declaration is
        // static-configurable so this module is INERT ("" = no requirement, no log) in every other suite
        // that boots a transport host.
        public sealed class TransportDeclaringTestModule : IModule
        {
            public static string DeclaredTransports { get; set; } = "";

            public ModuleDefinition ModuleDefinition => new ModuleDefinition
            {
                Name = "TransportDeclaringTestModule",
                Description = "BL-53c test module declaring a transport requirement",
                Transports = DeclaredTransports,
            };
        }

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

            public void Dispose()
            {
            }

            private sealed class CapturingLogger : ILogger
            {
                private readonly CapturingLoggerProvider _provider;

                public CapturingLogger(CapturingLoggerProvider provider) => _provider = provider;

                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    _provider.Entries.Enqueue((logLevel, formatter(state, exception)));
                }
            }
        }

        // ---- unit half: the checker itself, all combinations ----

        private static TransportOptions Options(bool grpc, bool signalR) => new()
        {
            Grpc = { Enabled = grpc },
            SignalR = { Enabled = signalR },
        };

        private static (CapturingLoggerProvider Provider, ILogger Logger) Capture()
        {
            var provider = new CapturingLoggerProvider();
            return (provider, provider.CreateLogger("test"));
        }

        [Fact]
        public void Declared_but_disabled_transport_logs_an_error_naming_module_and_transport()
        {
            var (provider, logger) = Capture();
            var definition = new ModuleDefinition { Name = "NeedsRealtime", ModuleDefinitionName = "Acme.Realtime, Acme.Realtime.Stomme" };
            definition.Transports = "SignalR";

            TransportRequirements.LogUnsatisfied(new[] { definition }, Options(grpc: true, signalR: false), logger);

            var entry = Assert.Single(provider.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("NeedsRealtime", entry.Message);
            Assert.Contains("Acme.Realtime, Acme.Realtime.Stomme", entry.Message);
            Assert.Contains("SignalR", entry.Message);
        }

        [Fact]
        public void Declared_and_enabled_transport_logs_nothing()
        {
            var (provider, logger) = Capture();
            var definition = new ModuleDefinition { Name = "NeedsBoth" };
            definition.Transports = "Grpc,SignalR";

            TransportRequirements.LogUnsatisfied(new[] { definition }, Options(grpc: true, signalR: true), logger);

            Assert.Empty(provider.Entries);
        }

        [Fact]
        public void Module_with_no_declaration_logs_nothing_even_with_all_transports_off()
        {
            var (provider, logger) = Capture();

            // The stock shape: every framework module declares nothing. This is the byte-for-byte-unchanged
            // guarantee for a stock install.
            TransportRequirements.LogUnsatisfied(new[] { new ModuleDefinition { Name = "Stock" } }, Options(grpc: false, signalR: false), logger);

            Assert.Empty(provider.Entries);
        }

        [Fact]
        public void Unknown_transport_name_is_an_error_not_silently_satisfied()
        {
            var (provider, logger) = Capture();
            var definition = new ModuleDefinition { Name = "Typo" };
            definition.Transports = "WebSockets";

            TransportRequirements.LogUnsatisfied(new[] { definition }, Options(grpc: true, signalR: true), logger);

            var entry = Assert.Single(provider.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("Typo", entry.Message);
            Assert.Contains("WebSockets", entry.Message);
        }

        // ---- end-to-end half: the real MapStommeTransport path, real module discovery ----

        [Fact]
        public async Task Real_startup_path_logs_the_error_for_a_declared_but_disabled_transport()
        {
            TransportDeclaringTestModule.DeclaredTransports = "Grpc";
            try
            {
                var provider = await RunMapStommeTransportAsync(grpc: false, signalR: true);

                Assert.Contains(provider.Entries, entry =>
                    entry.Level == LogLevel.Error &&
                    entry.Message.Contains("TransportDeclaringTestModule") &&
                    entry.Message.Contains("Grpc"));
            }
            finally
            {
                TransportDeclaringTestModule.DeclaredTransports = "";
            }
        }

        [Fact]
        public async Task Real_startup_path_stays_silent_for_a_declared_and_enabled_transport()
        {
            TransportDeclaringTestModule.DeclaredTransports = "SignalR";
            try
            {
                var provider = await RunMapStommeTransportAsync(grpc: false, signalR: true);

                Assert.DoesNotContain(provider.Entries, entry =>
                    entry.Message.Contains("TransportDeclaringTestModule"));
            }
            finally
            {
                TransportDeclaringTestModule.DeclaredTransports = "";
            }
        }

        private static async Task<CapturingLoggerProvider> RunMapStommeTransportAsync(bool grpc, bool signalR)
        {
            var provider = new CapturingLoggerProvider();

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(provider);
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transport:Grpc:Enabled"] = grpc.ToString().ToLowerInvariant(),
                ["Transport:SignalR:Enabled"] = signalR.ToString().ToLowerInvariant(),
            });
            builder.Services.AddRouting();
            builder.Services.AddStommeTransport(builder.Configuration);

            await using var app = builder.Build();
            app.UseRouting();
            app.UseStommeTransport(builder.Configuration);
            app.MapStommeTransport(builder.Configuration);

            // MapStommeTransport runs at mapping time — no request needed; starting the app just proves
            // the mapped pipeline is valid.
            await app.StartAsync();
            await app.StopAsync();

            return provider;
        }
    }
}
