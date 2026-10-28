#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Formats;
using Stomme.Formats.Persistence;
using Stomme.Modules.Formats.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Formats.Application
{
    /// <summary>
    /// The D108 cell formatter Formats replaces the invariant pass-through with (BL-94 increment 8, D110): a column whose
    /// <see cref="ExportColumn.Format"/> names a format - a preset's or a site definition's id, or its name - renders its
    /// values through that <see cref="FormatDescriptor"/> (which never throws); any other column is declined, so the
    /// renderer writes its own invariant text. The site's definitions are loaded ONCE per export by
    /// <see cref="PrepareAsync"/> (the contract's per-cell call is synchronous; a per-site lookup is not), so a key that
    /// names another site's definition is simply not this site's and is not applied - the refusal D110 asks for, with no
    /// cross-site read. Everything this formatter returns is a string and is neutralised by the renderer like any other
    /// cell (D108 refutation 11): a pattern whose literal output starts with a formula leader comes out quoted.
    /// </summary>
    public sealed class FormatsExportCellFormatter(IFormatDefinitionRepository definitions, ILogger<FormatsExportCellFormatter> logger) : IExportCellFormatter, IExportCellFormatterPreparation
    {
        private readonly Dictionary<Guid, FormatDescriptor> _byId = new();
        private readonly Dictionary<string, FormatDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);
        private int? _preparedSiteId;

        /// <summary>Loads the site's own definitions once; the presets need no loading. Called by the export processor before rendering; without it only presets resolve.</summary>
        public async Task PrepareAsync(int siteId, CancellationToken ct = default)
        {
            _byId.Clear();
            _byName.Clear();
            _unresolved.Clear();
            foreach (var definition in await definitions.ListForSiteAsync(siteId, ct))
            {
                var descriptor = definition.ToDescriptor();
                _byId[descriptor.Id] = descriptor;
                _byName[descriptor.Name] = descriptor; // the store keeps a site's names unique (D49)
            }

            _preparedSiteId = siteId;
        }

        public string? Format(object? value, ExportColumn column)
        {
            ArgumentNullException.ThrowIfNull(column);
            if (value is null || string.IsNullOrWhiteSpace(column.Format))
            {
                return null;
            }

            var descriptor = Resolve(column.Format.Trim());
            return descriptor?.Format(value);
        }

        /// <summary>The format a key names for the prepared site: the site's definition by id or name first, then a preset by id or name; null when nothing matches.</summary>
        public FormatDescriptor? Resolve(string key)
        {
            if (Guid.TryParse(key, out var id))
            {
                if (_byId.TryGetValue(id, out var own))
                {
                    return own;
                }

                if (SystemFormats.Find(id) is { } preset)
                {
                    return preset;
                }
            }
            else
            {
                if (_byName.TryGetValue(key, out var own))
                {
                    return own;
                }

                if (SystemFormats.All.FirstOrDefault(preset => string.Equals(preset.Name, key, StringComparison.OrdinalIgnoreCase)) is { } preset)
                {
                    return preset;
                }
            }

            if (_unresolved.Add(key))
            {
                // Once per key per export: an unknown key, or a definition that is not this site's, renders invariant.
                logger.LogDebug("Export column format '{FormatKey}' names no preset and no definition of site {SiteId}; the column renders invariant.", key, _preparedSiteId);
            }

            return null;
        }
    }
}
