using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime.TimeZones;

namespace Stomme.Services
{
    /// <summary>
    /// Finds the entry of a time zone list that a time zone name means.
    /// </summary>
    /// <remarks>
    /// A browser reports its zone as an IANA name (<c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>), and so does a
    /// person typing one, but not always the name the list carries: tzdb keeps old names as links (<c>Asia/Calcutta</c> for
    /// <c>Asia/Kolkata</c>, <c>Europe/Kiev</c> for <c>Europe/Kyiv</c>), and the list leaves out zones with no country. So a
    /// name is matched exactly, then ignoring case, then by the zone tzdb resolves both names to, then by the one city it names.
    /// </remarks>
    public static class TimeZoneMatcher
    {
        /// <summary>The <see cref="Models.TimeZone.Id"/> of the list entry <paramref name="name"/> means, or null when none.</summary>
        public static string Match(string name, IEnumerable<Models.TimeZone> zones)
        {
            if (string.IsNullOrWhiteSpace(name) || zones == null)
            {
                return null;
            }

            name = name.Trim();
            var list = zones.Where(zone => !string.IsNullOrEmpty(zone.Id)).ToList();

            var exact = list.FirstOrDefault(zone => string.Equals(zone.Id, name, StringComparison.Ordinal))
                ?? list.FirstOrDefault(zone => string.Equals(zone.Id, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact.Id;
            }

            var canonical = Canonical(name);
            if (canonical != null)
            {
                var linked = list.FirstOrDefault(zone => string.Equals(Canonical(zone.Id), canonical, StringComparison.Ordinal));
                if (linked != null)
                {
                    return linked.Id;
                }
            }

            // A city alone ("Stockholm", "new york"), or a part of one ("toky"), when exactly one entry's city has it.
            var part = name.Replace(' ', '_');
            var city = "/" + part;
            var cities = list.Where(zone => zone.Id.EndsWith(city, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (cities.Count == 1)
            {
                return cities[0].Id;
            }

            var partial = list.Where(zone => zone.Id[(zone.Id.LastIndexOf('/') + 1)..].Contains(part, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return partial.Count == 1 ? partial[0].Id : null;
        }

        private static string Canonical(string id)
        {
            var map = TzdbDateTimeZoneSource.Default.CanonicalIdMap;
            if (map.TryGetValue(id, out var canonical))
            {
                return canonical;
            }

            var key = map.Keys.FirstOrDefault(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
            return key == null ? null : map[key];
        }
    }
}
