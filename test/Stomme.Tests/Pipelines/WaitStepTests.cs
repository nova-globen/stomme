#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Wait, n8n's Wait node, through the engine as the host composes it: a run that reaches a Wait is STORED, waiting,
    /// with a bookmark that carries its moment; the due-waits task leaves it alone until then and resumes it after, so the step
    /// after the wait runs - once, a second tick finding nothing. A moment already past goes straight on; a wait past
    /// Pipelines:Waits:MaxDays, or one that names no time, fails the step saying why. Durations are read as people write them.
    /// </summary>
    public sealed class WaitStepTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        private readonly PipelineTriggerOptions _options = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new Captured(_logged)).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<DueWaitsTask>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logged = new();

        private sealed class Captured(System.Collections.Concurrent.ConcurrentQueue<string> logged) : Microsoft.Extensions.Logging.ILoggerProvider
        {
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(categoryName, logged);

            public void Dispose()
            {
            }

            private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<string> logged) : Microsoft.Extensions.Logging.ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Debug;

                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    logged.Enqueue(logLevel + " " + category.Split('.').Last() + ": " + formatter(state, exception) + (exception is null ? "" : " !! " + exception.GetType().Name + ": " + exception.Message));
            }
        }

        private sealed class Author : Stomme.Security.Abstractions.IAccessGuard
        {
            public Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default) => Task.FromResult(true);

            public Task<bool> IsAllowedAsync(System.Reflection.MemberInfo target, CancellationToken ct = default) => Task.FromResult(true);
        }

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

        // A webhook starts it, the Wait waits, a line is written after.
        private async Task<string> PublishAsync(IServiceProvider services, string hook, Wait wait)
        {
            var trigger = new Webhook { WebhookName = new(hook), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            wait.Id = "wait";
            var after = new WriteLine("after the wait " + hook) { Id = "after" };
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            // The flowchart's id is fixed, as the editor's codec writes one: a step's node id carries its parent's, and a run is
            // resumed by node id - an id made anew at each load left the waiting step's context unfound (measured).
            var definition = await publisher.NewAsync(new Flowchart
            {
                Id = "flow",
                Steps = { trigger, wait, after },
                Connections = { new Connection(new Endpoint(trigger), new Endpoint(wait)), new Connection(new Endpoint(wait), new Endpoint(after)) },
            });
            definition.Name = "Waits " + hook;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private static async Task<PipelineInstance> RunOf(IServiceProvider services, string definitionId) =>
            Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));

        [Fact]
        public async Task A_run_waits_stored_until_its_moment_and_the_task_resumes_it_once()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "later", new Wait { For = new("2 hours") });
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("later"));

            // Stored, waiting, its bookmark carrying the moment.
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, definitionId)).Status);
            Assert.DoesNotContain("after the wait later", _console.ToString());
            var bookmark = Assert.Single(await services.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter { Name = Wait.StepTypeName }));
            Assert.Equal(_clock.UtcNow.AddHours(2), Wait.DueOf(bookmark.Payload));

            // The editor sees the step WAITING (parked on its bookmark), not running.
            var run = await RunOf(services, definitionId);
            var steps = await new Stomme.Modules.Pipelines.Application.ListRunStepsQueryHandler(services.GetRequiredService<IStepExecutionStore>(), services.GetRequiredService<IStepRegistry>(), new Author())
                .Handle(new Stomme.Modules.Pipelines.Application.ListRunStepsQuery(run.Id), CancellationToken.None);
            Assert.True(Assert.Single(steps, step => step.StepId == "wait").Waiting);

            // Not yet: nothing moves.
            var task = services.GetRequiredService<DueWaitsTask>();
            _clock.UtcNow = _clock.UtcNow.AddMinutes(119);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Running, (await RunOf(services, definitionId)).Status);

            // Due: the run goes on to the line after the wait, and a second tick finds nothing left to resume.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, definitionId)).Status);
            Assert.Contains(_logged, line => line.Contains("DueWaitsTask: A wait on tenant", StringComparison.Ordinal));
            var after = await new Stomme.Modules.Pipelines.Application.ListRunStepsQueryHandler(services.GetRequiredService<IStepExecutionStore>(), services.GetRequiredService<IStepRegistry>(), new Author())
                .Handle(new Stomme.Modules.Pipelines.Application.ListRunStepsQuery(run.Id), CancellationToken.None);
            Assert.All(after, step => Assert.False(step.Waiting));
            Assert.Single(_console.ToString().Split("after the wait later").Skip(1));
            Assert.Empty(await services.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter { Name = Wait.StepTypeName }));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Single(_console.ToString().Split("after the wait later").Skip(1));
        }

        [Fact]
        public async Task A_moment_already_past_goes_straight_on_and_a_wait_too_long_or_untold_fails_the_step()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var sender = services.GetRequiredService<IStimulusSender>();

            var past = await PublishAsync(services, "past", new Wait { Until = new("2026-10-01T00:00:00Z") });
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("past"));
            Assert.Equal(PipelineStatus.Finished, (await RunOf(services, past)).Status);
            Assert.Contains("after the wait past", _console.ToString());

            _options.Waits.MaxDays = 7;
            var tooLong = await PublishAsync(services, "toolong", new Wait { For = new("2 weeks") });
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("toolong"));
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOf(services, tooLong)).SubStatus);
            _options.Waits.MaxDays = new WaitOptions().MaxDays;

            var untold = await PublishAsync(services, "untold", new Wait());
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("untold"));
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOf(services, untold)).SubStatus);
            Assert.DoesNotContain("after the wait toolong", _console.ToString());
            Assert.DoesNotContain("after the wait untold", _console.ToString());
        }

        [Theory]
        [InlineData("30 minutes", 30 * 60)]
        [InlineData("1.5 hours", 90 * 60)]
        [InlineData("45s", 45)]
        [InlineData("2 days", 2 * 86400)]
        [InlineData("1 week", 7 * 86400)]
        [InlineData("1.12:00:00", 36 * 3600)]
        [InlineData(" 00:05:00 ", 300)]
        public void A_duration_is_read_as_people_write_it(string text, int seconds)
        {
            Assert.True(PipelineWaits.TryParseDuration(text, out var duration, out var error), error);
            Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
        }

        [Fact]
        public void What_is_not_a_duration_or_a_moment_says_how_to_write_it_and_the_due_moment_is_read_back()
        {
            Assert.False(PipelineWaits.TryParseDuration("two hours", out _, out var error));
            Assert.Contains("30 minutes", error);
            Assert.False(PipelineWaits.TryParseDuration("-00:05:00", out _, out _));

            var now = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
            Assert.Equal(new DateTimeOffset(2026, 12, 24, 9, 0, 0, TimeSpan.Zero), PipelineWaits.ResumeAt("2026-12-24T09:00:00", "1 hour", now, out _)); // Until wins; no offset is UTC
            Assert.Equal(now.AddHours(1), PipelineWaits.ResumeAt(null, "1 hour", now, out _));
            Assert.Null(PipelineWaits.ResumeAt("Christmas", null, now, out error));
            Assert.Contains("2026-12-24T09:00:00Z", error);
            Assert.Null(PipelineWaits.ResumeAt(" ", "", now, out error));
            Assert.Contains("For", error);

            var due = new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero);
            Assert.Equal(due, Wait.DueOf(new WaitStimulus("2026-10-06T11:00:00.0000000Z")));
            Assert.Equal(due, Wait.DueOf(JsonSerializer.SerializeToElement(new { resumeAt = "2026-10-06T11:00:00Z" })));
            Assert.Equal(due, Wait.DueOf(JsonSerializer.SerializeToElement(new WaitStimulus("2026-10-06T11:00:00Z"))));
            Assert.Null(Wait.DueOf(null));
        }
    }
}
