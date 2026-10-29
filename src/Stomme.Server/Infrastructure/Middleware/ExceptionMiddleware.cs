using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stomme.Enums;
using Stomme.Extensions;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    internal class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;

        public ExceptionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context, IServiceProvider provider)
        {
            try
            {
                await _next(context);
            }
            catch (Exception exception)
            {
                Log(context, provider, exception);

                // An unhandled exception must NOT masquerade as an empty HTTP 200. The catch used to log
                // and return with the status untouched, so Kestrel emitted 200 with zero bytes — every API
                // consumer saw success on a server failure (an OAuth client got an empty 200 instead of
                // invalid_client; the OpenIddict store-provisioning throw and the prerender self-call failure both
                // hid behind it), and monitoring never saw a 5xx. If the response has not begun, surface a real
                // 500 so the failure is honest. If it has already begun (partway through a streamed render) the
                // status is locked in — the log above is then the only signal, which is the best we can do.
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }
        }

        // Logging must never itself throw out of the handler (e.g. the site-DB logger when the database is the
        // very thing that failed) — that would replace the 500 with an unhandled exception. Best-effort.
        private void Log(HttpContext context, IServiceProvider provider, Exception exception)
        {
            try
            {
                var endPoint = context.GetEndpoint()?.DisplayName;
                var alias = context.GetAlias();
                if (alias != null)
                {
                    var logger = provider.GetRequiredService<ILogManager>();
                    logger.Log(alias.SiteId, Shared.LogLevel.Error, this, LogFunction.Other, exception, "Unhandled Exception: {Error} For Endpoint: {Endpoint}", exception.Message, endPoint);
                }
                else
                {
                    var filelogger = provider.GetRequiredService<ILogger<ExceptionMiddleware>>();
                    filelogger.LogError(Utilities.LogMessage(this, $"Endpoint: {endPoint} Unhandled Exception: {exception}"));
                }
            }
            catch (Exception loggingFailure)
            {
                // Fall back to the framework logger so the original failure is not lost when site-DB logging is
                // itself unavailable.
                try
                {
                    provider.GetRequiredService<ILogger<ExceptionMiddleware>>()
                        .LogError(loggingFailure, "ExceptionMiddleware failed to log an unhandled exception: {Error}", exception.Message);
                }
                catch
                {
                    // Nothing left to do — never rethrow from the exception handler.
                }
            }
        }
    }
}
