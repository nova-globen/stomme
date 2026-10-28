using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Stomme.Infrastructure.Licensing;
using Xunit;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// D105 design item 5: <see cref="LicenceState"/> caches the PARSE + SIGNATURE result per (blob, installation) and
    /// re-checks <c>nbf</c>/<c>exp</c> against <see cref="TimeProvider"/> on EVERY call — no job, no restart.
    /// </summary>
    public sealed class LicenceStateTests : IDisposable
    {
        private const string Installation = "a0c3e1d2-5b64-47f8-9e0a-1b2c3d4e5f60";
        private static readonly DateTimeOffset T0 = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

        private readonly TestLicenceIssuer _issuer = new();
        private readonly MutableTime _time = new(T0);
        private int _verifierCalls;

        public void Dispose() => _issuer.Dispose();

        private LicenceState NewState(Func<IReadOnlyDictionary<string, byte[]>>? ring = null) =>
            new(_time, ring ?? (() => _issuer.Ring), (blob, installationId, r) =>
            {
                _verifierCalls++;
                return LicenceKeyVerifier.VerifyUntimed(blob, installationId, r);
            });

        [Fact]
        public void The_parse_and_signature_check_run_once_per_blob_and_installation()
        {
            var state = NewState();
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: T0.AddDays(-1)));

            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(LicenceKeyReason.Valid, state.Evaluate(blob, Installation).Reason);
            }

            Assert.Equal(1, _verifierCalls);

            // A different installation is a different cache entry (and a different answer).
            Assert.Equal(LicenceKeyReason.WrongInstallation, state.Evaluate(blob, "elsewhere").Reason);
            Assert.Equal(2, _verifierCalls);
            Assert.Equal(LicenceKeyReason.WrongInstallation, state.Evaluate(blob, "elsewhere").Reason);
            Assert.Equal(2, _verifierCalls);
        }

        [Fact]
        public void An_expiry_crossing_mid_process_flips_Valid_to_Expired_without_a_restart()
        {
            var state = NewState();
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: T0.AddDays(-1), exp: T0.AddHours(1)));

            Assert.Equal(LicenceKeyReason.Valid, state.Evaluate(blob, Installation).Reason);

            _time.Now = T0.AddHours(2);
            Assert.Equal(LicenceKeyReason.Expired, state.Evaluate(blob, Installation).Reason);

            Assert.Equal(1, _verifierCalls); // the flip came from the per-call re-check, not a re-parse
        }

        [Fact]
        public void A_not_before_crossing_mid_process_flips_NotYetValid_to_Valid()
        {
            var state = NewState();
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: T0.AddHours(1)));

            Assert.Equal(LicenceKeyReason.NotYetValid, state.Evaluate(blob, Installation).Reason);

            _time.Now = T0.AddHours(1);
            Assert.Equal(LicenceKeyReason.Valid, state.Evaluate(blob, Installation).Reason);
            Assert.Equal(1, _verifierCalls);
        }

        [Fact]
        public void The_cache_is_bounded()
        {
            var state = NewState();

            for (var i = 0; i < LicenceState.MaxCachedEntries + 50; i++)
            {
                state.Evaluate("garbage-" + i, Installation);
            }

            Assert.InRange(state.CachedEntryCount, 1, LicenceState.MaxCachedEntries);
        }

        [Fact]
        public void A_different_ring_instance_is_not_served_a_stale_cached_answer()
        {
            IReadOnlyDictionary<string, byte[]> ring = _issuer.Ring;
            var state = NewState(() => ring);
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: T0.AddDays(-1)));

            Assert.Equal(LicenceKeyReason.Valid, state.Evaluate(blob, Installation).Reason);

            ring = new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>());
            Assert.Equal(LicenceKeyReason.UnknownKey, state.Evaluate(blob, Installation).Reason);
            Assert.Equal(2, _verifierCalls);
        }

        [Fact]
        public void The_public_constructor_uses_the_ring_in_force()
        {
            var state = new LicenceState(_time);
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: T0.AddDays(-1)));

            Assert.Equal(LicenceKeyReason.UnknownKey, state.Evaluate(blob, Installation).Reason);
        }
    }
}