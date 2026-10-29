#nullable enable
using System.Threading.Tasks;
using Stomme.Enums;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Turns an <see cref="AccessDeniedException"/> escaping a controller action — a <c>[Requires]</c>-gated
    /// mediator request the <c>AuthorizationBehavior</c> refused — into a <b>403</b> <see cref="ProblemDetails"/>
    /// response. Before this filter a denial surfaced as HTTP 500 through the generic exception
    /// path (F5). Logged at Error under the Security function, the way <c>PermissionHandler</c> logs a
    /// denied policy, so a denial is visible in the site's log like any other.
    /// </summary>
    public sealed class AccessDeniedExceptionFilter : IAsyncExceptionFilter
    {
        private readonly ILogManager _logger;

        public AccessDeniedExceptionFilter(ILogManager logger)
        {
            _logger = logger;
        }

        public Task OnExceptionAsync(ExceptionContext context)
        {
            if (context.Exception is not AccessDeniedException exception)
            {
                return Task.CompletedTask;
            }

            _logger.Log(SecurityRefusals.Level(context.HttpContext.User), this, LogFunction.Security, "User {User} Was Denied Access: {Message}", context.HttpContext.User?.Identity?.Name, exception.Message);

            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = exception.Message,
                Instance = context.HttpContext.Request.Path,
            })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            context.ExceptionHandled = true;

            return Task.CompletedTask;
        }
    }
}
