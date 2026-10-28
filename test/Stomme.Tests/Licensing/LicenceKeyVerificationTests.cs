using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Stomme.Infrastructure.Licensing;
using Xunit;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// D105 design items 3-5: the <c>SLK1</c> verifier, driven over an EPHEMERAL test ring. Every rejection reason is
    /// pinned to the exact enum value, so a verifier that merely "rejects" cannot pass.
    /// </summary>
    /// <remarks>
    /// Adapted from the predecessor platform <c>tests/Licensing.Tests/LicenseKeyTests.cs</c> (by name):
    /// <c>SignedKey_RoundTrips_AndRejectsTamperingOrGarbage</c> (whole fact) and the VERIFICATION half of
    /// <c>ImportLicenseKey_ActivatesFromAValidKey_AndRejectsInvalidOrExpired</c> (its activation half is dropped with
    /// the predecessor platform's key-less Activate/Import feature, D105 item 12).
    /// </remarks>
    public sealed class LicenceKeyVerificationTests : IDisposable
    {
        private const string Installation = "7f1c2b9e-4d3a-4c8e-9b1f-2a6d5e8c0b13";
        private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        private readonly TestLicenceIssuer _issuer = new();

        public void Dispose() => _issuer.Dispose();

        private LicenceVerification Verify(string blob, string? installationId = Installation, DateTimeOffset? at = null) =>
            LicenceKeyVerifier.Verify(blob, installationId, at ?? Now, _issuer.Ring);

        private string ValidKey(DateTimeOffset? nbf = null, DateTimeOffset? exp = null, IEnumerable<string>? ent = null) =>
            _issuer.Issue(_issuer.Payload(Installation, ent, nbf: nbf ?? Now.AddDays(-1), exp: exp));

        private string RawJson(Func<string, string> edit, LicencePayload? payload = null)
        {
            var json = Encoding.UTF8.GetString((payload ?? _issuer.Payload(Installation, nbf: Now.AddDays(-1))).ToJsonBytes());
            var edited = edit(json);
            Assert.NotEqual(json, edited); // the edit must actually change something, or the case proves nothing
            return _issuer.IssueRaw(edited);
        }

        private static string[] Segments(string blob) => blob.Split('.');

        [Fact]
        public void A_valid_key_verifies_and_carries_its_payload()
        {
            var payload = _issuer.Payload(Installation, new[] { "novagloben.blogs.pro", "reports:export" },
                new Dictionary<string, long> { ["seats"] = 25, ["storage.gb"] = -1 },
                nbf: Now.AddDays(-1), exp: Now.AddYears(1));

            var result = Verify(_issuer.Issue(payload));

            Assert.Equal(LicenceKeyReason.Valid, result.Reason);
            Assert.True(result.IsValid);
            Assert.NotNull(result.Payload);
            Assert.Equal(payload.Lid, result.Payload!.Lid);
            Assert.Equal(new[] { "novagloben.blogs.pro", "reports:export" }, result.Payload.Ent);
            Assert.Equal(25, result.Payload.Lim["seats"]);
            Assert.Equal(-1, result.Payload.Lim["storage.gb"]);
            Assert.Equal(payload.Exp!.Value.ToUnixTimeSeconds(), result.Payload.Exp!.Value.ToUnixTimeSeconds());
        }

        [Fact]
        public void A_perpetual_key_with_null_exp_verifies()
        {
            var result = Verify(ValidKey(exp: null), at: Now.AddYears(50));
            Assert.Equal(LicenceKeyReason.Valid, result.Reason);
            Assert.Null(result.Payload!.Exp);
        }

        [Fact]
        public void A_flipped_payload_byte_is_BadSignature()
        {
            var blob = ValidKey();
            var parts = Segments(blob);
            Assert.True(LicenceBase64Url.TryDecode(parts[1], out var payloadBytes));
            var json = Encoding.UTF8.GetString(payloadBytes);
            var tampered = json.Replace("Test licence", "Test licencf", StringComparison.Ordinal);
            Assert.NotEqual(json, tampered);

            var forged = parts[0] + "." + LicenceBase64Url.Encode(Encoding.UTF8.GetBytes(tampered)) + "." + parts[2];

            Assert.Equal(LicenceKeyReason.BadSignature, Verify(forged).Reason);
        }

        [Fact]
        public void A_flipped_signature_byte_is_BadSignature()
        {
            var parts = Segments(ValidKey());
            Assert.True(LicenceBase64Url.TryDecode(parts[2], out var signature));
            signature[17] ^= 0x01;

            var forged = parts[0] + "." + parts[1] + "." + LicenceBase64Url.Encode(signature);

            Assert.Equal(LicenceKeyReason.BadSignature, Verify(forged).Reason);
        }

        [Fact]
        public void A_foreign_signer_under_a_known_kid_is_BadSignature()
        {
            var forged = _issuer.SignWithForeignKeyUnderKid(_issuer.Payload(Installation, nbf: Now.AddDays(-1)));

            Assert.Equal(LicenceKeyReason.BadSignature, Verify(forged).Reason);
        }

        [Fact]
        public void An_unknown_kid_is_UnknownKey()
        {
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: Now.AddDays(-1), kid: "some-other-kid"));

            Assert.Equal(LicenceKeyReason.UnknownKey, Verify(blob).Reason);
        }

        [Fact]
        public void A_test_ring_key_is_UnknownKey_against_the_ring_in_force()
        {
            // The public overload uses LicenceKeyRing.Current (Production here): an ephemeral test key must never verify.
            var result = LicenceKeyVerifier.Verify(ValidKey(), Installation, Now);

            Assert.Equal(LicenceKeyReason.UnknownKey, result.Reason);
        }

        [Fact]
        public void A_different_installation_is_WrongInstallation()
        {
            Assert.Equal(LicenceKeyReason.WrongInstallation, Verify(ValidKey(), "another-installation").Reason);
            // Ordinal: a casing difference is a different installation.
            Assert.Equal(LicenceKeyReason.WrongInstallation, Verify(ValidKey(), Installation.ToUpperInvariant()).Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void An_empty_installation_is_NoInstallationId_and_never_WrongInstallation(string? installationId)
        {
            var result = Verify(ValidKey(), installationId);

            Assert.Equal(LicenceKeyReason.NoInstallationId, result.Reason);
            Assert.NotEqual(LicenceKeyReason.WrongInstallation, result.Reason);
        }

        [Fact]
        public void The_installation_check_runs_after_the_signature()
        {
            var forged = _issuer.SignWithForeignKeyUnderKid(_issuer.Payload(Installation, nbf: Now.AddDays(-1)));

            Assert.Equal(LicenceKeyReason.BadSignature, Verify(forged, "").Reason);
        }

        [Fact]
        public void A_future_nbf_is_NotYetValid()
        {
            Assert.Equal(LicenceKeyReason.NotYetValid, Verify(ValidKey(nbf: Now.AddHours(1))).Reason);
        }

        [Fact]
        public void A_past_exp_is_Expired_through_a_TimeProvider()
        {
            var time = new MutableTime(Now);
            var blob = ValidKey(exp: Now.AddHours(1));

            Assert.Equal(LicenceKeyReason.Valid, Verify(blob, at: time.GetUtcNow()).Reason);

            time.Now = Now.AddHours(1); // exp is exclusive: utcNow >= exp is expired
            Assert.Equal(LicenceKeyReason.Expired, Verify(blob, at: time.GetUtcNow()).Reason);
        }

        [Fact]
        public void Version_2_is_Malformed()
        {
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace("\"v\":1", "\"v\":2"))).Reason);
        }

        [Fact]
        public void A_missing_segment_is_Malformed()
        {
            var parts = Segments(ValidKey());
            Assert.Equal(LicenceKeyReason.Malformed, Verify(parts[0] + "." + parts[1]).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(ValidKey() + ".extra").Reason);
        }

        [Theory]
        [InlineData('+')]
        [InlineData('/')]
        [InlineData('=')]
        [InlineData('!')]
        public void Non_base64url_characters_are_Malformed(char bad)
        {
            var parts = Segments(ValidKey());
            var payload = parts[1].Substring(0, 5) + bad + parts[1].Substring(6);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(parts[0] + "." + payload + "." + parts[2]).Reason);

            var padded = parts[0] + "." + parts[1] + "." + parts[2] + "==";
            Assert.Equal(LicenceKeyReason.Malformed, Verify(padded).Reason);
        }

        [Fact]
        public void A_blob_over_8KB_is_Malformed()
        {
            var big = _issuer.Issue(_issuer.Payload(Installation, nbf: Now.AddDays(-1), lbl: new string('x', 7000)));
            Assert.True(Encoding.UTF8.GetByteCount(big) > LicencePayload.MaxBlobBytes);

            Assert.Equal(LicenceKeyReason.Malformed, Verify(big).Reason);
        }

        [Fact]
        public void A_blob_just_under_8KB_is_still_Valid()
        {
            // The boundary is the blob, not the label: pins that the limit is not accidentally far lower than 8 KB.
            var blob = _issuer.Issue(_issuer.Payload(Installation, nbf: Now.AddDays(-1), lbl: new string('x', 5500)));
            Assert.InRange(Encoding.UTF8.GetByteCount(blob), 7000, LicencePayload.MaxBlobBytes);

            Assert.Equal(LicenceKeyReason.Valid, Verify(blob).Reason);
        }

        [Theory]
        [InlineData("*")]
        [InlineData("Blogs.Pro")]
        [InlineData("blogs pro")]
        [InlineData("")]
        [InlineData("blogs.pro\n")]
        public void A_non_canonical_entitlement_is_Malformed(string entitlement)
        {
            var blob = _issuer.Issue(_issuer.Payload(Installation, new[] { "reports:export", entitlement }, nbf: Now.AddDays(-1)));

            Assert.Equal(LicenceKeyReason.Malformed, Verify(blob).Reason);
        }

        [Fact]
        public void More_than_256_entitlements_is_Malformed_and_256_is_Valid()
        {
            var at256 = Enumerable.Range(0, 256).Select(i => "cap." + i).ToArray();
            var at257 = Enumerable.Range(0, 257).Select(i => "c" + i).ToArray();

            Assert.Equal(LicenceKeyReason.Valid, Verify(ValidKey(ent: at256)).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(ValidKey(ent: at257)).Reason);
        }

        [Fact]
        public void A_duplicate_entitlement_is_Malformed()
        {
            Assert.Equal(LicenceKeyReason.Malformed, Verify(ValidKey(ent: new[] { "a.b", "a.b" })).Reason);
        }

        [Fact]
        public void A_duplicate_kid_property_is_Malformed()
        {
            // Both spellings: a literal repeat, and a JSON-escaped repeat that only compares equal once unescaped.
            var literal = RawJson(j => j.Replace("\"kid\":", "\"kid\":\"some-other-kid\",\"kid\":"));
            var escaped = RawJson(j => j.Replace("\"kid\":", "\"k\\u0069d\":\"some-other-kid\",\"kid\":"));

            Assert.Equal(LicenceKeyReason.Malformed, Verify(literal).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(escaped).Reason);
        }

        [Fact]
        public void Structural_payload_defects_are_Malformed()
        {
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace("\"lid\":\"", "\"lid\":\"x"))).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace("\"iat\":", "\"iat\":\"1\",\"_\":"))).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace(",\"lbl\":", ",\"LBL\":"))).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace("\"lim\":{}", "\"lim\":{\"seats\":2.5}"))).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace("\"lim\":{}", "\"lim\":{\"seats\":-2}"))).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(_issuer.IssueRaw("[1,2,3]")).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(_issuer.IssueRaw("{}")).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(_issuer.IssueRawBytes(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D })).Reason);

            // A missing required field (exp removed entirely).
            Assert.Equal(LicenceKeyReason.Malformed, Verify(RawJson(j => j.Replace(",\"exp\":null", string.Empty))).Reason);
        }

        [Fact]
        public void A_signature_that_is_not_64_bytes_is_Malformed()
        {
            var parts = Segments(ValidKey());
            var shortSig = LicenceBase64Url.Encode(new byte[63]);
            var longSig = LicenceBase64Url.Encode(new byte[65]);

            Assert.Equal(LicenceKeyReason.Malformed, Verify(parts[0] + "." + parts[1] + "." + shortSig).Reason);
            Assert.Equal(LicenceKeyReason.Malformed, Verify(parts[0] + "." + parts[1] + "." + longSig).Reason);
        }

        [Theory]
        [InlineData("SLK2")]
        [InlineData("slk1")]
        [InlineData("")]
        public void A_wrong_prefix_is_Malformed(string prefix)
        {
            var parts = Segments(ValidKey());
            Assert.Equal(LicenceKeyReason.Malformed, Verify(prefix + "." + parts[1] + "." + parts[2]).Reason);
        }

        [Fact]
        public void SignedKey_RoundTrips_AndRejectsTamperingOrGarbage()
        {
            // Adapted by name from the predecessor platform LicenseKeyTests: issue → verify round-trips the signed fields; an appended
            // character and a garbage string are both rejected. the predecessor platform's '*' entitlement is NOT carried: '*' is
            // Malformed under D105.
            var payload = _issuer.Payload(Installation, new[] { "novagloben.enterprise" }, nbf: Now.AddDays(-1), exp: Now.AddYears(1));
            var signed = _issuer.Issue(payload);

            var verified = Verify(signed);
            Assert.Equal(LicenceKeyReason.Valid, verified.Reason);
            Assert.Equal(payload.Sub, verified.Payload!.Sub);
            Assert.Equal(payload.Lid, verified.Payload.Lid);
            Assert.Equal(payload.Lbl, verified.Payload.Lbl);

            Assert.False(Verify(signed + "x").IsValid);
            Assert.Equal(LicenceKeyReason.Malformed, Verify("not-a-key").Reason);
        }

        [Fact]
        public void ImportLicenseKey_ActivatesFromAValidKey_AndRejectsInvalidOrExpired()
        {
            // Adapted by name from the predecessor platform LicenseKeyTests — the VERIFICATION half only (the predecessor platform's Import/Activate
            // mediator path is not absorbed, D105 item 12).
            var valid = ValidKey(exp: Now.AddYears(1), ent: new[] { "reports:export" });
            var result = Verify(valid);
            Assert.Equal(LicenceKeyReason.Valid, result.Reason);
            Assert.Contains("reports:export", result.Payload!.Ent);

            Assert.Equal(LicenceKeyReason.Malformed, Verify("tampered").Reason);

            var expired = ValidKey(nbf: Now.AddYears(-1), exp: Now.AddDays(-1));
            Assert.Equal(LicenceKeyReason.Expired, Verify(expired).Reason);
        }
    }
}