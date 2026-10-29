#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// JWT and TOTP (n8n's JWT and TOTP nodes), run by the engine as the host composes it. JWT: tokens it signs with HS,
    /// RS and ES keys validate with Microsoft's own token handler and tokens that handler signs verify here; a token under another
    /// algorithm than the step names - "none" among them - a changed one, an expired one, one from another issuer or for another
    /// audience is refused saying why; a short secret and a public key to sign with refused. TOTP: RFC 6238's own test vectors,
    /// the otpauth:// link, a code checked a period either side and not beyond, and what it refuses.
    /// </summary>
    public sealed class JwtAndTotpTests : IAsyncLifetime
    {
        private const string Secret32 = "a-shared-secret-of-thirty-two-by";
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new Silent());
                module.AddStepsFrom<PipelineStepsMarker>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class Silent : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => TextWriter.Null;
        }

        private async Task<(RunPipelineResult Run, string Result, string? Error)> RunAsync(DataStep step)
        {
            using var scope = _services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
            var outputs = run.Journal.StepExecutionContexts.Single(c => c.Step.GetType() == step.GetType()).GetOutputs();
            return (run, outputs.TryGetValue("Result", out var result) ? (string)result! : "", outputs.TryGetValue("Error", out var error) ? error as string : null);
        }

        private static async Task<TokenValidationResult> MicrosoftValidatesAsync(string token, SecurityKey key, string algorithm) =>
            await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                IssuerSigningKey = key, ValidAlgorithms = new[] { algorithm }, ValidIssuer = "https://site.example", ValidAudience = "orders-api",
            });

        [Fact]
        public async Task A_token_it_signs_validates_with_microsofts_handler_and_one_that_signs_verifies_here()
        {
            using var rsa = RSA.Create(2048);
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keys = new (JwtAlgorithm Algorithm, string SignWith, string VerifyWith, SecurityKey Microsoft)[]
            {
                (JwtAlgorithm.HS256, Secret32, Secret32, new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret32))),
                (JwtAlgorithm.RS256, rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem(), new RsaSecurityKey(rsa)),
                (JwtAlgorithm.ES256, ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem(), new ECDsaSecurityKey(ecdsa)),
            };
            foreach (var (algorithm, signWith, verifyWith, microsoft) in keys)
            {
                var (_, token, _) = await RunAsync(new Jwt
                {
                    Claims = new("{\"role\": \"reader\"}"), Algorithm = new(algorithm), Key = new(signWith),
                    Issuer = new("https://site.example"), Audience = new("orders-api"), Subject = new("42"),
                });
                var validated = await MicrosoftValidatesAsync(token, microsoft, algorithm.ToString());
                Assert.True(validated.IsValid, algorithm + ": " + validated.Exception?.Message);
                Assert.Equal(("reader", "42"), (validated.Claims["role"].ToString(), validated.Claims["sub"].ToString()));
                Assert.True(validated.Claims.ContainsKey("exp") && validated.Claims.ContainsKey("iat"));

                // The other way: Microsoft's handler signs, this step verifies - and reads the claims.
                var theirs = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = "https://site.example", Audience = "orders-api", Expires = DateTime.UtcNow.AddMinutes(5),
                    Claims = new Dictionary<string, object> { ["sub"] = "7" },
                    SigningCredentials = new SigningCredentials(microsoft, algorithm.ToString()),
                });
                var (verified, claims, error) = await RunAsync(new Jwt
                {
                    Operation = new(JwtOperation.Verify), Token = new(theirs), Algorithm = new(algorithm), Key = new(verifyWith),
                    Issuer = new("https://site.example"), Audience = new("orders-api"),
                });
                Assert.True(verified.PipelineState.SubStatus == PipelineSubStatus.Finished, algorithm + ": " + error);
                Assert.Equal("7", JsonNode.Parse(claims)!["sub"]!.GetValue<string>());
            }

            // The key's field is one line, so a pasted PEM arrives without its line breaks - and still signs and verifies (.NET reads
            // it so; measured: a repair that put the breaks back changed nothing).
            string OneLine(string pem) => pem.Replace("\r", "").Replace("\n", "");
            var (_, pasted, pastedError) = await RunAsync(new Jwt { Algorithm = new(JwtAlgorithm.RS256), Key = new(OneLine(rsa.ExportPkcs8PrivateKeyPem())), Issuer = new("https://site.example"), Audience = new("orders-api") });
            Assert.True((await MicrosoftValidatesAsync(pasted, new RsaSecurityKey(rsa), "RS256")).IsValid, pastedError);
            var (checkedPasted, _, _) = await RunAsync(new Jwt { Operation = new(JwtOperation.Verify), Token = new(pasted), Algorithm = new(JwtAlgorithm.RS256), Key = new(OneLine(rsa.ExportSubjectPublicKeyInfoPem())) });
            Assert.Equal(PipelineSubStatus.Finished, checkedPasted.PipelineState.SubStatus);
        }

        [Fact]
        public async Task A_token_that_should_not_be_trusted_is_refused_saying_why()
        {
            var (_, token, _) = await RunAsync(new Jwt { Claims = new("{\"sub\": \"42\"}"), Key = new(Secret32), Issuer = new("https://site.example"), Audience = new("orders-api") });
            async Task<string?> Refusal(string candidate, string? issuer = null, string? audience = null, JwtAlgorithm algorithm = JwtAlgorithm.HS256, string key = Secret32)
            {
                var (run, _, error) = await RunAsync(new Jwt
                {
                    Operation = new(JwtOperation.Verify), Token = new(candidate), Algorithm = new(algorithm), Key = new(key),
                    Issuer = new(issuer), Audience = new(audience), OnFailure = new(FailurePolicy.Continue),
                });
                return run.PipelineState.SubStatus == PipelineSubStatus.Finished ? error : "faulted";
            }

            Assert.Null(await Refusal(token, "https://site.example", "orders-api"));
            var parts = token.Split('.');

            // "none" - no signature at all - and a header naming another algorithm than the step's.
            var none = JwtTokens.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}")) + "." + parts[1] + ".";
            Assert.Equal("JWT_UNTRUSTED: The token says it is signed with none; this step verifies HS256 only.", await Refusal(none));
            Assert.StartsWith("JWT_UNTRUSTED: The token says it is signed with HS256; this step verifies HS512 only.",
                await Refusal(token, algorithm: JwtAlgorithm.HS512, key: Secret32 + Secret32));

            // Claims changed after signing, or another key.
            var changed = parts[0] + "." + JwtTokens.Base64Url(Encoding.UTF8.GetBytes("{\"sub\":\"1\"}")) + "." + parts[2];
            Assert.StartsWith("JWT_UNTRUSTED: The token's signature does not match", await Refusal(changed));
            Assert.StartsWith("JWT_UNTRUSTED: The token's signature does not match", await Refusal(token, key: "another-shared-secret-of-32-bytes"));

            // Another issuer, another audience.
            Assert.Equal("JWT_UNTRUSTED: The token's issuer is https://site.example, not https://elsewhere.example.", await Refusal(token, issuer: "https://elsewhere.example"));
            Assert.Equal("JWT_UNTRUSTED: The token is not for billing-api.", await Refusal(token, audience: "billing-api"));

            // Expired - unless the step ignores expiry, or its tolerance reaches.
            var (_, expired, _) = await RunAsync(new Jwt { Claims = new("{\"exp\": " + DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds() + "}"), Key = new(Secret32) });
            Assert.StartsWith("JWT_UNTRUSTED: The token expired at ", await Refusal(expired));
            var (tolerated, _, _) = await RunAsync(new Jwt { Operation = new(JwtOperation.Verify), Token = new(expired), Key = new(Secret32), ClockToleranceSeconds = new(60) });
            Assert.Equal(PipelineSubStatus.Finished, tolerated.PipelineState.SubStatus);
            var (ignored, _, _) = await RunAsync(new Jwt { Operation = new(JwtOperation.Verify), Token = new(expired), Key = new(Secret32), IgnoreExpiration = new(true) });
            Assert.Equal(PipelineSubStatus.Finished, ignored.PipelineState.SubStatus);

            // Decode reads any token, unchecked; something that is no token is refused.
            var (_, decoded, _) = await RunAsync(new Jwt { Operation = new(JwtOperation.Decode), Token = new(changed) });
            Assert.Equal("1", JsonNode.Parse(decoded)!["sub"]!.GetValue<string>());
            Assert.Equal("JWT_INVALID: A token is three parts joined by dots - header.claims.signature.", await Refusal("not-a-token"));
        }

        [Fact]
        public async Task A_weak_or_wrong_key_is_refused_and_the_key_is_shown_hidden()
        {
            async Task<string?> Signing(JwtAlgorithm algorithm, string key, string claims = "{}")
            {
                var (_, _, error) = await RunAsync(new Jwt { Claims = new(claims), Algorithm = new(algorithm), Key = new(key), OnFailure = new(FailurePolicy.Continue) });
                return error;
            }

            Assert.Equal("JWT_INVALID: HS256 needs a secret of at least 32 bytes - this one has 6.", await Signing(JwtAlgorithm.HS256, "secret"));
            Assert.Equal("JWT_INVALID: HS512 needs a secret of at least 64 bytes - this one has 32.", await Signing(JwtAlgorithm.HS512, Secret32));
            using var rsa = RSA.Create(2048);
            Assert.Equal("JWT_INVALID: RS256 signs with a PRIVATE key - this is a public one.", await Signing(JwtAlgorithm.RS256, rsa.ExportSubjectPublicKeyInfoPem()));
            using var small = RSA.Create(1024);
            Assert.Equal("JWT_INVALID: RS256 needs an RSA key of at least 2048 bits - this one has 1024.", await Signing(JwtAlgorithm.RS256, small.ExportPkcs8PrivateKeyPem()));
            using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            Assert.Equal("JWT_INVALID: ES256 needs a P-256 key - this one is 384 bits.", await Signing(JwtAlgorithm.ES256, p384.ExportPkcs8PrivateKeyPem()));
            Assert.Equal("JWT_INVALID: The claims are a JSON object - {\"sub\": \"42\"}.", await Signing(JwtAlgorithm.HS256, Secret32, "[1, 2]"));

            // The key and the secret may hold secrets: the catalog marks them so - the editor shows them hidden (one line, so not a
            // textarea, which shows what it holds) and so does a run's data (ListRunStepsQueryHandler).
            using var scope = _services.CreateScope();
            var catalog = ListStepsQueryHandler.Catalog(scope.ServiceProvider.GetRequiredService<IStepRegistry>());
            var jwt = catalog.Single(entry => entry.Type == "Stomme.Jwt");
            var totp = catalog.Single(entry => entry.Type == "Stomme.Totp");
            Assert.Equal((true, "singleline"), (jwt.Inputs.Single(input => input.Name == "Key").IsSensitive, jwt.Inputs.Single(input => input.Name == "Key").UIHint));
            Assert.Equal((true, "singleline"), (totp.Inputs.Single(input => input.Name == "Secret").IsSensitive, totp.Inputs.Single(input => input.Name == "Secret").UIHint));
            Assert.False(jwt.Inputs.Single(input => input.Name == "Claims").IsSensitive);
            Assert.Equal(Enum.GetNames<JwtAlgorithm>(), jwt.Inputs.Single(input => input.Name == "Algorithm").Options);
        }

        // RFC 6238 Appendix B: the same moments, the three hashes, eight digits.
        [Theory]
        [InlineData(TotpAlgorithm.Sha1, 59L, "94287082")]
        [InlineData(TotpAlgorithm.Sha256, 59L, "46119246")]
        [InlineData(TotpAlgorithm.Sha512, 59L, "90693936")]
        [InlineData(TotpAlgorithm.Sha1, 1111111109L, "07081804")]
        [InlineData(TotpAlgorithm.Sha256, 1234567890L, "91819424")]
        [InlineData(TotpAlgorithm.Sha512, 20000000000L, "47863826")]
        public void A_code_is_rfc_6238s_test_vector(TotpAlgorithm algorithm, long unixSeconds, string expected)
        {
            var seed = algorithm switch
            {
                TotpAlgorithm.Sha256 => "12345678901234567890123456789012",
                TotpAlgorithm.Sha512 => "1234567890123456789012345678901234567890123456789012345678901234",
                _ => "12345678901234567890",
            };
            var settings = new TotpSettings(Encoding.ASCII.GetBytes(seed), algorithm, 8, 30);
            Assert.Equal(expected, TotpCodes.Generate(settings, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Token);
        }

        [Fact]
        public async Task A_code_is_made_and_checked_a_period_either_side_and_the_link_is_read()
        {
            const string base32 = "GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ"; // "12345678901234567890", spaced as an app shows it
            var (_, generated, _) = await RunAsync(new Totp { Secret = new(base32) });
            var token = JsonNode.Parse(generated)!;
            Assert.Matches("^[0-9]{6}$", token["token"]!.GetValue<string>());
            Assert.InRange(token["secondsRemaining"]!.GetValue<int>(), 1, 30);

            var settings = TotpCodes.Read(base32, TotpAlgorithm.Sha1, 6, 30);
            var now = DateTimeOffset.UtcNow;
            var counter = now.ToUnixTimeSeconds() / 30;
            Assert.Equal(0, TotpCodes.Verify(settings, TotpCodes.Code(settings, counter), now, 1));
            Assert.Equal(-1, TotpCodes.Verify(settings, TotpCodes.Code(settings, counter - 1), now, 1));
            Assert.Null(TotpCodes.Verify(settings, TotpCodes.Code(settings, counter - 2), now, 1));
            Assert.Null(TotpCodes.Verify(settings, TotpCodes.Code(settings, counter + 1), now, 0));
            Assert.Null(TotpCodes.Verify(settings, "12345", now, 1));

            var (_, checkedOne, _) = await RunAsync(new Totp { Operation = new(TotpOperation.Verify), Secret = new(base32), Code = new(TotpCodes.Code(settings, counter)) });
            Assert.True(JsonNode.Parse(checkedOne)!["valid"]!.GetValue<bool>());
            var (_, wrong, _) = await RunAsync(new Totp { Operation = new(TotpOperation.Verify), Secret = new(base32), Code = new("12345") });
            Assert.False(JsonNode.Parse(wrong)!["valid"]!.GetValue<bool>());

            // The link a QR code holds: its own algorithm, digits and period win.
            var linked = TotpCodes.Read("otpauth://totp/Site:ops@example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Site&algorithm=SHA256&digits=8&period=60", TotpAlgorithm.Sha1, 6, 30);
            Assert.Equal((TotpAlgorithm.Sha256, 8, 60), (linked.Algorithm, linked.Digits, linked.Period));
        }

        [Fact]
        public async Task A_secret_that_cannot_make_a_code_is_refused_without_repeating_it()
        {
            async Task<string?> Refusal(string secret, int digits = 6, int period = 30)
            {
                var (_, _, error) = await RunAsync(new Totp { Secret = new(secret), Digits = new(digits), Period = new(period), OnFailure = new(FailurePolicy.Continue) });
                return error;
            }

            Assert.Equal("TOTP_INVALID: The secret holds a character base32 does not - it is letters A to Z and digits 2 to 7.", await Refusal("GEZDGNBVGY3TQOJ1"));
            Assert.Equal("TOTP_INVALID: The secret is base32 of at least 10 bytes (16 characters), as an authenticator app shows it.", await Refusal("GEZDGNBV"));
            Assert.Equal("TOTP_INVALID: Digits: 6 to 8 - not 4.", await Refusal("GEZDGNBVGY3TQOJQ", digits: 4));
            Assert.Equal("TOTP_INVALID: Period: 15 to 300 seconds - not 5.", await Refusal("GEZDGNBVGY3TQOJQ", period: 5));
        }
    }
}
