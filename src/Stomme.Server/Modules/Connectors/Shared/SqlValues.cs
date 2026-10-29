#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// A SQL connector's bound values: the operation's parameters named <c>@name</c>, each the value of <c>@name</c> in
    /// the statement - sent to the server as a parameter, never written into the SQL, so a value from a pipeline run (a form
    /// field, a webhook's body) cannot change the statement it fills. n8n's "Query Parameters", by name. Every other
    /// parameter is the operation's own (<c>query</c>).
    /// </summary>
    public static class SqlValues
    {
        /// <summary>The values to bind, by name without the <c>@</c>, in name order.</summary>
        /// <exception cref="InvalidOperationException">A value's name is not a SQL parameter name (letters, digits, underscores).</exception>
        public static IReadOnlyList<KeyValuePair<string, string>> Of(IReadOnlyDictionary<string, string> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var values = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in parameters.Where(pair => pair.Key.StartsWith('@')).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var name = key[1..];
                if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    throw new InvalidOperationException($"'{key}' is not a value's name: write @ and letters, digits or underscores, as the query does.");
                }

                values.Add(new KeyValuePair<string, string>(name, value));
            }

            return values;
        }
    }
}
