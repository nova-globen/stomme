#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>
    /// The signed payload of an <c>SLK1</c> offline licence key (D105 "The key"). The wire form is
    /// <c>SLK1.&lt;base64url(payload JSON)&gt;.&lt;base64url(P1363 ECDSA P-256/SHA-256 signature)&gt;</c>, the signature
    /// taken over the ASCII bytes of <c>SLK1.&lt;payloadB64&gt;</c> exactly as issued — the payload is PARSED from those
    /// bytes and never re-serialised for verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This file is compiled into BOTH <c>Stomme.Server</c> (the verifier) and <c>tools/Stomme.LicenceIssuer</c>
    /// (by <c>&lt;Compile Include … Link&gt;</c>, not a project reference), so the JSON field names, the limits and the
    /// canonical-capability rule have exactly one definition. It contains NO signing code: signing lives only in the
    /// issuer tool.
    /// </para>
    /// <para>
    /// JSON shape (all fields required, no others allowed, no duplicate names): <c>v</c> (int, 1), <c>kid</c>
    /// (string), <c>lid</c> (Guid, "D" format), <c>sub</c> (the InstallationId, exact), <c>lbl</c> (display only),
    /// <c>ent</c> (array of canonical capability keys, no <c>*</c>, no duplicates, at most 256), <c>lim</c> (object,
    /// metric → integer, <c>-1</c> = unlimited), <c>iat</c>/<c>nbf</c> (Unix seconds, UTC), <c>exp</c> (Unix seconds
    /// or <c>null</c> = perpetual).
    /// </para>
    /// </remarks>
    internal sealed record LicencePayload(
        int V,
        string Kid,
        Guid Lid,
        string Sub,
        string Lbl,
        IReadOnlyList<string> Ent,
        IReadOnlyDictionary<string, long> Lim,
        DateTimeOffset Iat,
        DateTimeOffset Nbf,
        DateTimeOffset? Exp)
    {
        public const string Prefix = "SLK1";
        public const int CurrentVersion = 1;
        public const int MaxBlobBytes = 8192;
        public const int MaxEntitlements = 256;
        public const int SignatureBytes = 64;

        /// <summary>
        /// The canonical capability charset, as D105 words it. <see cref="IsCanonicalCapability"/> does NOT evaluate this
        /// with <c>Regex.IsMatch</c>: .NET's <c>$</c> also matches before a trailing <c>\n</c>, so the regex as written
        /// would accept <c>"blogs.pro\n"</c> — a key that could never equal a descriptor value. The check is a char loop.
        /// </summary>
        public const string CanonicalCapabilityPattern = "^[a-z0-9._:-]+$";

        /// <summary>The value of <c>lim</c> meaning "unlimited" (D102 decodes it; the licence passes it through).</summary>
        public const long UnlimitedLimit = -1;

        internal static class Fields
        {
            public const string V = "v";
            public const string Kid = "kid";
            public const string Lid = "lid";
            public const string Sub = "sub";
            public const string Lbl = "lbl";
            public const string Ent = "ent";
            public const string Lim = "lim";
            public const string Iat = "iat";
            public const string Nbf = "nbf";
            public const string Exp = "exp";

            public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
            {
                V, Kid, Lid, Sub, Lbl, Ent, Lim, Iat, Nbf, Exp
            };
        }

        /// <summary>True iff <paramref name="value"/> is non-empty and every char is in <c>[a-z0-9._:-]</c>.</summary>
        public static bool IsCanonicalCapability(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var ch in value)
            {
                var ok = ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The shape rules shared by the verifier and the issuer: version, entitlement count, canonical entries, no
        /// duplicates, and integer limits no lower than <see cref="UnlimitedLimit"/>. Returns a description of the first
        /// violation, or <c>null</c> when the shape is acceptable.
        /// </summary>
        public static string? FindShapeViolation(
            int version,
            string? kid,
            string? sub,
            IReadOnlyList<string> entitlements,
            IReadOnlyDictionary<string, long> limits)
        {
            if (version != CurrentVersion)
            {
                return $"unsupported version {version}";
            }

            if (string.IsNullOrEmpty(kid))
            {
                return "kid is empty";
            }

            if (string.IsNullOrEmpty(sub))
            {
                return "sub is empty";
            }

            if (entitlements.Count > MaxEntitlements)
            {
                return $"more than {MaxEntitlements} entitlements";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entitlement in entitlements)
            {
                if (!IsCanonicalCapability(entitlement))
                {
                    return "an entitlement is not a canonical capability key";
                }

                if (!seen.Add(entitlement))
                {
                    return "an entitlement is listed twice";
                }
            }

            foreach (var limit in limits)
            {
                if (string.IsNullOrEmpty(limit.Key))
                {
                    return "a limit has an empty metric name";
                }

                if (limit.Value < UnlimitedLimit)
                {
                    return "a limit is below -1";
                }
            }

            return null;
        }

        /// <summary>
        /// The payload JSON in the fixed field order. Used by the issuer (and the test-side issuer) to produce the bytes
        /// that are base64url-encoded and signed. The verifier never calls this.
        /// </summary>
        public byte[] ToJsonBytes()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber(Fields.V, V);
                writer.WriteString(Fields.Kid, Kid);
                writer.WriteString(Fields.Lid, Lid.ToString("D"));
                writer.WriteString(Fields.Sub, Sub);
                writer.WriteString(Fields.Lbl, Lbl);
                writer.WriteStartArray(Fields.Ent);
                foreach (var entitlement in Ent)
                {
                    writer.WriteStringValue(entitlement);
                }

                writer.WriteEndArray();
                writer.WriteStartObject(Fields.Lim);
                foreach (var limit in Lim)
                {
                    writer.WriteNumber(limit.Key, limit.Value);
                }

                writer.WriteEndObject();
                writer.WriteNumber(Fields.Iat, Iat.ToUnixTimeSeconds());
                writer.WriteNumber(Fields.Nbf, Nbf.ToUnixTimeSeconds());
                if (Exp is { } exp)
                {
                    writer.WriteNumber(Fields.Exp, exp.ToUnixTimeSeconds());
                }
                else
                {
                    writer.WriteNull(Fields.Exp);
                }

                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
    }

    /// <summary>
    /// Strict base64url (RFC 4648 §5): alphabet <c>A-Z a-z 0-9 - _</c>, NO padding, and the CANONICAL encoding only
    /// (a segment whose unused trailing bits are non-zero is rejected, so one byte string has one spelling).
    /// </summary>
    internal static class LicenceBase64Url
    {
        public static string Encode(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static bool TryDecode(string? text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(text) || text.Length % 4 == 1)
            {
                return false;
            }

            foreach (var ch in text)
            {
                var ok = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
                if (!ok)
                {
                    return false;
                }
            }

            var standard = text.Replace('-', '+').Replace('_', '/');
            standard = standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');
            var buffer = new byte[(standard.Length / 4) * 3];
            if (!Convert.TryFromBase64String(standard, buffer, out var written))
            {
                return false;
            }

            var decoded = buffer.AsSpan(0, written).ToArray();
            if (!string.Equals(Encode(decoded), text, StringComparison.Ordinal))
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
    }
}