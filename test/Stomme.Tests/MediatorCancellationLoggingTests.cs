using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A request its caller cancelled is not logged as a fault by the mediator's exception boundary.
    /// </summary>
    /// <remarks>
    /// Found driving BL-206 on a WebAssembly scratch site: navigating away from a dashboard landing page while its
    /// query ran aborted the request, EF Core's connection open threw <see cref="TaskCanceledException"/>, and the host's log
    /// carried "fail: [Mediator] Unhandled exception while processing GetDashboardQuery" with the stack - an error for every
    /// visitor who clicks on before a page has loaded. A cancellation the request's own token asked for is re-thrown untouched
    /// for the host to answer; one nobody asked for is still a fault and is still logged.
    /// </remarks>
    public sealed class MediatorCancellationLoggingTests
    {
        private sealed record Probe : IRequest<string>;

        [Fact]
        public async Task A_cancellation_the_caller_asked_for_is_rethrown_and_not_logged_as_an_error()
        {
            var logger = new RecordingLogger();
            var behavior = new ExceptionBehavior<Probe, string>(logger);
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                behavior.Handle(new Probe(), () => Task.FromException<string>(new TaskCanceledException()), aborted.Token));

            Assert.Empty(logger.Errors);
        }

        [Fact]
        public async Task A_cancellation_nobody_asked_for_is_still_logged_as_a_fault()
        {
            var logger = new RecordingLogger();
            var behavior = new ExceptionBehavior<Probe, string>(logger);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                behavior.Handle(new Probe(), () => Task.FromException<string>(new TaskCanceledException()), CancellationToken.None));

            Assert.Single(logger.Errors);
        }

        private sealed class RecordingLogger : ILogger<ExceptionBehavior<Probe, string>>
        {
            public List<string> Errors { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    Errors.Add(formatter(state, exception));
                }
            }
        }
    }
}
