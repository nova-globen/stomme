#nullable enable
using System;
using System.Linq;
using Stomme.AiAssistant;
using FluentValidation;
using Microsoft.AspNetCore.Http;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// The ONE mapping from what the assistant refuses to what a caller is told - used by the API and by
    /// the panel's server-side service alike, so a circuit and a WebAssembly client show the same thing. Only these exceptions
    /// are described, and their messages are written to be shown; anything else is not a refusal and is not echoed.
    /// </summary>
    public static class AssistantRefusals
    {
        public sealed record Refusal(int Status, AssistantRefusalView View);

        public static Refusal? Describe(Exception exception) => exception switch
        {
            AssistantGuardrailException refused => new(
                refused.Reason switch
                {
                    AssistantGuardrailReason.RateLimitExceeded => StatusCodes.Status429TooManyRequests,
                    AssistantGuardrailReason.Unauthorized => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status400BadRequest,
                },
                new AssistantRefusalView(refused.Reason.ToString(), refused.Message)),
            AssistantUnavailableException unavailable => new(
                unavailable.Reason switch
                {
                    AssistantUnavailableReason.QuotaExceeded => StatusCodes.Status429TooManyRequests,
                    AssistantUnavailableReason.MeteringUnavailable => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status409Conflict,
                },
                new AssistantRefusalView(unavailable.Reason.ToString(), unavailable.Message)),
            AssistantConflictException conflict => new(StatusCodes.Status409Conflict, new AssistantRefusalView("Conflict", conflict.Message)),
            ValidationException validation => new(StatusCodes.Status400BadRequest, new AssistantRefusalView("Invalid", string.Join(" ", validation.Errors.Select(failure => failure.ErrorMessage)))),
            _ => null,
        };
    }
}
