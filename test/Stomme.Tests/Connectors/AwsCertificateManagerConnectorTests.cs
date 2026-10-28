#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.AwsCertificateManager;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - n8n's AWS Certificate Manager node over ACM's JSON protocol, each call signed with SigV4 for acm in the connection's
    /// region: every ARN, domain, status, key type, usage, algorithm and token held to ACM's shape before anything is signed;
    /// certificates listed by status and what they include, page by page up to the most; a certificate's details and its PEM; one
    /// requested for a domain and its other names by DNS or email with a key algorithm; one renewed and one deleted; AWS's refusals
    /// with their type.
    /// </summary>
    public sealed class AwsCertificateManagerConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIAKAFE", ["secret-key"] = "kafe-secret" };
        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-west-1" };
        private const string Api = "https://acm.eu-west-1.amazonaws.com/";
        private const string Kassa = "arn:aws:acm:eu-west-1:123456789012:certificate/0d1e2f3a-4b5c-6d7e-8f90-a1b2c3d4e5f6";

        private sealed record Sent(string Method, string Url, string? Target, string? Authorization, string Body);

        private sealed class Aws(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, Header("X-Amz-Target")?.Replace("CertificateManager.", "", StringComparison.Ordinal), Header("Authorization"), body);
                Requests.Add(sent);
                return answer(sent);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1") };

        private static Task<List<ConnectorRecord>> Run(Aws aws, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsCertificateManagerConnector(), Contexts.Extract(new StubEgress(aws), "", operation, parameters, Settings, Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_aws_certificate_manager_node()
        {
            IConnector connector = new AwsCertificateManagerConnector();
            Assert.Equal(("awsacm", "AWS Certificate Manager", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "certificates", "certificate", "certificate_pem", "request", "renew", "delete" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var aws = new Aws(_ => Json("{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(aws, operation, parameters));
                Assert.Empty(aws.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("AWS Certificate Manager has no operation 'import' - certificates, certificate,", await Refused("import", new()), StringComparison.Ordinal);
            Assert.Equal("Give the certificate's ARN (certificate).", await Refused("certificate", new()));
            Assert.Equal("'arn:aws:acm:eu-west-1:1234:certificate/x' is not a certificate's ARN - arn:aws:acm:eu-west-1:123456789012:certificate/... (certificate).", await Refused("delete", new() { ["certificate"] = "arn:aws:acm:eu-west-1:1234:certificate/x" }));
            Assert.Equal("'arn:aws:iam::123456789012:role/Kassa' is not a certificate's ARN - arn:aws:acm:eu-west-1:123456789012:certificate/... (certificate).", await Refused("renew", new() { ["certificate"] = "arn:aws:iam::123456789012:role/Kassa" }));
            Assert.StartsWith("'VALID' is not one of PENDING_VALIDATION, ISSUED,", await Refused("certificates", new() { ["statuses"] = "issued, VALID" }), StringComparison.Ordinal);
            Assert.StartsWith("'RSA_8192' is not one of RSA_1024, RSA_2048,", await Refused("certificates", new() { ["key_types"] = "RSA_8192" }), StringComparison.Ordinal);
            Assert.StartsWith("'SIGNING' is not one of DIGITAL_SIGNATURE,", await Refused("certificates", new() { ["key_usage"] = "SIGNING" }), StringComparison.Ordinal);
            Assert.StartsWith("'WEB' is not one of TLS_WEB_SERVER_AUTHENTICATION,", await Refused("certificates", new() { ["extended_key_usage"] = "WEB" }), StringComparison.Ordinal);
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("certificates", new() { ["max"] = "0" }));
            Assert.Equal("Give the domain - kafe.example (domain).", await Refused("request", new()));
            Assert.Equal("'kafe' is not a domain name - kafe.example, or *.kafe.example (domain).", await Refused("request", new() { ["domain"] = "kafe" }));
            Assert.Equal("'www.kafe.example/x' is not a domain name - kafe.example, or *.kafe.example (other_names).", await Refused("request", new() { ["domain"] = "kafe.example", ["other_names"] = "www.kafe.example/x" }));
            Assert.Equal("A certificate covers at most 100 names (other_names).", await Refused("request", new() { ["domain"] = "kafe.example", ["other_names"] = string.Join(",", Enumerable.Range(0, 100).Select(i => $"k{i}.kafe.example")) }));
            Assert.Equal("'http' is not dns or email (validation).", await Refused("request", new() { ["domain"] = "kafe.example", ["validation"] = "http" }));
            Assert.Equal("'RSA_4096' is not RSA_2048, EC_prime256v1 or EC_secp384r1 (key_algorithm).", await Refused("request", new() { ["domain"] = "kafe.example", ["key_algorithm"] = "RSA_4096" }));
            Assert.Equal("'kassa-1' is not a request token - letters and digits, at most 32 (token).", await Refused("request", new() { ["domain"] = "kafe.example", ["token"] = "kassa-1" }));
        }

        [Fact]
        public async Task Certificates_are_listed_by_status_and_what_they_include_page_by_page()
        {
            string Page(int from, int count, string? next) => "{\"CertificateSummaryList\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"CertificateArn\":\"" + Kassa[..^2] + i.ToString("00") + "\",\"DomainName\":\"k" + i + ".kafe.example\",\"Status\":\"ISSUED\",\"InUse\":true}")) + "]" + (next is null ? "" : ",\"NextToken\":\"" + next + "\"") + "}";
            var aws = new Aws(sent => sent.Body.Contains("\"NextToken\":\"p2\"", StringComparison.Ordinal) ? Json(Page(30, 5, null)) : Json(Page(0, 30, "p2")));
            var all = await Run(aws, "certificates", new() { ["statuses"] = "issued, Expired, issued", ["key_types"] = "rsa_2048,EC_PRIME256V1", ["extended_key_usage"] = "TLS_WEB_SERVER_AUTHENTICATION" });
            Assert.Equal(35, all.Count);
            Assert.Equal(new[]
            {
                "{\"CertificateStatuses\":[\"ISSUED\",\"EXPIRED\"],\"Includes\":{\"keyTypes\":[\"RSA_2048\",\"EC_prime256v1\"],\"extendedKeyUsage\":[\"TLS_WEB_SERVER_AUTHENTICATION\"]},\"MaxItems\":1000}",
                "{\"CertificateStatuses\":[\"ISSUED\",\"EXPIRED\"],\"Includes\":{\"keyTypes\":[\"RSA_2048\",\"EC_prime256v1\"],\"extendedKeyUsage\":[\"TLS_WEB_SERVER_AUTHENTICATION\"]},\"MaxItems\":970,\"NextToken\":\"p2\"}",
            }, aws.Requests.Select(sent => sent.Body)); // ACM's own names, each once; the second page what is still wanted
            Assert.All(aws.Requests, sent => Assert.Equal(("POST", Api, "ListCertificates"), (sent.Method, sent.Url, sent.Target)));
            Assert.Contains("/eu-west-1/acm/aws4_request", aws.Requests[0].Authorization, StringComparison.Ordinal);
            Assert.Equal(("k0.kafe.example", "ISSUED", (object?)true), ((string?)all[0].Fields["DomainName"], (string?)all[0].Fields["Status"], all[0].Fields["InUse"]));

            aws.Requests.Clear();
            Assert.Equal(7, (await Run(aws, "certificates", new() { ["max"] = "7", ["key_usage"] = "digital_signature" })).Count);
            Assert.Equal("{\"Includes\":{\"keyUsage\":[\"DIGITAL_SIGNATURE\"]},\"MaxItems\":7}", aws.Requests.Single().Body); // a page no larger than what is wanted
            aws.Requests.Clear();
            await Run(aws, "certificates", new() { ["max"] = "3" });
            Assert.Equal("{\"MaxItems\":3}", aws.Requests.Single().Body); // nothing asked: nothing filtered
        }

        [Fact]
        public async Task A_certificate_is_described_got_requested_renewed_and_deleted()
        {
            var aws = new Aws(sent => sent.Target switch
            {
                "DescribeCertificate" => Json("{\"Certificate\":{\"CertificateArn\":\"" + Kassa + "\",\"DomainName\":\"kafe.example\",\"SubjectAlternativeNames\":[\"kafe.example\",\"www.kafe.example\"],\"Status\":\"ISSUED\",\"NotAfter\":1830297600.0,\"Type\":\"AMAZON_ISSUED\"}}"),
                "GetCertificate" => Json("{\"Certificate\":\"-----BEGIN CERTIFICATE-----\\nMIIB\\n-----END CERTIFICATE-----\",\"CertificateChain\":\"-----BEGIN CERTIFICATE-----\\nMIIC\\n-----END CERTIFICATE-----\"}"),
                "RequestCertificate" => Json("{\"CertificateArn\":\"" + Kassa + "\"}"),
                _ => Json("{}"),
            });
            var details = Assert.Single(await Run(aws, "certificate", new() { ["certificate"] = Kassa })).Fields;
            Assert.Equal(("kafe.example", "[\"kafe.example\",\"www.kafe.example\"]", (object?)1830297600.0), ((string?)details["DomainName"], (string?)details["SubjectAlternativeNames"], details["NotAfter"]));
            var pem = Assert.Single(await Run(aws, "certificate_pem", new() { ["certificate"] = Kassa })).Fields;
            Assert.Equal((Kassa, "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----"), ((string?)pem["certificate"], (string?)pem["Certificate"]));
            Assert.StartsWith("-----BEGIN CERTIFICATE-----\nMIIC", (string)pem["CertificateChain"]!, StringComparison.Ordinal);

            aws.Requests.Clear();
            var requested = Assert.Single(await Run(aws, "request", new() { ["domain"] = "*.Kafe.Example", ["other_names"] = "kafe.example, www.kafe.example, KAFE.example", ["validation"] = "Email", ["key_algorithm"] = "ec_prime256v1", ["token"] = "kassa1" })).Fields;
            Assert.Equal(new Dictionary<string, object?> { ["CertificateArn"] = Kassa, ["domain"] = "*.kafe.example", ["requested"] = true }, requested);
            Assert.Equal("{\"DomainName\":\"*.kafe.example\",\"SubjectAlternativeNames\":[\"kafe.example\",\"www.kafe.example\"],\"ValidationMethod\":\"EMAIL\",\"KeyAlgorithm\":\"EC_prime256v1\",\"IdempotencyToken\":\"kassa1\"}", aws.Requests.Single().Body);
            await Run(aws, "request", new() { ["domain"] = "kafe.example" });
            Assert.Equal("{\"DomainName\":\"kafe.example\",\"ValidationMethod\":\"DNS\"}", aws.Requests.Last().Body); // DNS by default, ACM's algorithm

            aws.Requests.Clear();
            Assert.Equal(new Dictionary<string, object?> { ["certificate"] = Kassa, ["renewed"] = true }, Assert.Single(await Run(aws, "renew", new() { ["certificate"] = Kassa })).Fields);
            Assert.Equal(new Dictionary<string, object?> { ["certificate"] = Kassa, ["deleted"] = true }, Assert.Single(await Run(aws, "delete", new() { ["certificate"] = Kassa })).Fields);
            Assert.Equal(new[] { ("RenewCertificate", "{\"CertificateArn\":\"" + Kassa + "\"}"), ("DeleteCertificate", "{\"CertificateArn\":\"" + Kassa + "\"}") }, aws.Requests.Select(sent => (sent.Target!, sent.Body)));

            var refusing = new Aws(_ => Json("{\"__type\":\"ResourceInUseException\",\"message\":\"Certificate is in use by a load balancer.\"}", HttpStatusCode.BadRequest));
            Assert.Equal("AWS Certificate Manager answered HTTP 400 ResourceInUseException: Certificate is in use by a load balancer.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "delete", new() { ["certificate"] = Kassa }))).Message);
            var tested = await new AwsCertificateManagerConnector().TestConnectionAsync(Contexts.Test(new StubEgress(aws), "", Settings, Credentials));
            Assert.Equal((true, "AWS Certificate Manager in eu-west-1 answers (no certificates yet)."), (tested.Success, tested.Message));
            Assert.Equal("{\"MaxItems\":1}", aws.Requests.Last().Body);
        }
    }

    /// <summary>
    /// BL-229 - the AWS Certificate Manager connector against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback),
    /// which checks every SigV4 signature against the IAM key it issued, through the HOST's egress: loopback refused until
    /// allowlisted; a test; a certificate requested for a domain and another name, listed by its status, described with both
    /// names, its PEM read (moto issues a requested certificate at once, where AWS waits for its validation), deleted and gone. Fails, never skips, when moto is
    /// absent (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsCertificateManagerConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = MotoTestEndpoint.SecretKey };

        private static Dictionary<string, string> Settings => new() { ["region"] = MotoTestEndpoint.Region };

        private static HostConnectorEgress Egress => new(new[] { MotoTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsCertificateManagerConnector(), Contexts.Extract(Egress, MotoTestEndpoint.Endpoint, operation, parameters, Settings, Credentials, timeoutSeconds: 30));

        [Fact]
        public async Task A_certificate_is_requested_listed_described_and_deleted()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new AwsCertificateManagerConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), MotoTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
            var tested = await new AwsCertificateManagerConnector().TestConnectionAsync(Contexts.Test(Egress, MotoTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {tested.Success}: {tested.Message}");
            Assert.True(tested.Success, tested.Message);

            var domain = "kafe-" + Guid.NewGuid().ToString("N")[..8] + ".example";
            var arn = (string)Assert.Single(await Run("request", new() { ["domain"] = domain, ["other_names"] = "www." + domain })).Fields["CertificateArn"]!;
            try
            {
                Assert.StartsWith("arn:aws:acm:eu-west-1:", arn, StringComparison.Ordinal);
                Assert.Contains(await Run("certificates", new() { ["statuses"] = "pending_validation,issued" }), found => (string?)found.Fields["CertificateArn"] == arn);
                var details = Assert.Single(await Run("certificate", new() { ["certificate"] = arn })).Fields;
                Assert.Equal(domain, details["DomainName"]);
                Assert.Contains("www." + domain, (string)details["SubjectAlternativeNames"]!, StringComparison.Ordinal);
                var pem = Assert.Single(await Run("certificate_pem", new() { ["certificate"] = arn })).Fields; // moto issues a requested certificate at once
                Assert.StartsWith("-----BEGIN CERTIFICATE-----", (string)pem["Certificate"]!, StringComparison.Ordinal);
                Assert.Equal(arn, pem["certificate"]);
            }
            finally
            {
                Assert.True((bool)Assert.Single(await Run("delete", new() { ["certificate"] = arn })).Fields["deleted"]!);
            }

            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("certificate", new() { ["certificate"] = arn }));
            Assert.StartsWith("AWS Certificate Manager answered HTTP 400 ResourceNotFoundException", gone.Message, StringComparison.Ordinal);
        }
    }
}
