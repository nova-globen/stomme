#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>Why an <c>SLK1</c> licence key is or is not in force (D105 design item 5).</summary>
    internal enum LicenceKeyReason
    {
        Valid,
        Malformed,
        UnknownKey,
        BadSignature,
        WrongInstallation,
        NoInstallationId,
        NotYetValid,
        Expired,

        /// <summary>
        /// NEVER produced by <see cref="LicenceKeyVerifier"/>: reported by <see cref="OfflineLicenceEntitlementProvider"/>
        /// when an otherwise-valid key sits under a <c>Licensing:Keys:&lt;name&gt;</c> entry whose name is not its signed
        /// <c>lid</c> (a copied or mis-pasted entry), so it grants nothing.
        /// </summary>
        LidMismatch
    }

    /// <summary>
    /// A verification outcome. <see cref="Payload"/> is present whenever the payload PARSED — including for
    /// <see cref="LicenceKeyReason.UnknownKey"/>, <see cref="LicenceKeyReason.BadSignature"/> and the installation/time
    /// reasons, so a status page can name the <c>lid</c> and expiry of a key it rejects — but its contents are
    /// AUTHENTIC only when <see cref="IsValid"/> (or the reason is a validity-window/installation reason reached after the
    /// signature check). Never grant anything from a payload whose reason is not <see cref="LicenceKeyReason.Valid"/>.
    /// </summary>
    internal sealed record LicenceVerification(LicenceKeyReason Reason, LicencePayload? Payload)
    {
        public bool IsValid => Reason == LicenceKeyReason.Valid;
    }

    /// <summary>
    /// The pure, synchronous <c>SLK1</c> verifier (D105 design item 5; D33-clean — no async, no blocking, no logger, so a
    /// key can never be logged from here).
    /// </summary>
    /// <remarks>
    /// Order of checks, each short-circuiting: (1) the envelope — non-empty, ≤ 8 KB UTF-8, exactly three dot-separated
    /// segments, the exact prefix <c>SLK1</c>, strict canonical base64url in both segments, a 64-byte signature →
    /// <see cref="LicenceKeyReason.Malformed"/>; (2) the payload JSON — an object, no duplicate or unknown property
    /// names, every required field present with the right type, <c>v</c> = 1, the <c>ent</c>/<c>lim</c> shape rules →
    /// <see cref="LicenceKeyReason.Malformed"/>; (3) <c>kid</c> in the ring → else <see cref="LicenceKeyReason.UnknownKey"/>;
    /// (4) ECDSA P-256/SHA-256 (IEEE P1363) over the ASCII bytes of <c>SLK1.&lt;payloadB64&gt;</c> AS RECEIVED — never a
    /// re-serialisation → else <see cref="LicenceKeyReason.BadSignature"/>; (5) an empty installation id →
    /// <see cref="LicenceKeyReason.NoInstallationId"/> (AFTER the signature, and never reported as a wrong installation);
    /// (6) <c>sub</c> ≠ installation id (ordinal) → <see cref="LicenceKeyReason.WrongInstallation"/>; (7) now &lt; <c>nbf</c>
    /// → <see cref="LicenceKeyReason.NotYetValid"/>; (8) <c>exp</c> set and now ≥ <c>exp</c> →
    /// <see cref="LicenceKeyReason.Expired"/>. Steps 1-6 are <see cref="VerifyUntimed"/> (what <see cref="LicenceState"/>
    /// caches); steps 7-8 are <see cref="ApplyValidityWindow"/> (re-run on every read).
    /// </remarks>
    internal static class LicenceKeyVerifier
    {
        public static LicenceVerification Verify(string blob, string installationId, DateTimeOffset utcNow) =>
            Verify(blob, installationId, utcNow, LicenceKeyRing.Current);

        internal static LicenceVerification Verify(
            string? blob,
            string? installationId,
            DateTimeOffset utcNow,
            IReadOnlyDictionary<string, byte[]> ring) =>
            ApplyValidityWindow(VerifyUntimed(blob, installationId, ring), utcNow);

        /// <summary>Checks 1-6: envelope, payload, key, signature, installation. Independent of the clock.</summary>
        internal static LicenceVerification VerifyUntimed(
            string? blob,
            string? installationId,
            IReadOnlyDictionary<string, byte[]> ring)
        {
            ArgumentNullException.ThrowIfNull(ring);

            // (1) Envelope.
            if (string.IsNullOrEmpty(blob)
                || blob.Length > LicencePayload.MaxBlobBytes
                || Encoding.UTF8.GetByteCount(blob) > LicencePayload.MaxBlobBytes)
            {
                return Malformed();
            }

            var segments = blob.Split('.');
            if (segments.Length != 3 || !string.Equals(segments[0], LicencePayload.Prefix, StringComparison.Ordinal))
            {
                return Malformed();
            }

            if (!LicenceBase64Url.TryDecode(segments[1], out var payloadBytes)
                || !LicenceBase64Url.TryDecode(segments[2], out var signature)
                || signature.Length != LicencePayload.SignatureBytes)
            {
                return Malformed();
            }

            // (2) Payload.
            var payload = TryParsePayload(payloadBytes);
            if (payload is null)
            {
                return Malformed();
            }

            // (3) Key.
            if (!ring.TryGetValue(payload.Kid, out var spki))
            {
                return new LicenceVerification(LicenceKeyReason.UnknownKey, payload);
            }

            // (4) Signature over the bytes exactly as received. segments[1] passed the base64url alphabet check, so the
            // signing input is pure ASCII and ASCII.GetBytes is lossless.
            var signingInput = Encoding.ASCII.GetBytes(LicencePayload.Prefix + "." + segments[1]);
            bool signatureOk;
            using (var key = LicenceKeyRing.LoadPublic(spki))
            {
                signatureOk = key.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }

            if (!signatureOk)
            {
                return new LicenceVerification(LicenceKeyReason.BadSignature, payload);
            }

            // (5) + (6) Installation binding.
            if (string.IsNullOrEmpty(installationId))
            {
                return new LicenceVerification(LicenceKeyReason.NoInstallationId, payload);
            }

            if (!string.Equals(payload.Sub, installationId, StringComparison.Ordinal))
            {
                return new LicenceVerification(LicenceKeyReason.WrongInstallation, payload);
            }

            return new LicenceVerification(LicenceKeyReason.Valid, payload);
        }

        /// <summary>Checks 7-8 against <paramref name="utcNow"/>, applied only to an otherwise-valid result.</summary>
        internal static LicenceVerification ApplyValidityWindow(LicenceVerification untimed, DateTimeOffset utcNow)
        {
            ArgumentNullException.ThrowIfNull(untimed);
            if (untimed.Reason != LicenceKeyReason.Valid || untimed.Payload is null)
            {
                return untimed;
            }

            if (utcNow < untimed.Payload.Nbf)
            {
                return new LicenceVerification(LicenceKeyReason.NotYetValid, untimed.Payload);
            }

            if (untimed.Payload.Exp is { } exp && utcNow >= exp)
            {
                return new LicenceVerification(LicenceKeyReason.Expired, untimed.Payload);
            }

            return untimed;
        }

        private static LicenceVerification Malformed() => new(LicenceKeyReason.Malformed, null);

        private static LicencePayload? TryParsePayload(byte[] json)
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // Duplicate names are checked BY HAND on the unescaped name: JsonDocument keeps both and TryGetProperty
                // would silently pick one, so "which kid?" would otherwise be answerable two ways.
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!names.Add(property.Name) || !LicencePayload.Fields.All.Contains(property.Name))
                    {
                        return null;
                    }
                }

                if (names.Count != LicencePayload.Fields.All.Count)
                {
                    return null; // a required field is missing
                }

                if (!TryInt32(root, LicencePayload.Fields.V, out var version)
                    || !TryString(root, LicencePayload.Fields.Kid, out var kid)
                    || !TryString(root, LicencePayload.Fields.Lid, out var lidText)
                    || !Guid.TryParseExact(lidText, "D", out var lid)
                    || !TryString(root, LicencePayload.Fields.Sub, out var sub)
                    || !TryString(root, LicencePayload.Fields.Lbl, out var lbl)
                    || !TryUnixSeconds(root.GetProperty(LicencePayload.Fields.Iat), out var iat)
                    || !TryUnixSeconds(root.GetProperty(LicencePayload.Fields.Nbf), out var nbf))
                {
                    return null;
                }

                DateTimeOffset? exp = null;
                var expElement = root.GetProperty(LicencePayload.Fields.Exp);
                if (expElement.ValueKind != JsonValueKind.Null)
                {
                    if (!TryUnixSeconds(expElement, out var expValue))
                    {
                        return null;
                    }

                    exp = expValue;
                }

                var entElement = root.GetProperty(LicencePayload.Fields.Ent);
                if (entElement.ValueKind != JsonValueKind.Array || entElement.GetArrayLength() > LicencePayload.MaxEntitlements)
                {
                    return null;
                }

                var entitlements = new List<string>(entElement.GetArrayLength());
                foreach (var item in entElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    entitlements.Add(item.GetString()!);
                }

                var limElement = root.GetProperty(LicencePayload.Fields.Lim);
                if (limElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var limits = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var limit in limElement.EnumerateObject())
                {
                    if (limit.Value.ValueKind != JsonValueKind.Number
                        || !limit.Value.TryGetInt64(out var value)
                        || !limits.TryAdd(limit.Name, value))
                    {
                        return null;
                    }
                }

                if (LicencePayload.FindShapeViolation(version, kid, sub, entitlements, limits) is not null)
                {
                    return null;
                }

                return new LicencePayload(version, kid, lid, sub, lbl, entitlements.AsReadOnly(), limits, iat, nbf, exp);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool TryString(JsonElement root, string name, out string value)
        {
            var element = root.GetProperty(name);
            value = element.ValueKind == JsonValueKind.String ? element.GetString()! : string.Empty;
            return element.ValueKind == JsonValueKind.String;
        }

        private static bool TryInt32(JsonElement root, string name, out int value)
        {
            value = 0;
            var element = root.GetProperty(name);
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
        }

        private static bool TryUnixSeconds(JsonElement element, out DateTimeOffset value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var seconds)
                || seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                return false;
            }

            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
    }
}