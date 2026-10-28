using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-56 — AN UNHANDLED EXCEPTION MUST NOT MASQUERADE AS AN EMPTY HTTP 200.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ExceptionMiddleware</c> used to catch every unhandled exception, log it, and return with the
    /// response status untouched — so Kestrel emitted <c>200</c> with zero bytes. That made a server failure
    /// look like success to every caller: an OAuth client received an empty 200 instead of
    /// <c>invalid_client</c> (BL-55 RED drive), a failed prerender returned a blank 200 (BL-57), and monitoring
    /// never saw a 5xx. This suite pins the corrected contract: on an unhandled exception the middleware sets a
    /// real <c>500</c> when the response has not begun, leaves an already-started response's status alone (it
    /// cannot be changed), passes a successful request straight through, and NEVER rethrows — including when the
    /// logger itself throws.
    /// </para>
    /// </remarks>
    public class ExceptionMiddlewareTests
    {
        private static IServiceProvider Services(ILogManager? logManager = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILogger<ExceptionMiddleware>>(NullLogger<ExceptionMiddleware>.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            if (logManager != null)
            {
                services.AddSingleton(logManager);
            }
            return services.BuildServiceProvider();
        }

        private static DefaultHttpContext Context(IServiceProvider provider)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Response.Body = new MemoryStream();
            return context;
        }

        [Fact]
        public async Task Unhandled_exception_before_response_starts_becomes_500()
        {
            var provider = Services();
            var context = Context(provider);
            var middleware = new ExceptionMiddleware(_ => throw new InvalidOperationException("boom"));

            await middleware.Invoke(context, provider); // must NOT rethrow

            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        }

        [Fact]
        public async Task Successful_request_passes_through_untouched()
        {
            var provider = Services();
            var context = Context(provider);
            var middleware = new ExceptionMiddleware(async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                await Task.CompletedTask;
            });

            await middleware.Invoke(context, provider);

            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }

        [Fact]
        public async Task Exception_after_response_started_leaves_status_and_does_not_rethrow()
        {
            var provider = Services();
            var context = new DefaultHttpContext { RequestServices = provider };
            // A plain MemoryStream never flips HasStarted (that is a server-feature concern), so substitute a
            // response feature that reports HasStarted=true — the genuine "already streaming" state.
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature()); // defaults to 200
            var middleware = new ExceptionMiddleware(_ => throw new InvalidOperationException("boom mid-stream"));

            await middleware.Invoke(context, provider); // must NOT rethrow

            // Once the response has started the status is locked; the middleware must not touch it (a blind
            // Response.Clear()/StatusCode= would itself throw).
            Assert.True(context.Response.HasStarted);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        [Fact]
        public async Task Logging_failure_does_not_escape_the_handler()
        {
            // A site-DB logger that throws (e.g. the database is the very thing that failed) must not replace
            // the 500 with a new unhandled exception.
            var throwingLog = new ThrowingLogManager();
            var provider = Services(throwingLog);
            var context = Context(provider);
            // GetAlias reads the alias off HttpContext.Items; set one so the ILogManager path (not the file
            // logger) is taken and can throw.
            context.Items[Stomme.Shared.Constants.HttpContextAliasKey] = new Stomme.Models.Alias { SiteId = 1 };
            var middleware = new ExceptionMiddleware(_ => throw new InvalidOperationException("boom"));

            await middleware.Invoke(context, provider); // must NOT rethrow the logging failure

            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
            Assert.True(throwingLog.WasCalled);
        }

        // A response feature that reports the response as already begun, so ExceptionMiddleware's HasStarted
        // guard is genuinely exercised. Any attempt to mutate StatusCode after "start" throws, mirroring Kestrel.
        private sealed class StartedResponseFeature : IHttpResponseFeature
        {
            private int _statusCode = StatusCodes.Status200OK;

            public bool HasStarted => true;
            public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
            public Stream Body { get; set; } = new MemoryStream();
            public string? ReasonPhrase { get; set; }

            public int StatusCode
            {
                get => _statusCode;
                set => throw new InvalidOperationException("StatusCode cannot be set, the response has already started.");
            }

            public void OnStarting(Func<object, Task> callback, object state) { }
            public void OnCompleted(Func<object, Task> callback, object state) { }
        }

        private sealed class ThrowingLogManager : ILogManager
        {
            public bool WasCalled { get; private set; }

            public void Log(Stomme.Shared.LogLevel level, object @class, Stomme.Enums.LogFunction function, string message, params object[] args)
                => throw new InvalidOperationException("log store down");

            public void Log(Stomme.Shared.LogLevel level, object @class, Stomme.Enums.LogFunction function, Exception exception, string message, params object[] args)
                => throw new InvalidOperationException("log store down");

            public void Log(int siteId, Stomme.Shared.LogLevel level, object @class, Stomme.Enums.LogFunction function, string message, params object[] args)
                => throw new InvalidOperationException("log store down");

            public void Log(int siteId, Stomme.Shared.LogLevel level, object @class, Stomme.Enums.LogFunction function, Exception exception, string message, params object[] args)
            {
                WasCalled = true;
                throw new InvalidOperationException("log store down");
            }

            public void Log(Stomme.Models.Log log)
                => throw new InvalidOperationException("log store down");
        }
    }
}
