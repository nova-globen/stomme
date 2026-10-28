#nullable enable
using System.Threading.Tasks;
using Stomme.Repository;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// The scope's copy of <see cref="HealthSettings"/>, loaded once, asynchronously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because reading settings is asynchronous and a constructor is not.</b> The obvious
    /// registration — resolve <see cref="HealthSettings"/> from a factory that blocks on
    /// <c>LoadAsync(...).GetAwaiter().GetResult()</c> — is banned outright here: <c>AsyncAllTheWayDownTests</c>
    /// (D33) fails the build on sync-over-async and its allowlist has exactly ONE entry, a MAUI platform
    /// contract. Wanting a second is a design error, not a paperwork step. So the settings are loaded by the
    /// PASS, which is already in an async method, and shared with everything else in the scope through here.
    /// </para>
    /// <para>
    /// <b>Until it is loaded, this answers with the defaults</b> rather than throwing or returning null. A
    /// contributor that builds its probes before the pass has loaded anything gets sane thresholds instead of an
    /// exception — the worst case is one tick measured against the default latency threshold, which is a far
    /// better failure than a monitor that will not start.
    /// </para>
    /// </remarks>
    public sealed class HealthSettingsAccessor
    {
        private bool _loaded;

        /// <summary>The settings for this scope; the defaults until <see cref="EnsureLoadedAsync"/> has run.</summary>
        public HealthSettings Current { get; private set; } = HealthSettings.Defaults;

        /// <summary>Loads the settings once per scope.</summary>
        public async Task<HealthSettings> EnsureLoadedAsync(ISettingRepository settings)
        {
            if (_loaded)
            {
                return Current;
            }

            Current = await HealthSettings.LoadAsync(settings).ConfigureAwait(false);
            _loaded = true;
            return Current;
        }
    }
}
