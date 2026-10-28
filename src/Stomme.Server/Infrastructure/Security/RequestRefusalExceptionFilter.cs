#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// BL-251 - the two refusals every module meets, answered for an action that does not catch them itself: a
    /// <see cref="ValidationException"/> (a command the mediator's validator rejected) becomes a <b>400</b>
    /// <see cref="ValidationProblemDetails"/> with each field's messages, and a <see cref="ConcurrencyConflictException"/>
    /// (a write over a newer version) a <b>409</b>. Before, both escaped as a 500 with nothing for the caller to show. An
    /// action that catches them itself - the module pattern - is unchanged, and the mediator has already logged them as
    /// refusals, not faults; neither is logged again here.
    /// </summary>
    public sealed class RequestRefusalExceptionFilter : IAsyncExceptionFilter
    {
        public Task OnExceptionAsync(ExceptionContext context)
        {
            switch (context.Exception)
            {
                case ValidationException validation:
                    var errors = validation.Errors
                        .GroupBy(error => string.IsNullOrEmpty(error.PropertyName) ? string.Empty : error.PropertyName)
                        .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).ToArray());
                    context.Result = new BadRequestObjectResult(new ValidationProblemDetails(errors)
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Instance = context.HttpContext.Request.Path,
                    });
                    context.ExceptionHandled = true;
                    break;
                case ConcurrencyConflictException conflict:
                    context.Result = new ConflictObjectResult(new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "Conflict",
                        Detail = conflict.Message,
                        Instance = context.HttpContext.Request.Path,
                    });
                    context.ExceptionHandled = true;
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
