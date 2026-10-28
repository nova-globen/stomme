#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// AWS Signature Version 4 (BL-229, the AWS connectors): a request signed for a service in a region with an access key and its
    /// secret - and a session token when the keys are temporary - over the connection's own egress client, never the AWS SDK's,
    /// so the host's egress policy holds. The canonical request, the string to sign and the derived key as AWS documents them.
    /// </summary>
    public static class AwsSigV4
    {
        public const string Algorithm = "AWS4-HMAC-SHA256";

        /// <summary>Signs <paramref name="request"/> in place: its <c>x-amz-date</c>, its session token, its payload hash when asked, its <c>Authorization</c>.</summary>
        public static void Sign(HttpRequestMessage request, byte[] payload, string region, string service, string accessKey, string secretKey, string? sessionToken, DateTimeOffset now, bool signPayloadHeader = false) =>
            Sign(request, Hex(SHA256.HashData(payload)), region, service, accessKey, secretKey, sessionToken, now, signPayloadHeader);

        /// <summary>Signs <paramref name="request"/> with its payload's hex SHA-256 given - hashed beforehand from a body that is streamed, not held (an S3 upload).</summary>
        public static void Sign(HttpRequestMessage request, string payloadHash, string region, string service, string accessKey, string secretKey, string? sessionToken, DateTimeOffset now, bool signPayloadHeader = false)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrEmpty(payloadHash);
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no address.");
            var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var day = amzDate[..8];
            request.Headers.Remove("x-amz-date");
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            if (!string.IsNullOrEmpty(sessionToken))
            {
                request.Headers.Remove("x-amz-security-token");
                request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
            }

            if (signPayloadHeader)
            {
                request.Headers.Remove("x-amz-content-sha256");
                request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            }

            // The signed headers: host, every x-amz-* header and the content type, lower-cased, sorted, their values trimmed.
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var header in request.Headers)
            {
                var name = header.Key.ToLowerInvariant();
                if (name.StartsWith("x-amz-", StringComparison.Ordinal))
                {
                    headers[name] = string.Join(",", header.Value.Select(Trimmed));
                }
            }

            if (request.Content?.Headers.ContentType is { } type)
            {
                headers["content-type"] = Trimmed(type.ToString());
            }

            var signedHeaders = string.Join(";", headers.Keys);
            var canonical = string.Join("\n",
                request.Method.Method,
                CanonicalPath(uri.AbsolutePath, service),
                CanonicalQuery(uri.Query),
                string.Concat(headers.Select(header => header.Key + ":" + header.Value + "\n")),
                signedHeaders,
                payloadHash);
            var scope = $"{day}/{region}/{service}/aws4_request";
            var toSign = string.Join("\n", Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
            var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day), region), service), "aws4_request");
            var signature = Hex(Hmac(key, toSign));
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        }

        // The path each segment URI-encoded; services other than S3 encode it once more, as AWS documents.
        private static string CanonicalPath(string absolutePath, string service)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return "/";
            }

            var segments = absolutePath.Split('/').Select(segment => Encode(Uri.UnescapeDataString(segment)));
            var once = string.Join("/", segments);
            return service == "s3" ? once : string.Join("/", once.Split('/').Select(segment => Encode(segment)));
        }

        // The query its pairs encoded and sorted by name, then value.
        private static string CanonicalQuery(string query)
        {
            if (string.IsNullOrEmpty(query) || query == "?")
            {
                return "";
            }

            return string.Join("&", query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(pair => (Name: Encode(Uri.UnescapeDataString(pair[0])), Value: Encode(Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : ""))))
                .OrderBy(pair => pair.Name, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => pair.Name + "=" + pair.Value));
        }

        // RFC 3986: letters, digits and -._~ kept, everything else percent-encoded in upper-case hexadecimal.
        public static string Encode(string value)
        {
            var encoded = new StringBuilder(value.Length * 2);
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
                {
                    encoded.Append(c);
                }
                else
                {
                    encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            return encoded.ToString();
        }

        private static string Trimmed(string value) => string.Join(" ", value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

        /// <summary>Lower-case hexadecimal, as SigV4 writes a hash.</summary>
        public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
