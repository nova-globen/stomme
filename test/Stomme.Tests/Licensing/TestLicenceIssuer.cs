using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Stomme.Infrastructure.Licensing;

namespace Stomme.Tests.Licensing
{
    /// <summary>
    /// A test-side licence issuer over an EPHEMERAL P-256 pair generated in-process (falsification F-E): no private key
    /// bytes are committed anywhere, and the ring it exposes carries the SPKI only. Its <c>kid</c> is
    /// <c>test-ephemeral-&lt;guid8&gt;</c>, so it can never collide with a production <c>kid</c>.
    /// </summary>
    internal sealed class TestLicenceIssuer : IDisposable
    {
        private readonly ECDsa _key;

        public TestLicenceIssuer()
        {
            _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Kid = "test-ephemeral-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Spki = _key.ExportSubjectPublicKeyInfo();
            Ring = new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [Kid] = Spki });
        }

        public string Kid { get; }

        public byte[] Spki { get; }

        /// <summary>The ring a verifier under test is given: <see cref="Kid"/> → SPKI, nothing private.</summary>
        public IReadOnlyDictionary<string, byte[]> Ring { get; }

        public LicencePayload Payload(
            string sub,
            IEnumerable<string>? ent = null,
            IReadOnlyDictionary<string, long>? lim = null,
            DateTimeOffset? nbf = null,
            DateTimeOffset? exp = null,
            string? kid = null,
            string lbl = "Test licence")
        {
            var now = DateTimeOffset.UtcNow;
            return new LicencePayload(
                LicencePayload.CurrentVersion,
                kid ?? Kid,
                Guid.NewGuid(),
                sub,
                lbl,
                (ent ?? new[] { "novagloben.blogs.pro" }).ToArray(),
                lim ?? new Dictionary<string, long>(StringComparer.Ordinal),
                now,
                nbf ?? now.AddMinutes(-5),
                exp);
        }

        public string Issue(LicencePayload payload) => SignJson(payload.ToJsonBytes(), _key);

        public string Issue(string sub, IEnumerable<string>? ent = null, DateTimeOffset? nbf = null, DateTimeOffset? exp = null) =>
            Issue(Payload(sub, ent, nbf: nbf, exp: exp));

        /// <summary>Signs an arbitrary payload JSON text (for malformed-payload cases) with the ring key.</summary>
        public string IssueRaw(string payloadJson) => SignJson(Encoding.UTF8.GetBytes(payloadJson), _key);

        /// <summary>Signs raw payload bytes with the ring key.</summary>
        public string IssueRawBytes(byte[] payloadBytes) => SignJson(payloadBytes, _key);

        /// <summary>A structurally perfect key naming the ring's <c>kid</c>, signed by a DIFFERENT (foreign) P-256 key.</summary>
        public string SignWithForeignKeyUnderKid(LicencePayload payload)
        {
            using var foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return SignJson(payload.ToJsonBytes(), foreign);
        }

        private static string SignJson(byte[] payloadJson, ECDsa key)
        {
            var signingInput = LicencePayload.Prefix + "." + LicenceBase64Url.Encode(payloadJson);
            var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            return signingInput + "." + LicenceBase64Url.Encode(signature);
        }

        public void Dispose() => _key.Dispose();
    }

    /// <summary>A hand-rolled mutable clock (the <c>FeatureFlagCacheUnitTests.TestTime</c> shape; no testing package).</summary>
    internal sealed class MutableTime : TimeProvider
    {
        public MutableTime(DateTimeOffset now)
        {
            Now = now;
        }

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}