#nullable enable
using System;
using System.Security.Claims;
using Stomme.Enums;
using LogLevel = Stomme.Shared.LogLevel;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// How a refused request is logged (owner's decision, 2026-10-10, D99a): refused to an ANONYMOUS caller it is a Warning -
    /// in the site's log, never mailed to its hosts, so a crawler probing the APIs no longer spends the mail relay's day; refused
    /// to a SIGNED-IN user it stays an Error and is mailed (once an hour per kind, <see cref="LogNotificationThrottle"/>) -
    /// a wrong role or an account probing what it cannot reach is worth knowing about. Every other security entry keeps its
    /// level.
    /// </summary>
    public static class SecurityRefusals
    {
        /// <summary>The level of a refusal of <paramref name="user"/>'s request.</summary>
        public static LogLevel Level(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true ? LogLevel.Error : LogLevel.Warning;

        /// <summary>
        /// The level an entry is logged at: an inherited refusal of an anonymous caller is a Warning; anything else as it was
        /// asked for. The inherited refusals are written at Error in two shapes, measured on 2026-10-10: a Security entry that
        /// begins "Unauthorized " (159 - "Unauthorized User Get Attempt ...") and the settings controller's "User Not Authorized
        /// To ..." under its Read, Create, Update and Delete functions (8) - an anonymous caller reaches both.
        /// </summary>
        public static LogLevel LevelFor(LogLevel asked, LogFunction function, string? template, ClaimsPrincipal? user) =>
            asked == LogLevel.Error && IsRefusal(function, template) ? Level(user) : asked;

        private static bool IsRefusal(LogFunction function, string? template) =>
            template is not null
            && ((function == LogFunction.Security && template.StartsWith("Unauthorized ", StringComparison.Ordinal))
                || template.Contains("User Not Authorized To ", StringComparison.Ordinal));
    }
}
