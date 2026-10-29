#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>
    /// The COMPILED-IN public keys an <c>SLK1</c> licence may be signed under, by <c>kid</c> → P-256 SubjectPublicKeyInfo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is deliberately NO <c>IConfiguration</c>, <c>IOptions</c> or environment input and NO ephemeral fallback:
    /// a key ring an operator could extend by configuration is a ring the Host role (which can write any config key,
    /// <c>SystemController.Post</c>) could forge licences for. There is NO private material here and no signing code
    /// anywhere under <c>src</c>; the private halves are kept only in the company's secret store and used only by
    /// <c>tools/Stomme.LicenceIssuer</c>. Rotation = a new <c>kid</c> added in a release, the old one kept until the
    /// licences signed under it have expired.
    /// </para>
    /// <para>
    /// <see cref="OverrideForTests"/> is the internal ring-substitution seam binding item 11 requires: reachable from
    /// <c>Stomme.Tests</c> only through <c>InternalsVisibleTo</c> (<c>Stomme.Server.csproj</c>), never a DI
    /// registration or a configuration input. A REFLECTIVE caller running inside the host process could also reach it;
    /// that is outside the design's scope (the threat model is accidental reuse, not code running in the process,
    /// which could equally patch the IL). <c>LicenceKeyRingSecretGateTests</c> pins that the production ring and the
    /// test ring share no <c>kid</c> and no key bytes.
    /// </para>
    /// </remarks>
    internal static class LicenceKeyRing
    {
        private const string NistP256Oid = "1.2.840.10045.3.1.7";

        private static readonly object OverrideGate = new();
        private static IReadOnlyDictionary<string, byte[]>? _override;

        /// <summary>The shipped ring. Entries are base64 SPKI literals; nothing else may populate it.</summary>
        internal static IReadOnlyDictionary<string, byte[]> Production { get; } =
            new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                // stomme-2026-1: generated with tools/Stomme.LicenceIssuer keygen. Private half: the secret store only.
                ["stomme-2026-1"] = Convert.FromBase64String(
                    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjIN5Uwll4SKA/BDE4vYnIH1Mf+aJZTOoCzVu6BjSmFYRM1pbh3REC47+2e+YQISSITOYg0r00wzGITs470ap9g=="),
            });

        /// <summary>The ring in force: the test override when one is active, otherwise <see cref="Production"/>.</summary>
        internal static IReadOnlyDictionary<string, byte[]> Current
        {
            get
            {
                lock (OverrideGate)
                {
                    return _override ?? Production;
                }
            }
        }

        /// <summary>
        /// Replaces <see cref="Current"/> process-wide until the returned handle is disposed. Only one override may be
        /// active at a time — a second concurrent override THROWS rather than silently interleaving two tests' rings, so
        /// tests that use it must not run in parallel with each other. Every entry must load as a P-256 public key.
        /// </summary>
        internal static IDisposable OverrideForTests(IReadOnlyDictionary<string, byte[]> ring)
        {
            ArgumentNullException.ThrowIfNull(ring);
            foreach (var entry in ring)
            {
                using var _ = LoadPublic(entry.Value);
            }

            var copy = new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>(ring, StringComparer.Ordinal));
            lock (OverrideGate)
            {
                if (_override is not null)
                {
                    throw new InvalidOperationException("A licence key ring test override is already active.");
                }

                _override = copy;
            }

            return new OverrideHandle(copy);
        }

        /// <summary>
        /// Imports a SubjectPublicKeyInfo as an <see cref="ECDsa"/> public key, rejecting trailing bytes and any curve
        /// other than NIST P-256. The caller owns (and must dispose) the returned instance.
        /// </summary>
        internal static ECDsa LoadPublic(byte[] spki)
        {
            ArgumentNullException.ThrowIfNull(spki);
            var key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(spki, out var read);
                if (read != spki.Length)
                {
                    throw new CryptographicException("The licence public key has trailing bytes after its SubjectPublicKeyInfo.");
                }

                var curve = key.ExportParameters(includePrivateParameters: false).Curve;
                if (!IsNistP256(curve) || key.KeySize != 256)
                {
                    throw new CryptographicException("A licence public key must be on the NIST P-256 curve.");
                }

                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        private static bool IsNistP256(ECCurve curve)
        {
            if (!curve.IsNamed)
            {
                return false;
            }

            return curve.Oid.Value == NistP256Oid
                || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1";
        }

        private sealed class OverrideHandle : IDisposable
        {
            private IReadOnlyDictionary<string, byte[]>? _installed;

            public OverrideHandle(IReadOnlyDictionary<string, byte[]> installed)
            {
                _installed = installed;
            }

            public void Dispose()
            {
                lock (OverrideGate)
                {
                    if (_installed is not null && ReferenceEquals(_override, _installed))
                    {
                        _override = null;
                    }

                    _installed = null;
                }
            }
        }
    }
}