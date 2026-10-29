using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Modules.Guestbook.Application;
using Stomme.Server;
using Stomme.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;
using Stomme.Infrastructure.Tenancy;
using Stomme.Tenancy.Abstractions;
using Stomme.Persistence.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Drives the Guestbook sample module on the REAL composed host (the production DI container and
    /// middleware pipeline, transport swapped for TestServer), proving the mediator is LIVE end to end:
    /// <list type="number">
    ///   <item>the module is UNSEEDED from the default template (W5/#66) — absent from the home page,
    ///     while the host still serves the installed site;</item>
    ///   <item>a command dispatched through the LIVE <see cref="IMediator"/> persists to the module's real EF
    ///     store, and BOTH in-process fan-out paths (the mediator notification and the framework domain-event
    ///     dispatcher) fire, with the logging/performance behaviors observable in the host log;</item>
    ///   <item>the <c>ValidationBehavior</c> REJECTS an invalid command on the real host;</item>
    ///   <item>the CQRS query returns the written data over HTTP through the controller.</item>
    /// </list>
    /// Runs on SQLite (always available); the command is driven in-process because the host globally requires
    /// an antiforgery token on unsafe HTTP methods, while the query (a GET) is driven over HTTP.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class GuestbookModuleHostTests
    {
        private const int ModuleId = 777;
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public GuestbookModuleHostTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Guestbook_is_unseeded_but_its_cqrs_flows_are_live_on_the_host()
        {
            var logs = new ConcurrentQueue<string>();
            using var scratch = new ScratchInstall();

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Logging.AddProvider(new SentinelLoggerProvider(logs));
                });

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException("The installer did not finish in time.");
                }

                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();
                using var client = app.GetTestClient();

                // ---- 1. The host serves the installed site; Guestbook is UNSEEDED (W5/#66) --------------
                var home = await client.GetAsync("/");
                var html = await home.Content.ReadAsStringAsync();
                _output.WriteLine($"GET / -> {(int)home.StatusCode}, {html.Length} chars");
                Assert.Equal(HttpStatusCode.OK, home.StatusCode);
                Assert.DoesNotContain("Installation Wizard", html, StringComparison.OrdinalIgnoreCase);
                // Guestbook is no longer seeded on the default template (W5/#66): it must be ABSENT from the
                // home page. Its live-on-host proof is the CQRS/mediator + HTTP-controller flow below, which
                // needs no page placement (the module renders when an operator adds it; that path is exercised
                // by the client component + the render guard's pipeline).
                Assert.DoesNotContain("Sign the guestbook", html, StringComparison.OrdinalIgnoreCase);

                // ---- 2. A command through the LIVE mediator persists, and both dispatch paths fire ------
                // A signature carries the tenant it is signed under; a request resolves it from the alias, and
                // this direct send names the host's first tenant the way a job's scope would.
                using var ambient = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(FrameworkTenantId.ToTenantId(1).Value);
                using (var scope = app.Services.CreateScope())
                {
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                    var created = await mediator.Send(
                        new SignGuestbookCommand(ModuleId, "Grace Hopper", "Compiled the first host test."));
                    Assert.NotEqual(Guid.Empty, created.Id);

                    var listed = await mediator.Send(new ListGuestbookEntriesQuery(ModuleId));
                    Assert.Contains(listed, entry => entry.Author == "Grace Hopper");

                    // ---- 3. The ValidationBehavior rejects an invalid command on the real host ----------
                    await Assert.ThrowsAsync<ValidationException>(() =>
                        mediator.Send(new SignGuestbookCommand(ModuleId, "   ", "no author")));
                }

                // ---- 4. The CQRS query returns the written data over HTTP through the controller --------
                var apiEntries = await client.GetFromJsonAsync<WireModel[]>($"/api/Guestbook?moduleid={ModuleId}");
                Assert.NotNull(apiEntries);
                Assert.Contains(apiEntries, entry => entry.Author == "Grace Hopper");

                // The behavior pipeline and BOTH dispatch paths were observed live on the host.
                var log = string.Join("\n", logs);
                Assert.Contains("[Mediator] Handling SignGuestbookCommand", log);
                Assert.Contains("SignGuestbookCommand completed in", log);
                Assert.Contains("MEDIATOR INotification", log);
                Assert.Contains("FRAMEWORK domain event", log);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }

        /// <summary>A throwaway content root + override settings file for a SQLite install, mirroring the render guard.</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-guestbook-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var connectionString = $"Data Source={Path.Combine(ContentRoot, "stomme-guestbook.db")};";
                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "Gu3st!Book$2026",
                        ["HostEmail"] = "guestbook@example.com",
                        ["HostName"] = "Host"
                    }
                };
                File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { }
            }
        }

        /// <summary>Captures only the Guestbook/mediator log lines, so the host's full log stream is not buffered.</summary>
        private sealed class SentinelLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new SentinelLogger(logs);

            public void Dispose() { }

            private sealed class SentinelLogger(ConcurrentQueue<string> logs) : ILogger
            {
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
                    Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    var message = formatter(state, exception);
                    if (message.Contains("[Mediator]", StringComparison.Ordinal) ||
                        message.Contains("[Guestbook]", StringComparison.Ordinal))
                    {
                        logs.Enqueue(message);
                    }
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
