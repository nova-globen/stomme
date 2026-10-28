#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsCertificateManager
{
    /// <summary>
    /// AWS Certificate Manager (BL-229, n8n's AWS Certificate Manager node: an account's TLS certificates) over ACM's JSON protocol
    /// (<c>https://acm.{region}.amazonaws.com</c>, <c>X-Amz-Target: CertificateManager.*</c>) through the shared
    /// <see cref="AwsJsonSession"/>, every call signed with SigV4 by an IAM user's access key and secret, over the host's egress. A
    /// test lists the account's certificates, one.
    /// </summary>
    /// <remarks>
    /// <c>certificates</c> (by status, key type, key usage and extended key usage, page by page up to <c>max</c> - n8n's Get
    /// Many); <c>certificate</c> (its details - n8n's Get Metadata); <c>certificate_pem</c> (the certificate and its chain, public
    /// PEM - n8n's Get); <c>request</c> (one for a domain and its other names, validated by DNS or email, by a key algorithm -
    /// which n8n's node has not); <c>renew</c> (a private certificate's managed renewal) and <c>delete</c>. Every ARN, domain,
    /// status, key type and usage held to ACM's shape before anything is signed; AWS's error type and message on a failure. No
    /// private key ever passes through it: an imported certificate's key is a secret no step's parameter holds.
    /// </remarks>
    public sealed class AwsCertificateManagerConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private const int PageSize = 1000; // ACM's most a page

        private static readonly string[] Known = { "certificates", "certificate", "certificate_pem", "request", "renew", "delete" };

        private static readonly string[] Statuses = { "PENDING_VALIDATION", "ISSUED", "INACTIVE", "EXPIRED", "VALIDATION_TIMED_OUT", "REVOKED", "FAILED" };

        private static readonly string[] KeyTypes = { "RSA_1024", "RSA_2048", "RSA_3072", "RSA_4096", "EC_prime256v1", "EC_secp384r1", "EC_secp521r1" };

        private static readonly string[] KeyUsages = { "DIGITAL_SIGNATURE", "NON_REPUDIATION", "KEY_ENCIPHERMENT", "DATA_ENCIPHERMENT", "KEY_AGREEMENT", "CERTIFICATE_SIGNING", "CRL_SIGNING", "ENCIPHER_ONLY", "DECIPHER_ONLY", "ANY", "CUSTOM" };

        private static readonly string[] ExtendedKeyUsages =
        {
            "TLS_WEB_SERVER_AUTHENTICATION", "TLS_WEB_CLIENT_AUTHENTICATION", "CODE_SIGNING", "EMAIL_PROTECTION", "TIME_STAMPING", "OCSP_SIGNING",
            "IPSEC_END_SYSTEM", "IPSEC_TUNNEL", "IPSEC_USER", "ANY", "NONE", "CUSTOM",
        };

        private static readonly string[] Algorithms = { "RSA_2048", "EC_prime256v1", "EC_secp384r1" };

        private static readonly Regex Arn = new("^arn:aws[a-z-]*:acm:[a-z0-9-]+:[0-9]{12}:certificate/[0-9a-f-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Domain = new("^(\\*\\.)?([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)+[a-zA-Z]{2,63}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awsacm";

        public string DisplayName => "AWS Certificate Manager";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://acm.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability, ConnectorCategory.NetworkInfrastructure };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/certificate-manager/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/acm/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region its certificates are in - eu-west-1. The address is ACM's in it when left empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField ArnField = Field("certificate", "Certificate", "Its ARN - arn:aws:acm:eu-west-1:123456789012:certificate/....", required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("certificates", "Get certificates", "The account's certificates in the region - n8n's Get Many.")
            {
                Parameters = new(new[]
                {
                    Field("statuses", "Statuses", "ISSUED, PENDING_VALIDATION, EXPIRED... - several by commas."),
                    Field("key_types", "Key types", "RSA_2048, EC_prime256v1... - several by commas. Empty: ACM's, RSA_2048."),
                    Field("key_usage", "Key usage", "DIGITAL_SIGNATURE, KEY_ENCIPHERMENT... - several by commas."),
                    Field("extended_key_usage", "Extended key usage", "TLS_WEB_SERVER_AUTHENTICATION... - several by commas."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("certificate", "Get a certificate's details", "Its domains, status, validity and use - n8n's Get Metadata.") { Parameters = new(new[] { ArnField }) },
            new ConnectorOperation("certificate_pem", "Get a certificate", "The certificate and its chain, PEM - n8n's Get.") { Parameters = new(new[] { ArnField }) },
            new ConnectorOperation("request", "Request a certificate", "For a domain and its other names.")
            {
                Parameters = new(new[]
                {
                    Field("domain", "Domain", "kafe.example, or *.kafe.example.", required: true),
                    Field("other_names", "Other names", "More names it covers - www.kafe.example, by commas."),
                    Field("validation", "Validation", "dns (the default) or email."),
                    Field("key_algorithm", "Key algorithm", "RSA_2048 (ACM's default), EC_prime256v1 or EC_secp384r1."),
                    Field("token", "Request token", "Letters and digits that make a repeated request the same one."),
                }),
            },
            new ConnectorOperation("renew", "Renew a certificate", "A private certificate's managed renewal - n8n's Renew.") { Parameters = new(new[] { ArnField }) },
            new ConnectorOperation("delete", "Delete a certificate", "n8n's Delete.") { Parameters = new(new[] { ArnField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.CallAsync("ListCertificates", new JsonObject { ["MaxItems"] = 1 }, cancellationToken);
                return ConnectionTestResult.Ok($"AWS Certificate Manager in {session.Region} answers ({((answer?["CertificateSummaryList"] as JsonArray)?.Count ?? 0) switch { 0 => "no certificates yet", _ => "its certificates listed" }}).");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "certificates").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"AWS Certificate Manager has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var body = new JsonObject();
            switch (operation)
            {
                case "certificates":
                    {
                        if (List(p, "statuses", Statuses) is { } statuses)
                        {
                            body["CertificateStatuses"] = statuses;
                        }

                        var includes = new JsonObject();
                        foreach (var (name, known, key) in new[] { ("key_types", KeyTypes, "keyTypes"), ("key_usage", KeyUsages, "keyUsage"), ("extended_key_usage", ExtendedKeyUsages, "extendedKeyUsage") })
                        {
                            if (List(p, name, known) is { } chosen)
                            {
                                includes[key] = chosen;
                            }
                        }

                        if (includes.Count > 0)
                        {
                            body["Includes"] = includes;
                        }

                        break;
                    }

                case "request":
                    {
                        var domain = Values.Get(p, "domain") ?? throw new InvalidOperationException("Give the domain - kafe.example (domain).");
                        body["DomainName"] = Host(domain, "domain");
                        if (Values.Get(p, "other_names") is { } others)
                        {
                            var names = others.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(name => Host(name, "other_names")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            // ACM's additional names: at most 99 beside the domain, a certificate's 100.
                            body["SubjectAlternativeNames"] = names.Count <= 99 ? new JsonArray(names.Select(name => (JsonNode?)name).ToArray()) : throw new InvalidOperationException("A certificate covers at most 100 names (other_names).");
                        }

                        body["ValidationMethod"] = (Values.Get(p, "validation") ?? "dns").ToUpperInvariant() is ("DNS" or "EMAIL") and var method ? method : throw new InvalidOperationException($"'{Values.Get(p, "validation")}' is not dns or email (validation).");
                        if (Values.Get(p, "key_algorithm") is { } algorithm)
                        {
                            body["KeyAlgorithm"] = Algorithms.FirstOrDefault(known => string.Equals(known, algorithm, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{algorithm}' is not {string.Join(", ", Algorithms[..^1])} or {Algorithms[^1]} (key_algorithm).");
                        }

                        if (Values.Get(p, "token") is { } token)
                        {
                            body["IdempotencyToken"] = token.Length <= 32 && token.All(char.IsAsciiLetterOrDigit) ? token : throw new InvalidOperationException($"'{token}' is not a request token - letters and digits, at most 32 (token).");
                        }

                        break;
                    }

                default:
                    {
                        var arn = Values.Get(p, "certificate") ?? throw new InvalidOperationException("Give the certificate's ARN (certificate).");
                        body["CertificateArn"] = Arn.IsMatch(arn) ? arn : throw new InvalidOperationException($"'{arn}' is not a certificate's ARN - arn:aws:acm:eu-west-1:123456789012:certificate/... (certificate).");
                        break;
                    }
            }

            var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "certificates":
                    {
                        // Page by page by ACM's NextToken, a page no larger than what is still wanted.
                        var read = 0;
                        string? next = null;
                        do
                        {
                            body["MaxItems"] = Math.Min(PageSize, max - read);
                            if (next is null)
                            {
                                body.Remove("NextToken");
                            }
                            else
                            {
                                body["NextToken"] = next;
                            }

                            var page = await session.CallAsync("ListCertificates", body, cancellationToken) as JsonObject;
                            foreach (var summary in (page?["CertificateSummaryList"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(summary);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = Text(page?["NextToken"]) is { Length: > 0 } token ? token : null;
                        }
                        while (next is not null);

                        break;
                    }

                case "certificate":
                    yield return Record((await session.CallAsync("DescribeCertificate", body, cancellationToken))?["Certificate"] as JsonObject ?? new JsonObject());
                    break;
                case "certificate_pem":
                    {
                        var found = await session.CallAsync("GetCertificate", body, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return Fields(("certificate", Text(body["CertificateArn"])), ("Certificate", Text(found["Certificate"])), ("CertificateChain", Text(found["CertificateChain"])));
                        break;
                    }

                case "request":
                    yield return Fields(("CertificateArn", Text((await session.CallAsync("RequestCertificate", body, cancellationToken))?["CertificateArn"])), ("domain", Text(body["DomainName"])), ("requested", true));
                    break;
                case "renew":
                    await session.CallAsync("RenewCertificate", body, cancellationToken);
                    yield return Fields(("certificate", Text(body["CertificateArn"])), ("renewed", true));
                    break;
                default:
                    await session.CallAsync("DeleteCertificate", body, cancellationToken);
                    yield return Fields(("certificate", Text(body["CertificateArn"])), ("deleted", true));
                    break;
            }
        }

        private AwsJsonSession Session(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
            AwsJsonSession.Of("AWS Certificate Manager", "acm", "CertificateManager", endpoint, settings, credentials, egress, timeout, time);

        // A list of ACM's own names, by commas, each held to them; null when none is given.
        private static JsonArray? List(IReadOnlyDictionary<string, string> p, string name, string[] known)
        {
            if (Values.Get(p, name) is not { } given)
            {
                return null;
            }

            var chosen = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => known.FirstOrDefault(each => string.Equals(each, value, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{value}' is not one of {string.Join(", ", known)} ({name})."))
                .Distinct()
                .Select(value => (JsonNode?)value)
                .ToArray();
            return chosen.Length > 0 ? new JsonArray(chosen) : null;
        }

        private static string Host(string given, string name) =>
            given.Length <= 253 && Domain.IsMatch(given) ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a domain name - kafe.example, or *.kafe.example ({name}).");

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
