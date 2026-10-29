using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Mediator.Behaviors;
using Stomme.Persistence.Abstractions;
using Stomme.Security.Abstractions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A refused request is not a fault. The mediator's exception boundary logged a duplicate title, a stale write or
    /// a denied key at Error with its stack (reported four times by the app-guidance writers); an
    /// <see cref="IExpectedRefusal"/>, and the framework's own refusals, are now logged at Information and re-thrown. A real
    /// fault still logs at Error. And a validation failure or a stale write escaping a controller is a 400 / 409, not a 500.
    /// </summary>
    public sealed class RequestRefusalTests
    {
        private sealed record Probe : IRequest<string>;

        private sealed class DuplicateTitleException(string message) : Exception(message), IExpectedRefusal;

        public static IEnumerable<object[]> Refusals() => new[]
        {
            new object[] { new DuplicateTitleException("A note titled 'x' already exists.") },
            new object[] { new AccessDeniedException("notes:edit") },
            new object[] { new ConcurrencyConflictException("Note", "42") },
            new object[] { new ValidationException("title is required") },
        };

        [Theory]
        [MemberData(nameof(Refusals))]
        public async Task A_refusal_is_rethrown_and_not_logged_as_an_error(Exception refusal)
        {
            var logger = new RecordingLogger();
            var behavior = new ExceptionBehavior<Probe, string>(logger);

            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => behavior.Handle(new Probe(), () => Task.FromException<string>(refusal), CancellationToken.None));

            Assert.Same(refusal, thrown);
            Assert.Empty(logger.Errors);
        }

        [Fact]
        public async Task A_fault_is_still_logged_as_an_error()
        {
            var logger = new RecordingLogger();
            var behavior = new ExceptionBehavior<Probe, string>(logger);

            await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(new Probe(), () => Task.FromException<string>(new InvalidOperationException("a bug")), CancellationToken.None));

            Assert.Single(logger.Errors);
        }

        [Fact]
        public async Task A_validation_failure_escaping_an_action_is_a_400_with_each_fields_messages()
        {
            var context = Context(new ValidationException(new[] { new ValidationFailure("Title", "Title is required."), new ValidationFailure("Title", "Title is too long."), new ValidationFailure("Body", "Body is too long.") }));

            await new RequestRefusalExceptionFilter().OnExceptionAsync(context);

            Assert.True(context.ExceptionHandled);
            var result = Assert.IsType<BadRequestObjectResult>(context.Result);
            var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
            Assert.Equal(new[] { "Title is required.", "Title is too long." }, problem.Errors["Title"]);
            Assert.Equal(new[] { "Body is too long." }, problem.Errors["Body"]);
        }

        [Fact]
        public async Task A_stale_write_escaping_an_action_is_a_409_and_anything_else_is_left_alone()
        {
            var conflict = Context(new ConcurrencyConflictException("Note", "42"));
            await new RequestRefusalExceptionFilter().OnExceptionAsync(conflict);
            Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ConflictObjectResult>(conflict.Result).StatusCode);

            var fault = Context(new InvalidOperationException("a bug"));
            await new RequestRefusalExceptionFilter().OnExceptionAsync(fault);
            Assert.False(fault.ExceptionHandled);
            Assert.Null(fault.Result);
        }

        private static ExceptionContext Context(Exception exception) =>
            new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = exception };

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
