using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// <see cref="OfflineLicenceEntitlementProvider"/> grants a capability iff a Valid key under
    /// <c>Licensing:Keys:&lt;lid&gt;</c> lists it, reading the map FRESH on every call over a shared <see cref="LicenceState"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider is built the way DI builds it (public ctor → <see cref="LicenceKeyRing.Current"/>), so the test ring is
    /// installed with <see cref="LicenceKeyRing.OverrideForTests"/>. That override is PROCESS-WIDE and refuses to nest, so
    /// every class using it lives in the one <c>LicenceRing</c> collection; the verifier's unit tests use the explicit-ring
    /// overload and need no collection.
    /// </para>
    /// <para>
    /// Reconciled by name with an earlier licensing test suite:
    /// <c>License_Entitlement_RespectsValidityWindowAndWildcard</c> → <see cref="License_Entitlement_RespectsValidityWindowAndWildcard"/>
    /// (validity window and listed-capability half kept; the Revoke half is dropped with Revoke; the wildcard half is
    /// deliberately INVERTED — this product does not carry <c>*</c>, which the verifier rejects as Malformed).
    /// </para>
    /// </remarks>
    [Collection("LicenceRing")]
    public sealed class OfflineEntitlementProviderTests : IDisposable
    {
        private const string Installation = "5f0e8c1a-2b3d-4e5f-8a9b-0c1d2e3f4a5b";
        private const string OtherInstallation = "0a1b2c3d-4e5f-4a6b-9c8d-7e6f5a4b3c2d";
        private const string BlogsPro = "novagloben.blogs.pro";
        private const string ReportsExport = "novagloben.reports.export";
        private static readonly DateTimeOffset T0 = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

        private readonly TestLicenceIssuer _issuer = new();
        private readonly IDisposable _ring;
        private readonly MutableTime _time = new(T0);
        private readonly StubConfigManager _configManager = new() { InstallationId = Installation };
        private readonly IConfigurationRoot _configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        private readonly LicenceState _state;

        public OfflineEntitlementProviderTests()
        {
            _ring = LicenceKeyRing.OverrideForTests(_issuer.Ring);
            _state = new LicenceState(_time);
        }

        public void Dispose()
        {
            _ring.Dispose();
            _issuer.Dispose();
        }

        private OfflineLicenceEntitlementProvider NewProvider() => new(_configuration, _configManager, _state);

        private LicencePayload Payload(string[] ent, string sub = Installation, DateTimeOffset? nbf = null, DateTimeOffset? exp = null) =>
            _issuer.Payload(sub, ent, nbf: nbf ?? T0.AddDays(-1), exp: exp);

        /// <summary>Writes a key under its own lid (or <paramref name="name"/>) and returns the blob.</summary>
        private string Put(LicencePayload payload, string? name = null)
        {
            var blob = _issuer.Issue(payload);
            _configuration["Licensing:Keys:" + (name ?? payload.Lid.ToString("D"))] = blob;
            return blob;
        }

        [Fact]
        public async Task No_key_is_denied()
        {
            Assert.False(await NewProvider().IsEntitledAsync(BlogsPro));
            Assert.Empty(NewProvider().GetKeys());
        }

        [Fact]
        public async Task A_valid_key_entitles_only_the_capabilities_it_lists_matched_ordinally()
        {
            Put(Payload(new[] { BlogsPro }));
            var provider = NewProvider();

            Assert.True(await provider.IsEntitledAsync(BlogsPro));
            Assert.False(await provider.IsEntitledAsync(ReportsExport));
            Assert.False(await provider.IsEntitledAsync("NovaGloben.Blogs.Pro"));
            Assert.False(await provider.IsEntitledAsync(string.Empty));
        }

        [Fact]
        public async Task Two_valid_keys_union_their_capabilities()
        {
            Put(Payload(new[] { BlogsPro }));
            Put(Payload(new[] { ReportsExport }));
            var provider = NewProvider();

            Assert.True(await provider.IsEntitledAsync(BlogsPro));
            Assert.True(await provider.IsEntitledAsync(ReportsExport));
            Assert.False(await provider.IsEntitledAsync("novagloben.other"));
        }

        [Fact]
        public async Task An_invalid_key_beside_a_valid_one_contributes_nothing()
        {
            Put(Payload(new[] { ReportsExport }));

            // Structurally perfect, names the ring's kid, signed by a foreign key: BadSignature.
            var forged = Payload(new[] { BlogsPro });
            _configuration["Licensing:Keys:" + forged.Lid.ToString("D")] = _issuer.SignWithForeignKeyUnderKid(forged);
            var provider = NewProvider();

            Assert.True(await provider.IsEntitledAsync(ReportsExport));
            Assert.False(await provider.IsEntitledAsync(BlogsPro));
            Assert.Contains(provider.GetKeys(), k => k.Lid == forged.Lid && k.Reason == LicenceKeyReason.BadSignature);
        }

        [Fact]
        public async Task An_expiry_crossing_mid_process_denies_with_no_restart()
        {
            Put(Payload(new[] { BlogsPro }, exp: T0.AddHours(1)));
            var provider = NewProvider();

            Assert.True(await provider.IsEntitledAsync(BlogsPro));

            _time.Now = T0.AddHours(1);
            Assert.False(await provider.IsEntitledAsync(BlogsPro));
            Assert.False(await NewProvider().IsEntitledAsync(BlogsPro));
            Assert.Equal(LicenceKeyReason.Expired, Assert.Single(provider.GetKeys()).Reason);
        }

        [Fact]
        public async Task A_key_bound_to_another_installation_is_denied()
        {
            Put(Payload(new[] { BlogsPro }, sub: OtherInstallation));
            var provider = NewProvider();

            Assert.False(await provider.IsEntitledAsync(BlogsPro));
            Assert.Equal(LicenceKeyReason.WrongInstallation, Assert.Single(provider.GetKeys()).Reason);

            // Control: the same blob IS good — for the installation it names.
            _configManager.InstallationId = OtherInstallation;
            Assert.True(await provider.IsEntitledAsync(BlogsPro));
        }

        [Fact]
        public async Task An_uninitialised_installation_id_is_denied_as_NoInstallationId()
        {
            Put(Payload(new[] { BlogsPro }));
            _configManager.InstallationId = string.Empty;
            var provider = NewProvider();

            Assert.False(await provider.IsEntitledAsync(BlogsPro));
            Assert.Equal(LicenceKeyReason.NoInstallationId, Assert.Single(provider.GetKeys()).Reason);
        }

        [Fact]
        public async Task A_map_entry_whose_name_is_not_its_lid_is_denied()
        {
            var payload = Payload(new[] { BlogsPro });
            var blob = Put(payload, name: Guid.NewGuid().ToString("D"));
            _configuration["Licensing:Keys:not-a-guid"] = blob;
            var provider = NewProvider();

            Assert.False(await provider.IsEntitledAsync(BlogsPro));
            Assert.All(provider.GetKeys(), k => Assert.Equal(LicenceKeyReason.LidMismatch, k.Reason));
            Assert.Equal(2, provider.GetKeys().Count);

            // Control: the same blob under its own lid — matched case-insensitively — grants.
            _configuration["Licensing:Keys:" + payload.Lid.ToString("D").ToUpperInvariant()] = blob;
            Assert.True(await provider.IsEntitledAsync(BlogsPro));
        }

        [Fact]
        public async Task A_key_written_after_the_provider_was_built_is_observed_on_the_next_call()
        {
            var provider = NewProvider();
            Assert.False(await provider.IsEntitledAsync(BlogsPro));

            Put(Payload(new[] { BlogsPro }));
            Assert.True(await provider.IsEntitledAsync(BlogsPro));

            foreach (var key in _configuration.GetSection("Licensing:Keys").GetChildren().ToList())
            {
                _configuration["Licensing:Keys:" + key.Key] = null;
            }

            Assert.False(await provider.IsEntitledAsync(BlogsPro));
        }

        /// <summary>
        /// Adapted by name from an earlier suite, <c>LicensingTests.License_Entitlement_RespectsValidityWindowAndWildcard</c>: listed
        /// capability inside the window → granted; an unlisted one → denied; before <c>nbf</c> and at/after <c>exp</c> → denied.
        /// The WILDCARD half is deliberately NOT carried: there is no <c>*</c> capability, the verifier rejects a key listing
        /// it as Malformed, so here a <c>*</c> key grants NOTHING (an earlier version of this test asserted that it granted everything).
        /// </summary>
        [Fact]
        public async Task License_Entitlement_RespectsValidityWindowAndWildcard()
        {
            Put(Payload(new[] { ReportsExport }, nbf: T0.AddDays(1), exp: T0.AddDays(30)));
            var provider = NewProvider();

            Assert.False(await provider.IsEntitledAsync(ReportsExport)); // not yet valid

            _time.Now = T0.AddDays(2);
            Assert.True(await provider.IsEntitledAsync(ReportsExport));
            Assert.False(await provider.IsEntitledAsync("novagloben.something.else"));

            _time.Now = T0.AddDays(60);
            Assert.False(await provider.IsEntitledAsync(ReportsExport)); // expired

            _time.Now = T0.AddDays(2);
            var wildcard = Payload(new[] { "*" });
            Put(wildcard);
            Assert.False(await provider.IsEntitledAsync("novagloben.anything.at-all"));
            Assert.Equal(LicenceKeyReason.Malformed, provider.GetKeys().Single(k => k.Name == wildcard.Lid.ToString("D")).Reason);
        }

        [Fact]
        public void GetKeys_reports_lid_label_reason_entitlements_limits_and_expiry_but_never_the_blob()
        {
            var payload = _issuer.Payload(
                Installation,
                new[] { BlogsPro, ReportsExport },
                new Dictionary<string, long>(StringComparer.Ordinal) { ["users"] = 25, ["sites"] = -1 },
                nbf: T0.AddDays(-1),
                exp: T0.AddDays(10),
                lbl: "Acme on-prem");
            var blob = Put(payload);

            var status = Assert.Single(NewProvider().GetKeys());

            Assert.Equal(payload.Lid.ToString("D"), status.Name);
            Assert.Equal(payload.Lid, status.Lid);
            Assert.Equal("Acme on-prem", status.Label);
            Assert.Equal(LicenceKeyReason.Valid, status.Reason);
            Assert.Equal(new[] { BlogsPro, ReportsExport }, status.Entitlements);
            Assert.Equal(25, status.Limits["users"]);
            Assert.Equal(-1, status.Limits["sites"]);
            Assert.Equal(T0.AddDays(10), status.Expires);

            // No member of the status record carries the blob (or any fragment of its signature segment).
            var signature = blob.Split('.')[2];
            foreach (var property in typeof(LicenceKeyStatus).GetProperties())
            {
                var value = property.GetValue(status)?.ToString() ?? string.Empty;
                Assert.DoesNotContain(signature, value, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(signature, status.ToString(), StringComparison.Ordinal);
        }

        /// <summary>Only <see cref="IConfigManager.GetInstallationId"/> is used by the provider; everything else throws.</summary>
        private sealed class StubConfigManager : IConfigManager
        {
            public string InstallationId { get; set; } = string.Empty;

            public string GetInstallationId() => InstallationId;

            public IConfigurationSection GetSection(string sectionKey) => throw new NotSupportedException();
            public T GetSetting<T>(string settingKey, T defaultValue) => throw new NotSupportedException();
            public T GetSetting<T>(string sectionKey, string settingKey, T defaultValue) => throw new NotSupportedException();
            public Dictionary<string, string> GetSettings(string sectionKey) => throw new NotSupportedException();
            public void AddOrUpdateSetting<T>(string key, T value, bool reload) => throw new NotSupportedException();
            public void AddOrUpdateSetting<T>(string file, string key, T value, bool reload) => throw new NotSupportedException();
            public void RemoveSetting(string key, bool reload) => throw new NotSupportedException();
            public void RemoveSetting(string file, string key, bool reload) => throw new NotSupportedException();
            public void Reload() => throw new NotSupportedException();
            public string GetWritableSettingsPath() => throw new NotSupportedException();
            public string GetDataDirectory() => throw new NotSupportedException();
            public string GetConnectionString() => throw new NotSupportedException();
            public string GetConnectionString(string name) => throw new NotSupportedException();
            public string GetDatabaseType() => throw new NotSupportedException();
            public bool IsInstalled() => throw new NotSupportedException();
            public string EnsureInstallationId() => throw new NotSupportedException("The provider must never MINT an installation id.");
        }
    }
}