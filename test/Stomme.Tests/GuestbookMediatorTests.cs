using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Guestbook.Persistence;
using Stomme.Modules.Guestbook.Application;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests
{
    /// <summary>
    /// Drives the Guestbook module's CQRS pipeline with the REAL mediator, the REAL four pipeline
    /// behaviors, the REAL handlers, and the REAL framework unit-of-work / domain-event dispatcher — only
    /// persistence is a fake in-memory repository, so the test is deterministic and needs no database. This
    /// proves the mediator is functional end to end: a validated command round-trips, an invalid command is
    /// REJECTED by the ValidationBehavior, a query returns the written data, a stream yields it, the mediator
    /// notification fires AND the framework domain-event handler fires (the two coexisting in-process paths),
    /// and the logging + performance behaviors are observable.
    /// </summary>
    public class GuestbookMediatorTests
    {
        [Fact]
        public async Task Valid_command_round_trips_and_fires_both_dispatch_paths_and_all_behaviors()
        {
            var logs = new ConcurrentQueue<string>();
            await using var provider = BuildProvider(logs);
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var created = await mediator.Send(new SignGuestbookCommand(42, "Ada Lovelace", "First to compute."));

            Assert.NotEqual(Guid.Empty, created.Id);
            Assert.Equal(42, created.ModuleId);
            Assert.Equal("Ada Lovelace", created.Author);

            // The query (also through the mediator) returns the written signature.
            var listed = await mediator.Send(new ListGuestbookEntriesQuery(42));
            Assert.Single(listed);
            Assert.Equal("Ada Lovelace", listed[0].Author);

            var log = string.Join("\n", logs);
            // All four behaviors ran: exception (silent on success), logging (brackets), performance (timing).
            Assert.Contains("[Mediator] Handling SignGuestbookCommand", log);
            Assert.Contains("[Mediator] Handled SignGuestbookCommand", log);
            // The performance behavior logs ONE of two lines depending on WALL-CLOCK time (500 ms threshold), and the
            // first, cold mediator call inside a loaded full gate crossed it (measured 2026-09-23: "took ... ms (slow)"
            // instead of "completed in"). What this asserts is that the behavior RAN, so either line is the answer.
            Assert.True(log.Contains("SignGuestbookCommand completed in", StringComparison.Ordinal)
                        || log.Contains("SignGuestbookCommand took", StringComparison.Ordinal),
                "the performance behavior logged nothing for SignGuestbookCommand:\n" + log);
            // Both in-process fan-out paths fired on the one signature (D74 keeps them coexisting).
            Assert.Contains("MEDIATOR INotification", log);
            Assert.Contains("FRAMEWORK domain event", log);
        }

        [Fact]
        public async Task Invalid_command_is_rejected_by_the_validation_behavior()
        {
            var logs = new ConcurrentQueue<string>();
            await using var provider = BuildProvider(logs);
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            // Empty author violates the validator; the ValidationBehavior must throw BEFORE the handler runs.
            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                mediator.Send(new SignGuestbookCommand(42, "   ", "A message")));

            Assert.Contains(ex.Errors, failure => failure.PropertyName == nameof(SignGuestbookCommand.Author));

            // The handler never ran, so neither dispatch path fired for the rejected command.
            var log = string.Join("\n", logs);
            Assert.DoesNotContain("MEDIATOR INotification", log);
            Assert.DoesNotContain("FRAMEWORK domain event", log);
        }

        [Fact]
        public async Task Stream_request_yields_the_written_entries()
        {
            var logs = new ConcurrentQueue<string>();
            await using var provider = BuildProvider(logs);
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            await mediator.Send(new SignGuestbookCommand(7, "Grace Hopper", "A message."));
            await mediator.Send(new SignGuestbookCommand(7, "Alan Turing", "Another message."));

            var streamed = new List<WireModel>();
            await foreach (var entry in mediator.CreateStream(new StreamGuestbookEntriesQuery(7)))
            {
                streamed.Add(entry);
            }

            Assert.Equal(2, streamed.Count);
            Assert.Contains(streamed, entry => entry.Author == "Grace Hopper");
            Assert.Contains(streamed, entry => entry.Author == "Alan Turing");
        }

        private sealed class FixedTenantContext : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;

            public bool IsSystemScope => false;
        }

        private static ServiceProvider BuildProvider(ConcurrentQueue<string> logs)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(new CapturingLoggerProvider(logs));
            });

            // Real framework unit-of-work + current-scope domain-event dispatcher + tracker (all
            // context-agnostic), plus the framework domain-event handler.
            services.AddScoped<IAggregateTracker, AggregateTracker>();
            services.AddScoped<IDomainEventDispatcher, CurrentScopeDomainEventDispatcher>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IDomainEventHandler<GuestbookEntrySignedDomainEvent>, GuestbookEntrySignedDomainEventHandler>();

            // The persistence fake: it enrolls the aggregate in the ambient tracker exactly as EfRepository
            // does in the no-serializer (in-process dispatch) mode, so the unit of work has something to drain.
            services.AddScoped<IGuestbookRepository, FakeGuestbookRepository>();
            services.AddScoped<ITenantContext, FixedTenantContext>(); // The handler stamps the ambient tenant

            // The mediator, live, with the four behaviors in the module's deliberate order (registration order
            // == outer-to-inner execution): exception -> logging -> performance -> validation -> handler.
            services.AddMediator();
            services.AddMediatorBehavior(typeof(ExceptionBehavior<,>));
            services.AddMediatorBehavior(typeof(LoggingBehavior<,>));
            services.AddMediatorBehavior(typeof(PerformanceBehavior<,>));
            services.AddMediatorBehavior(typeof(ValidationBehavior<,>));

            services.AddScoped<IRequestHandler<SignGuestbookCommand, WireModel>, SignGuestbookCommandHandler>();
            services.AddScoped<IRequestHandler<ListGuestbookEntriesQuery, IReadOnlyList<WireModel>>, ListGuestbookEntriesQueryHandler>();
            services.AddScoped<IStreamRequestHandler<StreamGuestbookEntriesQuery, WireModel>, StreamGuestbookEntriesQueryHandler>();
            services.AddScoped<INotificationHandler<GuestbookSignedNotification>, GuestbookSignedNotificationHandler>();
            services.AddScoped<IValidator<SignGuestbookCommand>, SignGuestbookCommandValidator>();

            return services.BuildServiceProvider(validateScopes: true);
        }

        /// <summary>An in-memory <see cref="IGuestbookRepository"/> that mimics EfRepository's track-on-add.</summary>
        private sealed class FakeGuestbookRepository(IAggregateTracker tracker) : IGuestbookRepository
        {
            private readonly List<GuestbookEntryAggregate> _entries = new();

            public Task<GuestbookEntryAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
                Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

            public Task<IReadOnlyList<GuestbookEntryAggregate>> GetAllAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<GuestbookEntryAggregate>>(_entries.ToList());

            public Task AddAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default)
            {
                _entries.Add(aggregate);
                tracker.Track(aggregate);
                return Task.CompletedTask;
            }

            public Task UpdateAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default)
            {
                tracker.Track(aggregate);
                return Task.CompletedTask;
            }

            public Task DeleteAsync(GuestbookEntryAggregate aggregate, CancellationToken ct = default)
            {
                _entries.Remove(aggregate);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<GuestbookEntryAggregate>> GetByModuleAsync(int moduleId, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<GuestbookEntryAggregate>>(
                    _entries.Where(e => e.ModuleId == moduleId).OrderByDescending(e => e.SignedOnUtc).ToList());
        }

        private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

            public void Dispose()
            {
            }

            private sealed class CapturingLogger(ConcurrentQueue<string> logs) : ILogger
            {
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter) => logs.Enqueue(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }
}
