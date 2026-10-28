#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Variables (an Enterprise feature there): values every pipeline on the site reads in its expressions as
    /// <c>{{ $vars.name }}</c> - an API's base address, a team's e-mail, a threshold - set once for the site by its authors. Not for
    /// secrets: a value is shown to whoever may view the site's pipelines; a secret belongs in a connection. The one rule for both sides.
    /// </summary>
    public static class PipelineSiteVariables
    {
        public const int MaxVariables = 100;

        public const int MaxNameLength = 50;

        public const int MaxValueLength = 1000;

        /// <summary>A name: a letter or '_', then letters, digits or '_' - what <c>$vars.name</c> can read.</summary>
        public static bool IsName(string? name) =>
            !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        /// <summary>Whether the variables may be kept; the reason when not.</summary>
        public static bool Check(IReadOnlyDictionary<string, string> variables, out string? error)
        {
            error = null;
            if (variables.Count > MaxVariables)
            {
                error = $"A site keeps at most {MaxVariables} variables.";
            }
            else if (variables.Keys.FirstOrDefault(name => !IsName(name)) is { } bad)
            {
                error = $"'{bad}' cannot be a variable's name: a letter or '_', then letters, digits or '_', at most {MaxNameLength}.";
            }
            else if (variables.FirstOrDefault(pair => (pair.Value ?? "").Length > MaxValueLength) is { Key: not null } longValue)
            {
                error = $"'{longValue.Key}' is over {MaxValueLength} characters.";
            }

            return error is null;
        }
    }
}
