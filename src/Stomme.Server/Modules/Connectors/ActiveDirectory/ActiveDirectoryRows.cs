#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.ActiveDirectory
{
    /// <summary>
    /// Flattens one <see cref="AdEntry"/> into a canonical record: multi-valued attributes join with ", ", the AD FILETIME
    /// attributes (<c>lastLogonTimestamp</c>, <c>accountExpires</c>) become a round-trippable UTC timestamp (blank for the
    /// "never" sentinels), <c>lastLogoff</c> blanks when zero, and every row carries <c>ADTarget</c> plus the
    /// <c>FlatDomain</c>/<c>QualifiedDomain</c> derived from its DN.
    /// </summary>
    internal static class ActiveDirectoryRows
    {
        private const long NeverExpires = long.MaxValue;

        private static readonly HashSet<string> FileTimeAttributes = new(StringComparer.OrdinalIgnoreCase) { "lastLogonTimestamp", "accountExpires" };

        public static ConnectorRecord Flatten(AdEntry entry, string adTarget)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, values) in entry.Attributes)
            {
                fields[name] = FieldValue(name, values);
            }

            if (!fields.ContainsKey("distinguishedName") && !string.IsNullOrEmpty(entry.DistinguishedName))
            {
                fields["distinguishedName"] = entry.DistinguishedName;
            }

            var (flat, qualified) = DomainFromDn(entry.DistinguishedName);
            fields["FlatDomain"] = flat;
            fields["QualifiedDomain"] = qualified;
            fields["ADTarget"] = adTarget;
            return new ConnectorRecord(fields);
        }

        /// <summary>(<c>FlatDomain</c>, <c>QualifiedDomain</c>) from a DN's <c>DC=</c> components: <c>CN=h,DC=corp,DC=example,DC=com</c> gives (<c>corp</c>, <c>corp.example.com</c>).</summary>
        public static (string Flat, string Qualified) DomainFromDn(string? distinguishedName)
        {
            if (string.IsNullOrWhiteSpace(distinguishedName))
            {
                return (string.Empty, string.Empty);
            }

            var labels = distinguishedName
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                .Select(part => part["DC=".Length..])
                .ToList();
            return labels.Count == 0 ? (string.Empty, string.Empty) : (labels[0], string.Join('.', labels));
        }

        private static object? FieldValue(string name, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return null;
            }

            if (FileTimeAttributes.Contains(name))
            {
                return ConvertFileTime(values[0]);
            }

            if (string.Equals(name, "lastLogoff", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks == 0 ? string.Empty : values[0];
            }

            return values.Count == 1 ? values[0] : string.Join(", ", values);
        }

        private static string ConvertFileTime(string raw)
        {
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 || ticks == NeverExpires)
            {
                return string.Empty;
            }

            try
            {
                return DateTime.FromFileTimeUtc(ticks).ToString("o", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }
    }
}
