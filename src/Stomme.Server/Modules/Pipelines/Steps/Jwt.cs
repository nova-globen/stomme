#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>What <see cref="Jwt"/> does with a token.</summary>
    public enum JwtOperation
    {
        /// <summary>Makes a signed token of the claims.</summary>
        Sign,

        /// <summary>Reads a token's claims WITHOUT checking its signature - for a token already trusted, or to look inside one.</summary>
        Decode,

        /// <summary>Checks a token's signature, its algorithm, its times and - when given - its issuer and audience; reads its claims.</summary>
        Verify,
    }

    /// <summary>The JWS algorithms a token is signed with: HMAC with a shared secret, RSA or ECDSA with a key pair.</summary>
    public enum JwtAlgorithm
    {
        HS256,
        HS384,
        HS512,
        RS256,
        RS384,
        RS512,
        ES256,
        ES384,
        ES512,
    }

    /// <summary>
    /// JWT (BL-229, n8n's JWT node): signs claims into a token, decodes one, or verifies one - how a call to another system is
    /// authorised, or a token a caller sent is checked. HS256/384/512 under a shared secret (at least as long as the hash: 32, 48
    /// or 64 bytes - a shorter one is refused, as the .NET token handler refuses it); RS256/384/512 and ES256/384/512 under a PEM
    /// key - a private key to sign, a public key or a certificate to verify. A token is verified only under the algorithm the
    /// step names (its header saying another, "none" among them, is refused - the classic algorithm-confusion attack), then its
    /// expiry and not-before with the clock tolerance, then the issuer and audience when they are given. Sign adds "iat" (now),
    /// and "exp", "iss", "aud" and "sub" from its inputs where the claims do not have them. The result is the token (Sign), or the
    /// claims as JSON text (Decode, Verify), read with an expression - <c>{{ claims.sub }}</c>. A token that does not verify takes
    /// the step's Failed path (or faults the run), saying why. The key may hold a secret, so a run's data shows it hidden.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Transformations", "Sign claims into a JSON Web Token, decode one, or verify one's signature, times, issuer and audience.", DisplayName = "JWT")]
    public class Jwt : DataStep
    {
        [Input(Description = "Sign | Decode | Verify.", DefaultValue = JwtOperation.Sign, Order = 1)]
        public Input<JwtOperation> Operation { get; set; } = new(new Literal<JwtOperation>(JwtOperation.Sign));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Sign: the claims, a JSON object - {\"sub\": \"42\", \"role\": \"reader\"}. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Claims { get; set; }

        [Input(Description = "Decode and Verify: the token. Empty: the previous step's result.", Order = 3)]
        public Input<string?>? Token { get; set; }

        [Input(Description = "HS256 | HS384 | HS512 (a shared secret), RS256 | RS384 | RS512 (RSA) or ES256 | ES384 | ES512 (ECDSA) - a PEM key.", DefaultValue = JwtAlgorithm.HS256, Order = 4)]
        public Input<JwtAlgorithm> Algorithm { get; set; } = new(new Literal<JwtAlgorithm>(JwtAlgorithm.HS256));

        // One line, so the editor shows it hidden (it shows a multi-line input as a plain textarea): a PEM pasted there loses its
        // line breaks, which .NET reads all the same (measured - a repair written for it changed nothing and was removed).
        [Input(Description = "HS: the shared secret, at least 32, 48 or 64 bytes. RS and ES: a PEM key, pasted as it is - private to sign, public (or a certificate) to verify. Shown hidden here and in a run's data.", CanContainSecrets = true, Order = 5)]
        public Input<string?>? Key { get; set; }

        [Input(DisplayName = "Expires in", Description = "Sign: seconds until the token expires (its \"exp\"), unless the claims say. 0: no expiry.", DefaultValue = 3600, Order = 6)]
        public Input<int> ExpiresInSeconds { get; set; } = new(new Literal<int>(3600));

        [Input(Description = "Sign: its \"iss\", unless the claims say. Verify: the issuer the token must name. Empty: any.", Order = 7)]
        public Input<string?>? Issuer { get; set; }

        [Input(Description = "Sign: its \"aud\", unless the claims say. Verify: an audience the token must name. Empty: any.", Order = 8)]
        public Input<string?>? Audience { get; set; }

        [Input(Description = "Sign: its \"sub\", unless the claims say.", Order = 9)]
        public Input<string?>? Subject { get; set; }

        [Input(DisplayName = "Clock tolerance", Description = "Verify: seconds of difference between clocks allowed for its expiry and not-before.", DefaultValue = 0, Order = 10)]
        public Input<int> ClockToleranceSeconds { get; set; } = new(new Literal<int>(0));

        [Input(DisplayName = "Ignore expiry", Description = "Verify: an expired token still verifies - its signature is checked all the same.", Order = 11)]
        public Input<bool> IgnoreExpiration { get; set; } = new(new Literal<bool>(false));

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var operation = Operation.GetOrDefault(context);
            var algorithm = Algorithm.GetOrDefault(context);
            var now = DateTimeOffset.UtcNow;
            try
            {
                switch (operation)
                {
                    case JwtOperation.Sign:
                        var claims = JwtTokens.Claims(TextOrLastResult(context, Claims));
                        JwtTokens.AddDefaults(claims, now, ExpiresInSeconds.GetOrDefault(context), Issuer?.GetOrDefault(context), Audience?.GetOrDefault(context), Subject?.GetOrDefault(context));
                        await SucceedAsync(context, JwtTokens.Sign(claims, algorithm, Key?.GetOrDefault(context))).ConfigureAwait(false);
                        return;
                    case JwtOperation.Decode:
                        await SucceedAsync(context, JwtTokens.Decode(TextOrLastResult(context, Token)).Payload.ToJsonString()).ConfigureAwait(false);
                        return;
                    default:
                        var payload = JwtTokens.Verify(TextOrLastResult(context, Token), algorithm, Key?.GetOrDefault(context), now,
                            ClockToleranceSeconds.GetOrDefault(context), IgnoreExpiration.GetOrDefault(context), Issuer?.GetOrDefault(context), Audience?.GetOrDefault(context));
                        await SucceedAsync(context, payload.ToJsonString()).ConfigureAwait(false);
                        return;
                }
            }
            catch (JwtProblem problem)
            {
                await FailAsync(context, problem.Code, problem.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Why a token could not be made, read or trusted - its code (<c>JWT_INVALID</c>, <c>JWT_UNTRUSTED</c>) and the reason.</summary>
    public sealed class JwtProblem(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>The JWS compact form, signed and checked with .NET's own primitives - no token library, so nothing it trusts by default.</summary>
    public static class JwtTokens
    {
        public const string Invalid = "JWT_INVALID";
        public const string Untrusted = "JWT_UNTRUSTED";

        /// <summary>The claims to sign: a JSON object, or the refusal.</summary>
        public static JsonObject Claims(string? json)
        {
            try
            {
                return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject
                    ?? throw new JwtProblem(Invalid, "The claims are a JSON object - {\"sub\": \"42\"}.");
            }
            catch (JsonException)
            {
                throw new JwtProblem(Invalid, "The claims are a JSON object - {\"sub\": \"42\"}.");
            }
        }

        /// <summary>"iat" now, and "exp", "iss", "aud" and "sub" where the claims do not have them already.</summary>
        public static void AddDefaults(JsonObject claims, DateTimeOffset now, int expiresInSeconds, string? issuer, string? audience, string? subject)
        {
            claims.TryAdd("iat", now.ToUnixTimeSeconds());
            if (expiresInSeconds > 0)
            {
                claims.TryAdd("exp", now.ToUnixTimeSeconds() + expiresInSeconds);
            }

            if (!string.IsNullOrWhiteSpace(issuer))
            {
                claims.TryAdd("iss", issuer.Trim());
            }

            if (!string.IsNullOrWhiteSpace(audience))
            {
                claims.TryAdd("aud", audience.Trim());
            }

            if (!string.IsNullOrWhiteSpace(subject))
            {
                claims.TryAdd("sub", subject.Trim());
            }
        }

        public static string Sign(JsonObject claims, JwtAlgorithm algorithm, string? key)
        {
            var header = new JsonObject { ["alg"] = algorithm.ToString(), ["typ"] = "JWT" };
            var signingInput = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
            var data = Encoding.ASCII.GetBytes(signingInput);
            var signature = Family(algorithm) switch
            {
                'H' => Hmac(algorithm, SharedSecret(algorithm, key), data),
                'R' => WithRsa(key, algorithm, isPrivate: true, rsa => rsa.SignData(data, Hash(algorithm), RSASignaturePadding.Pkcs1)),
                _ => WithEcdsa(key, algorithm, isPrivate: true, ecdsa => ecdsa.SignData(data, Hash(algorithm), DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            };
            return signingInput + "." + Base64Url(signature);
        }

        /// <summary>A token's header and claims, its signature unchecked - or the refusal when it is no JWS compact token.</summary>
        public static (JsonObject Header, JsonObject Payload, byte[] Signature, string SigningInput) Decode(string? token)
        {
            var parts = (token ?? "").Trim().Split('.');
            if (parts.Length != 3)
            {
                throw new JwtProblem(Invalid, "A token is three parts joined by dots - header.claims.signature.");
            }

            try
            {
                var header = JsonNode.Parse(FromBase64Url(parts[0])) as JsonObject;
                var payload = JsonNode.Parse(FromBase64Url(parts[1])) as JsonObject;
                if (header is null || payload is null)
                {
                    throw new JwtProblem(Invalid, "A token's header and claims are JSON objects.");
                }

                return (header, payload, FromBase64Url(parts[2]), parts[0] + "." + parts[1]);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new JwtProblem(Invalid, "The token is not base64url-encoded JSON.");
            }
        }

        /// <summary>The claims of a token that verifies, or why it does not.</summary>
        public static JsonObject Verify(string? token, JwtAlgorithm algorithm, string? key, DateTimeOffset now, int toleranceSeconds, bool ignoreExpiration, string? issuer, string? audience)
        {
            var (header, payload, signature, signingInput) = Decode(token);
            var named = header["alg"] is JsonValue alg && alg.TryGetValue<string>(out var text) ? text : null;
            if (!string.Equals(named, algorithm.ToString(), StringComparison.Ordinal))
            {
                throw new JwtProblem(Untrusted, $"The token says it is signed with {named ?? "nothing"}; this step verifies {algorithm} only.");
            }

            var data = Encoding.ASCII.GetBytes(signingInput);
            bool good;
            try
            {
                good = Family(algorithm) switch
                {
                    'H' => CryptographicOperations.FixedTimeEquals(Hmac(algorithm, SharedSecret(algorithm, key), data), signature),
                    'R' => WithRsa(key, algorithm, isPrivate: false, rsa => rsa.VerifyData(data, signature, Hash(algorithm), RSASignaturePadding.Pkcs1)),
                    _ => WithEcdsa(key, algorithm, isPrivate: false, ecdsa => ecdsa.VerifyData(data, signature, Hash(algorithm), DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
                };
            }
            catch (CryptographicException)
            {
                good = false; // a signature of the wrong shape is a signature that does not match
            }
            if (!good)
            {
                throw new JwtProblem(Untrusted, "The token's signature does not match - it was not signed with this key, or it was changed.");
            }

            var tolerance = Math.Max(0, toleranceSeconds);
            var seconds = now.ToUnixTimeSeconds();
            if (!ignoreExpiration && Seconds(payload, "exp") is { } expires && seconds > expires + tolerance)
            {
                throw new JwtProblem(Untrusted, $"The token expired at {DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime:O}.");
            }

            if (Seconds(payload, "nbf") is { } notBefore && seconds + tolerance < notBefore)
            {
                throw new JwtProblem(Untrusted, $"The token is not valid before {DateTimeOffset.FromUnixTimeSeconds(notBefore).UtcDateTime:O}.");
            }

            if (!string.IsNullOrWhiteSpace(issuer) && !string.Equals(Text(payload["iss"]), issuer.Trim(), StringComparison.Ordinal))
            {
                throw new JwtProblem(Untrusted, $"The token's issuer is {Text(payload["iss"]) ?? "not named"}, not {issuer.Trim()}.");
            }

            if (!string.IsNullOrWhiteSpace(audience) && !Audiences(payload["aud"]).Contains(audience.Trim(), StringComparer.Ordinal))
            {
                throw new JwtProblem(Untrusted, $"The token is not for {audience.Trim()}.");
            }

            return payload;
        }

        private static char Family(JwtAlgorithm algorithm) => algorithm.ToString()[0];

        private static HashAlgorithmName Hash(JwtAlgorithm algorithm) => algorithm.ToString()[2..] switch
        {
            "384" => HashAlgorithmName.SHA384,
            "512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

        private static byte[] Hmac(JwtAlgorithm algorithm, byte[] key, byte[] data) => Hash(algorithm).Name switch
        {
            "SHA384" => HMACSHA384.HashData(key, data),
            "SHA512" => HMACSHA512.HashData(key, data),
            _ => HMACSHA256.HashData(key, data),
        };

        // A shared secret at least as long as the hash, as RFC 7518 asks and the .NET token handler holds it.
        private static byte[] SharedSecret(JwtAlgorithm algorithm, string? key)
        {
            var bytes = Encoding.UTF8.GetBytes(key ?? "");
            var least = int.Parse(algorithm.ToString()[2..], CultureInfo.InvariantCulture) / 8;
            if (bytes.Length < least)
            {
                throw new JwtProblem(Invalid, $"{algorithm} needs a secret of at least {least} bytes - this one has {bytes.Length}.");
            }

            return bytes;
        }

        private static T WithRsa<T>(string? pem, JwtAlgorithm algorithm, bool isPrivate, Func<RSA, T> use)
        {
            using var certificate = isPrivate ? null : Certificate(pem);
            using var rsa = certificate is null ? Read(RSA.Create(), pem, algorithm, isPrivate)
                : certificate.GetRSAPublicKey() ?? throw new JwtProblem(Invalid, $"The certificate holds no RSA key for {algorithm}.");
            if (rsa.KeySize < 2048)
            {
                throw new JwtProblem(Invalid, $"{algorithm} needs an RSA key of at least 2048 bits - this one has {rsa.KeySize}.");
            }

            return use(rsa);
        }

        private static T WithEcdsa<T>(string? pem, JwtAlgorithm algorithm, bool isPrivate, Func<ECDsa, T> use)
        {
            using var certificate = isPrivate ? null : Certificate(pem);
            using var ecdsa = certificate is null ? Read(ECDsa.Create(), pem, algorithm, isPrivate)
                : certificate.GetECDsaPublicKey() ?? throw new JwtProblem(Invalid, $"The certificate holds no ECDSA key for {algorithm}.");
            var curve = algorithm switch { JwtAlgorithm.ES384 => 384, JwtAlgorithm.ES512 => 521, _ => 256 };
            if (ecdsa.KeySize != curve)
            {
                throw new JwtProblem(Invalid, $"{algorithm} needs a P-{curve} key - this one is {ecdsa.KeySize} bits.");
            }

            return use(ecdsa);
        }

        // A certificate to verify with, when the PEM is one.
        private static X509Certificate2? Certificate(string? pem)
        {
            if (pem is null || !pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return X509Certificate2.CreateFromPem(pem);
            }
            catch (CryptographicException)
            {
                throw new JwtProblem(Invalid, "The certificate could not be read.");
            }
        }

        private static TKey Read<TKey>(TKey key, string? pem, JwtAlgorithm algorithm, bool isPrivate)
            where TKey : AsymmetricAlgorithm
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN", StringComparison.Ordinal))
                {
                    throw new JwtProblem(Invalid, $"{algorithm} needs a PEM key - {(isPrivate ? "a private key to sign" : "a public key or a certificate to verify")}.");
                }

                if (isPrivate && !pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                {
                    throw new JwtProblem(Invalid, $"{algorithm} signs with a PRIVATE key - this is a public one.");
                }

                key.ImportFromPem(pem);
                return key;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                key.Dispose();
                throw new JwtProblem(Invalid, $"The key is not {(algorithm.ToString()[0] == 'R' ? "an RSA" : "an ECDSA")} key this step can read.");
            }
            catch (JwtProblem)
            {
                key.Dispose();
                throw;
            }
        }

        private static long? Seconds(JsonObject payload, string claim) =>
            payload[claim] is JsonValue value && (value.TryGetValue<long>(out var whole) ? whole : value.TryGetValue<double>(out var real) ? (long)real : (long?)null) is { } seconds ? seconds : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string[] Audiences(JsonNode? node) => node switch
        {
            JsonArray list => list.Select(Text).Where(text => text is not null).ToArray()!,
            _ => Text(node) is { } one ? new[] { one } : Array.Empty<string>(),
        };

        public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string text)
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
        }
    }
}
