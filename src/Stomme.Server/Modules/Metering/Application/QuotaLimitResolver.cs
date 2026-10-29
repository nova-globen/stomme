#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Decides the limit in force for a metric on a site, and is the ONLY place -1 is decoded.
    /// </summary>
    /// <remarks>
    /// Three sources, in order: an <see cref="IMeteredLimitProvider"/> (a licence, when one is registered), then
    /// the module's own default for the metric, then "no quota defined". Keeping the -1 decoding here means no
    /// other file has to remember that a negative limit is a sentinel rather than an error.
    /// </remarks>
    public sealed class QuotaLimitResolver
    {
        private readonly IMeteredLimitProvider? _limits;
        private readonly IReadOnlyDictionary<string, long> _defaults;

        public QuotaLimitResolver(IEnumerable<IMeteredLimitProvider> limits, MeteringDefaults defaults)
        {
            // FirstOrDefault, not Single: none registered is the stock install, and a second one is a composition
            // error the startup validation reports rather than something to decide here.
            foreach (var provider in limits)
            {
                _limits = provider;
                break;
            }

            _defaults = defaults.Limits;
        }

        /// <summary>The limit for <paramref name="metric"/>, or null when no quota is defined for it.</summary>
        public async Task<QuotaLimit?> ResolveAsync(string metric, int siteId, CancellationToken ct = default)
        {
            if (_limits is not null)
            {
                var granted = await _limits.GetLimitAsync(metric, siteId, ct);
                if (granted is not null)
                {
                    return Decode(granted.Value);
                }
            }

            return _defaults.TryGetValue(MetricKeys.Normalize(metric), out var fallback) ? Decode(fallback) : null;
        }

        private static QuotaLimit Decode(long value) =>
            value == Domain.QuotaAggregate.UnlimitedLimit ? new QuotaLimit(0, true) : new QuotaLimit(value, false);
    }

    /// <summary>A resolved limit: a number, or unlimited.</summary>
    public sealed record QuotaLimit(long Units, bool Unlimited);

    /// <summary>The module's own default limits, by canonical metric key.</summary>
    /// <remarks>
    /// A stock install seeds NOTHING, so this is a catalog an operator adopts rather than rows that appear on
    /// their own: the default quotas are documentation, not data.
    /// </remarks>
    public sealed class MeteringDefaults
    {
        public MeteringDefaults(IReadOnlyDictionary<string, long>? limits = null)
        {
            Limits = limits ?? new Dictionary<string, long>(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, long> Limits { get; }
    }
}
