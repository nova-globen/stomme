#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Rendering
{
    /// <summary>
    /// The one place a format is turned into its renderer (D108 item 3).
    /// </summary>
    /// <remarks>
    /// Two renderers claiming one format THROWS at construction rather than letting the last registration win:
    /// which renderer produced an artifact would otherwise depend on module load order, and a packaged module
    /// could silently replace the built-in CSV renderer - the one that neutralises formulas - with its own.
    /// </remarks>
    public sealed class ExportRendererRegistry
    {
        private readonly Dictionary<ExportFormat, ITabularRenderer> _renderers = new();

        public ExportRendererRegistry(IEnumerable<ITabularRenderer> renderers)
        {
            ArgumentNullException.ThrowIfNull(renderers);

            foreach (var renderer in renderers)
            {
                if (_renderers.TryGetValue(renderer.Format, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Two renderers claim the {renderer.Format} format: {existing.GetType().FullName} and " +
                        $"{renderer.GetType().FullName}. A format has exactly one renderer.");
                }

                _renderers.Add(renderer.Format, renderer);
            }
        }

        public IReadOnlyCollection<ExportFormat> Formats => _renderers.Keys;

        public bool TryGet(ExportFormat format, out ITabularRenderer renderer) =>
            _renderers.TryGetValue(format, out renderer!);

        /// <summary>The renderer for <paramref name="format"/>; throws when none is registered.</summary>
        public ITabularRenderer Get(ExportFormat format) =>
            _renderers.TryGetValue(format, out var renderer)
                ? renderer
                : throw new ExportRenderException($"No renderer is registered for the {format} format.");
    }
}
