#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>
    /// One <c>Licensing:Keys:&lt;name&gt;</c> entry as the licence status surface reports it. It NEVER carries the blob.
    /// <see cref="Lid"/>, <see cref="Label"/>, <see cref="Entitlements"/>, <see cref="Limits"/> and <see cref="Expires"/> are
    /// present whenever the payload parsed, but are AUTHENTIC only for reasons reached after the signature check (every
    /// reason except <see cref="LicenceKeyReason.Malformed"/>, <see cref="LicenceKeyReason.UnknownKey"/> and
    /// <see cref="LicenceKeyReason.BadSignature"/>); only <see cref="LicenceKeyReason.Valid"/> grants anything.
    /// </summary>
    internal sealed record LicenceKeyStatus(
        string Name,
        Guid? Lid,
        string? Label,
        LicenceKeyReason Reason,
        IReadOnlyList<string> Entitlements,
        IReadOnlyDictionary<string, long> Limits,
        DateTimeOffset? Expires);

    /// <summary>
    /// The offline licence ring's <see cref="IEntitlementProvider"/>, registered SCOPED and only under
    /// <c>Deployment:Mode=OnPrem</c> (<see cref="LicensingServiceCollectionExtensions.AddStommeLicensing"/>). A capability
    /// is granted iff a <see cref="LicenceKeyReason.Valid"/> key's <c>ent</c> contains it, ordinally — so several keys union.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reads <c>Licensing:Keys</c> FRESH on every call from the <see cref="IConfigurationRoot"/> — Stomme's own root, NOT
    /// the WebApplicationBuilder's <c>IConfiguration</c>, which is a different object that lacks
    /// <c>appsettings.override.json</c>, <c>features.json</c> and the other layered sources — so a ConfigManager write plus
    /// <c>Reload()</c> is observed on the next evaluation with no restart. Parse + signature results are cached by the
    /// singleton <see cref="LicenceState"/>; the validity window is re-applied per call.
    /// </para>
    /// <para>
    /// The installation id is <see cref="IConfigManager.GetInstallationId"/> — a PURE read. The provider never mints one
    /// (<c>EnsureInstallationId</c> writes config); an empty id yields <see cref="LicenceKeyReason.NoInstallationId"/>.
    /// A map entry whose name is not the key's signed <c>lid</c> (Guid compare, so casing is irrelevant) grants nothing and
    /// reports <see cref="LicenceKeyReason.LidMismatch"/>. Keys are never logged by this class — it has no logger at all.
    /// </para>
    /// <para>
    /// Does NOT implement <c>IMeteredLimitProvider</c>: that port does not exist yet, so the numeric half is
    /// left to the metering work rather than ported ahead of it.
    /// </para>
    /// </remarks>
    internal sealed class OfflineLicenceEntitlementProvider : IEntitlementProvider
    {
        internal const string KeysSection = "Licensing:Keys";

        private readonly IConfigurationRoot _configuration;
        private readonly IConfigManager _configManager;
        private readonly LicenceState _state;

        public OfflineLicenceEntitlementProvider(IConfigurationRoot configuration, IConfigManager configManager, LicenceState state)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(configManager);
            ArgumentNullException.ThrowIfNull(state);
            _configuration = configuration;
            _configManager = configManager;
            _state = state;
        }

        public Task<bool> IsEntitledAsync(string capability, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(capability))
            {
                return Task.FromResult(false);
            }

            var installationId = _configManager.GetInstallationId();
            foreach (var entry in ReadEntries())
            {
                var (reason, payload) = Evaluate(entry.Name, entry.Blob, installationId);
                if (reason == LicenceKeyReason.Valid && payload is not null && payload.Ent.Contains(capability, StringComparer.Ordinal))
                {
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        /// <summary>Every configured key's status, ordered by map name. For the Host status surface; carries no blob.</summary>
        public IReadOnlyList<LicenceKeyStatus> GetKeys()
        {
            var installationId = _configManager.GetInstallationId();
            var result = new List<LicenceKeyStatus>();
            foreach (var entry in ReadEntries().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var (reason, payload) = Evaluate(entry.Name, entry.Blob, installationId);
                result.Add(new LicenceKeyStatus(
                    entry.Name,
                    payload?.Lid,
                    payload?.Lbl,
                    reason,
                    payload?.Ent ?? Array.Empty<string>(),
                    payload?.Lim ?? new Dictionary<string, long>(StringComparer.Ordinal),
                    payload?.Exp));
            }

            return result;
        }

        private IEnumerable<(string Name, string Blob)> ReadEntries() =>
            _configuration.GetSection(KeysSection).GetChildren().Select(child => (child.Key, child.Value ?? string.Empty)).ToList();

        private (LicenceKeyReason Reason, LicencePayload? Payload) Evaluate(string name, string blob, string? installationId)
        {
            var verification = _state.Evaluate(blob, installationId ?? string.Empty);
            if (verification.Reason == LicenceKeyReason.Valid
                && verification.Payload is not null
                && !(Guid.TryParse(name, out var named) && named == verification.Payload.Lid))
            {
                return (LicenceKeyReason.LidMismatch, verification.Payload);
            }

            return (verification.Reason, verification.Payload);
        }
    }
}