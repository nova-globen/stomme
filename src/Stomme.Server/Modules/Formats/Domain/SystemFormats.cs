#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Stomme.Formats;

namespace Stomme.Modules.Formats.Domain
{
    /// <summary>
    /// The compiled system presets: dates, numbers, currencies and locales every site can render with,
    /// each under a DETERMINISTIC id derived from its kind and name so a reference survives every install and every
    /// provider. No two presets share a (kind, pattern, culture) triple - the predecessor platform's "Unix epoch seconds" duplicated the
    /// ISO UTC preset under a name it did not honour, and is dropped.
    /// </summary>
    public static class SystemFormats
    {
        private static readonly FormatDescriptor[] Catalogue = Build();

        public static IReadOnlyList<FormatDescriptor> All => Catalogue;

        public static FormatDescriptor? Find(Guid id) => Array.Find(Catalogue, format => format.Id == id);

        /// <summary>The preset id for a kind and name: SHA-256 over a fixed namespace and the key, folded to 16 bytes. Stable by construction.</summary>
        public static Guid IdOf(FormatKind kind, string name)
        {
            var bytes = Encoding.UTF8.GetBytes("stomme.formats.preset|" + kind + "|" + name);
            var hash = SHA256.HashData(bytes);
            var guid = new byte[16];
            Array.Copy(hash, guid, 16);
            return new Guid(guid);
        }

        private static FormatDescriptor Preset(string name, FormatKind kind, string pattern, string culture) =>
            new(IdOf(kind, name), name, kind, pattern, culture, FormatScope.System);

        private static FormatDescriptor[] Build()
        {
            var presets = new[]
            {
                // --- Date / time (ISO, regional, legacy) ---
                Preset("ISO 8601 date", FormatKind.Date, "yyyy-MM-dd", ""),
                Preset("ISO 8601 date-time", FormatKind.Date, "yyyy-MM-ddTHH:mm:ss", ""),
                Preset("ISO 8601 date-time (UTC)", FormatKind.Date, "yyyy-MM-ddTHH:mm:ssZ", ""),
                Preset("ISO 8601 with offset", FormatKind.Date, "yyyy-MM-ddTHH:mm:sszzz", ""),
                Preset("Sortable", FormatKind.Date, "yyyy-MM-dd HH:mm:ss", ""),
                Preset("US date", FormatKind.Date, "M/d/yyyy", "en-US"),
                Preset("US date-time", FormatKind.Date, "M/d/yyyy h:mm tt", "en-US"),
                Preset("UK date", FormatKind.Date, "dd/MM/yyyy", "en-GB"),
                Preset("EU date (de-DE)", FormatKind.Date, "dd.MM.yyyy", "de-DE"),
                Preset("EU date (fr-FR)", FormatKind.Date, "dd/MM/yyyy", "fr-FR"),
                Preset("RFC 2822", FormatKind.Date, "ddd, dd MMM yyyy HH:mm:ss zzz", "en-US"),
                Preset("Long date", FormatKind.Date, "dddd, dd MMMM yyyy", "en-US"),
                Preset("Month and year", FormatKind.Date, "MMMM yyyy", "en-US"),
                Preset("Time of day", FormatKind.Date, "HH:mm:ss", ""),

                // --- Numbers ---
                Preset("Integer", FormatKind.Number, "N0", ""),
                Preset("Decimal (2 places)", FormatKind.Number, "N2", ""),
                Preset("Decimal (US)", FormatKind.Number, "N2", "en-US"),
                Preset("Decimal (de-DE)", FormatKind.Number, "N2", "de-DE"),
                Preset("Decimal (fr-FR)", FormatKind.Number, "N2", "fr-FR"),
                Preset("Thousands grouped", FormatKind.Number, "#,##0", "en-US"),
                Preset("Percent", FormatKind.Number, "P1", "en-US"),
                Preset("Scientific", FormatKind.Number, "0.###E+0", ""),
                Preset("Fixed (3 places)", FormatKind.Number, "0.000", ""),

                // --- Currencies (ISO 4217 via culture) ---
                Preset("USD", FormatKind.Currency, "C2", "en-US"),
                Preset("EUR (de-DE)", FormatKind.Currency, "C2", "de-DE"),
                Preset("EUR (fr-FR)", FormatKind.Currency, "C2", "fr-FR"),
                Preset("GBP", FormatKind.Currency, "C2", "en-GB"),
                Preset("JPY", FormatKind.Currency, "C0", "ja-JP"),

                // --- Locales ---
                Preset("English (United States)", FormatKind.Locale, "", "en-US"),
                Preset("English (United Kingdom)", FormatKind.Locale, "", "en-GB"),
                Preset("German (Germany)", FormatKind.Locale, "", "de-DE"),
                Preset("French (France)", FormatKind.Locale, "", "fr-FR"),
                Preset("Spanish (Spain)", FormatKind.Locale, "", "es-ES"),
                Preset("Japanese (Japan)", FormatKind.Locale, "", "ja-JP"),
                Preset("Invariant", FormatKind.Locale, "", ""),
            };

            var duplicate = presets.GroupBy(preset => (preset.Kind, preset.Pattern, preset.Culture)).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"Two system presets render the same way: {string.Join(" / ", duplicate.Select(preset => preset.Name))}.");
            }

            return presets;
        }
    }
}
