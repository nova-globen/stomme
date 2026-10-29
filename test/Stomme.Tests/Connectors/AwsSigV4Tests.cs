#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// AWS Signature Version 4: AWS's own documented example (IAM's ListUsers, 2015-08-30, AKIDEXAMPLE) signed to the
    /// signature AWS gives; the session token and the payload hash as headers when asked, both signed; a path's and a query's
    /// characters encoded and the headers canonical as AWS documents - the path twice outside S3, once in it; a port in the host.
    /// </summary>
    public sealed class AwsSigV4Tests
    {
        private const string Secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";

        private static readonly DateTimeOffset When = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

        [Fact]
        public void Awss_documented_example_signs_to_its_signature()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08") { Content = new ByteArrayContent(Array.Empty<byte>()) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded; charset=utf-8");
            AwsSigV4.Sign(request, Array.Empty<byte>(), "us-east-1", "iam", "AKIDEXAMPLE", Secret, null, When);

            Assert.Equal("20150830T123600Z", request.Headers.GetValues("x-amz-date").Single());
            Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7",
                request.Headers.GetValues("Authorization").Single());
        }

        // AWS's Signature Version 4 test suite: get-vanilla and get-vanilla-query-order-key-case, service 'service' in us-east-1.
        [Theory]
        [InlineData("https://example.amazonaws.com/", "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31")]
        [InlineData("https://example.amazonaws.com/?Param2=value2&Param1=value1", "b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500")]
        public void Awss_test_suite_signs_to_its_signatures(string url, string signature)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AwsSigV4.Sign(request, Array.Empty<byte>(), "us-east-1", "service", "AKIDEXAMPLE", Secret, null, When);
            Assert.Equal($"AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature={signature}", request.Headers.GetValues("Authorization").Single());
        }

        // Amazon S3: the S3 API reference's own examples (examplebucket, 2013-05-24, AKIAIOSFODNN7EXAMPLE) - GET Bucket
        // Lifecycle and GET Bucket (list objects) - signed with the payload hash header S3 requires, to the signatures S3 gives;
        // the same with the hash handed over as text, as an upload hashed beforehand signs.
        [Theory]
        [InlineData("https://examplebucket.s3.amazonaws.com/?lifecycle", "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543")]
        [InlineData("https://examplebucket.s3.amazonaws.com/?max-keys=2&prefix=J", "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7")]
        public void S3s_documented_examples_sign_to_their_signatures(string url, string signature)
        {
            var when = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
            const string secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
            var expected = $"AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={signature}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AwsSigV4.Sign(request, Array.Empty<byte>(), "us-east-1", "s3", "AKIAIOSFODNN7EXAMPLE", secret, null, when, signPayloadHeader: true);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", request.Headers.GetValues("x-amz-content-sha256").Single());
            Assert.Equal(expected, request.Headers.GetValues("Authorization").Single());

            var hashed = new HttpRequestMessage(HttpMethod.Get, url);
            AwsSigV4.Sign(hashed, AwsSigV4.Hex(SHA256.HashData(Array.Empty<byte>())), "us-east-1", "s3", "AKIAIOSFODNN7EXAMPLE", secret, null, when, signPayloadHeader: true);
            Assert.Equal(expected, hashed.Headers.GetValues("Authorization").Single());
        }

        [Fact]
        public void A_session_token_and_the_payload_hash_are_signed_and_a_path_and_query_encoded()
        {
            var payload = Encoding.UTF8.GetBytes("{\"Message\":\"Fika\"}");
            var hash = Hex(SHA256.HashData(payload));
            var request = new HttpRequestMessage(HttpMethod.Post, "https://example.amazonaws.com/a%20b/c?b=2&a=1&a=0&k%C3%A9=v%20w");
            request.Headers.TryAddWithoutValidation("X-Amz-Meta-Note", "  Fika   time ");
            AwsSigV4.Sign(request, payload, "eu-north-1", "sns", "AKIDEXAMPLE", Secret, "session-1", When, signPayloadHeader: true);

            Assert.Equal(("session-1", hash), (request.Headers.GetValues("x-amz-security-token").Single(), request.Headers.GetValues("x-amz-content-sha256").Single()));
            // The canonical request as AWS documents it: the path's segments encoded twice, the query sorted by name and then value,
            // every x-amz-* header lower-cased with its value trimmed and its spaces collapsed.
            var canonical = "POST\n/a%2520b/c\na=0&a=1&b=2&k%C3%A9=v%20w\n"
                + $"host:example.amazonaws.com\nx-amz-content-sha256:{hash}\nx-amz-date:20150830T123600Z\nx-amz-meta-note:Fika time\nx-amz-security-token:session-1\n\n"
                + $"host;x-amz-content-sha256;x-amz-date;x-amz-meta-note;x-amz-security-token\n{hash}";
            Assert.Equal(Expected(canonical, "eu-north-1", "sns", "host;x-amz-content-sha256;x-amz-date;x-amz-meta-note;x-amz-security-token"), request.Headers.GetValues("Authorization").Single());

            // S3 encodes the path once; a port not the scheme's own is part of the host.
            var s3 = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com:8443/a%20b/c");
            AwsSigV4.Sign(s3, Array.Empty<byte>(), "eu-north-1", "s3", "AKIDEXAMPLE", Secret, null, When);
            var empty = Hex(SHA256.HashData(Array.Empty<byte>()));
            Assert.Equal(Expected($"GET\n/a%20b/c\n\nhost:example.amazonaws.com:8443\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n{empty}", "eu-north-1", "s3", "host;x-amz-date"), s3.Headers.GetValues("Authorization").Single());

            Assert.Equal("caf%C3%A9%20%26%20fika~-_.", AwsSigV4.Encode("café & fika~-_."));
        }

        // The signature AWS documents for a canonical request: its hash in the string to sign, the key derived day, region, service.
        private static string Expected(string canonical, string region, string service, string signedHeaders)
        {
            var scope = $"20150830/{region}/{service}/aws4_request";
            var toSign = $"AWS4-HMAC-SHA256\n20150830T123600Z\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
            var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + Secret), "20150830"), region), service), "aws4_request");
            return $"AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/{scope}, SignedHeaders={signedHeaders}, Signature={Hex(Hmac(key, toSign))}";
        }

        private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
