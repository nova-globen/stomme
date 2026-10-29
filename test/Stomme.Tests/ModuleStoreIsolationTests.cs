#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-263 (infra, 2026-10-29): every module aggregate on a relational install shares ONE EF Core model
    /// (<c>ModuleAggregatesDbContext</c>), so an aggregate that cannot be mapped must fail ONLY its own module - named in the
    /// log, its repository refusing with that name - while every other module on the store keeps reading and writing. A
    /// mappable aggregate is registered beside an unmappable one (a value object the convention does not support).
    /// </summary>
    public sealed class ModuleStoreIsolationTests : IAsyncLifetime
    {
        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-module-isolation-{Guid.NewGuid():N}.db")};Pooling=True";

        private readonly CapturingLoggerProvider _logs = new();
        private ServiceProvider _services = null!;

        /// <summary>A module's healthy aggregate.</summary>
        public sealed class IsolationHealthyProbe : AggregateRoot<Guid>
        {
            private IsolationHealthyProbe()
            {
            }

            public string Name { get; private set; } = string.Empty;

            public static IsolationHealthyProbe Create(string name) => new() { Id = Guid.NewGuid(), Name = name };
        }

        /// <summary>Another module's aggregate the shared store cannot map: a value object with no supported shape.</summary>
        public sealed class IsolationUnmappableProbe : AggregateRoot<Guid>
        {
            private IsolationUnmappableProbe()
            {
            }

            public UnsupportedShape Shape { get; private set; } = null!;
        }

        public sealed class UnsupportedShape : ValueObject
        {
            public int Width { get; private set; }

            protected override IEnumerable<object> GetEqualityComponents()
            {
                yield return Width;
            }
        }

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(_logs));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = _connectionString,
                })
                .Build());
            services.AddModuleAggregatePersistence<IsolationHealthyProbe, Guid>();
            services.AddModuleAggregatePersistence<IsolationUnmappableProbe, Guid>();
            _services = services.BuildServiceProvider();

            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            try
            {
                File.Delete(new SqliteConnectionStringBuilder(_connectionString).DataSource);
            }
            catch (IOException)
            {
                // a leftover temp file is not worth a red run
            }
        }

        [Fact]
        public async Task An_unmappable_aggregate_fails_only_its_own_module_named_and_the_others_keep_working()
        {
            // The healthy module writes and reads back, untouched by its neighbour's mapping failure.
            var probe = IsolationHealthyProbe.Create("still here");
            await using (var scope = _services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IRepository<IsolationHealthyProbe, Guid>>().AddAsync(probe);
            }

            await using (var scope = _services.CreateAsyncScope())
            {
                var read = await scope.ServiceProvider.GetRequiredService<IRepository<IsolationHealthyProbe, Guid>>().GetByIdAsync(probe.Id);
                Assert.Equal("still here", read?.Name);
            }

            // The broken module is refused by name - never an empty read that looks like no data.
            await using (var scope = _services.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IRepository<IsolationUnmappableProbe, Guid>>();
                var refusal = await Assert.ThrowsAnyAsync<Exception>(() => repository.GetByIdAsync(Guid.NewGuid()));
                Assert.Contains(nameof(IsolationUnmappableProbe), refusal.Message);
            }

            // And the log says which aggregate, and that only it is off.
            Assert.Contains(_logs.Messages, message => message.Contains(nameof(IsolationUnmappableProbe)) && message.Contains("cannot be mapped", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(_logs.Messages, message => message.Contains("will read as empty", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Two modules' aggregates with one class name - one table in the shared store, which EF cannot build.</summary>
        public static class ModuleA
        {
            public sealed class Clash : AggregateRoot<Guid>
            {
                private Clash()
                {
                }
            }
        }

        public static class ModuleB
        {
            public sealed class Clash : AggregateRoot<Guid>
            {
                private Clash()
                {
                }
            }
        }

        [Fact]
        public async Task Two_module_aggregates_with_one_name_are_both_refused_and_the_others_keep_working()
        {
            var connection = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-module-clash-{Guid.NewGuid():N}.db")};Pooling=True";
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(_logs));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = connection,
                })
                .Build());
            services.AddModuleAggregatePersistence<IsolationHealthyProbe, Guid>();
            services.AddModuleAggregatePersistence<ModuleA.Clash, Guid>();
            services.AddModuleAggregatePersistence<ModuleB.Clash, Guid>();
            await using var provider = services.BuildServiceProvider();
            try
            {
                foreach (var hosted in provider.GetServices<IHostedService>())
                {
                    await hosted.StartAsync(CancellationToken.None);
                }

                var probe = IsolationHealthyProbe.Create("beside a clash");
                await using (var scope = provider.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<IRepository<IsolationHealthyProbe, Guid>>().AddAsync(probe);
                    Assert.Equal("beside a clash", (await scope.ServiceProvider.GetRequiredService<IRepository<IsolationHealthyProbe, Guid>>().GetByIdAsync(probe.Id))?.Name);
                }

                await using (var scope = provider.CreateAsyncScope())
                {
                    var a = await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<IRepository<ModuleA.Clash, Guid>>().GetAllAsync());
                    var b = await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<IRepository<ModuleB.Clash, Guid>>().GetAllAsync());
                    Assert.Contains("also named 'Clash'", a.Message);
                    Assert.Contains("also named 'Clash'", b.Message);
                }
            }
            finally
            {
                SqliteConnection.ClearPool(new SqliteConnection(connection));
                try
                {
                    File.Delete(new SqliteConnectionStringBuilder(connection).DataSource);
                }
                catch (IOException)
                {
                    // a leftover temp file is not worth a red run
                }
            }
        }

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            private readonly List<string> _messages = new();

            public IReadOnlyList<string> Messages
            {
                get
                {
                    lock (_messages)
                    {
                        return _messages.ToList();
                    }
                }
            }

            public ILogger CreateLogger(string categoryName) => new Capturing(this);

            public void Dispose()
            {
            }

            private sealed class Capturing(CapturingLoggerProvider owner) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (owner._messages)
                    {
                        owner._messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message));
                    }
                }
            }
        }
    }
}
