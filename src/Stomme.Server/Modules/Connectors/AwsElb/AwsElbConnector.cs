#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AwsElb
{
    /// <summary>
    /// AWS Elastic Load Balancing (BL-229, n8n's AWS ELB node: application and network load balancers, and their listeners'
    /// certificates) over ELB's Query protocol (<c>https://elasticloadbalancing.{region}.amazonaws.com</c>, version 2015-12-01)
    /// through the shared <see cref="AwsQuerySession"/>, every call signed with SigV4 by an IAM user's access key and secret, over
    /// the host's egress. A test lists a load balancer.
    /// </summary>
    /// <remarks>
    /// <c>load_balancers</c> (by name, page by page by ELB's Marker up to <c>max</c> - n8n's Get Many), <c>load_balancer</c> (by
    /// its ARN - n8n's Get), <c>load_balancer_create</c> (an application or network one, internet-facing or internal, IPv4 or
    /// dual-stack, in its subnets, with security groups and tags - n8n's Create), <c>load_balancer_delete</c>; <c>listeners</c>
    /// (a load balancer's, which n8n's node lists only to pick from); <c>certificates</c>, <c>certificate_add</c> and
    /// <c>certificate_remove</c> (a listener's extra certificates - n8n's Listener Certificate). Every ARN, name, id and choice held
    /// to ELB's shape before anything is signed; AWS's error code and message on a failure.
    /// </remarks>
    public sealed class AwsElbConnector(TimeProvider? clock = null) : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private const string Version = "2015-12-01";

        private const int PageSize = 400; // ELB's most a page

        private static readonly string[] Known =
        {
            "load_balancers", "load_balancer", "load_balancer_create", "load_balancer_delete", "listeners", "certificates", "certificate_add", "certificate_remove",
        };

        private static readonly Regex Region = new("^[a-z]{2}(-gov|-iso[a-z]?)?-[a-z]+-[0-9]$", RegexOptions.CultureInvariant);

        private static readonly Regex Name = new("^(?!internal-)[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,30}[a-zA-Z0-9])?$", RegexOptions.CultureInvariant);

        private static readonly Regex LoadBalancerArn = new("^arn:aws[a-z-]*:elasticloadbalancing:[a-z0-9-]+:[0-9]{12}:loadbalancer/(app|net|gwy)/[a-zA-Z0-9-]{1,32}/[0-9a-f]{1,32}$", RegexOptions.CultureInvariant);

        private static readonly Regex ListenerArn = new("^arn:aws[a-z-]*:elasticloadbalancing:[a-z0-9-]+:[0-9]{12}:listener/(app|net|gwy)/[a-zA-Z0-9-]{1,32}/[0-9a-f]{1,32}/[0-9a-f]{1,32}$", RegexOptions.CultureInvariant);

        private static readonly Regex CertificateArn = new("^arn:aws[a-z-]*:(acm|iam):[a-z0-9-]*:[0-9]{12}:(certificate|server-certificate)/[\\w+=,.@/-]{1,512}$", RegexOptions.CultureInvariant);

        private static readonly Regex SubnetId = new("^subnet-[0-9a-f]{8,17}$", RegexOptions.CultureInvariant);

        private static readonly Regex SecurityGroupId = new("^sg-[0-9a-f]{8,17}$", RegexOptions.CultureInvariant);

        private readonly TimeProvider time = clock ?? TimeProvider.System;

        public string ConnectorType => "awselb";

        public string DisplayName => "AWS Elastic Load Balancing";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public string EndpointExample => "https://elasticloadbalancing.eu-west-1.amazonaws.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.NetworkInfrastructure };

        public Uri WebsiteUrl { get; } = new("https://aws.amazon.com/elasticloadbalancing/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/Welcome.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Text, Required: true, Description: "The AWS region its load balancers are in - eu-west-1. The address is ELB's in it when left empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField LoadBalancerField = Field("load_balancer", "Load balancer", "Its ARN - arn:aws:elasticloadbalancing:eu-west-1:123456789012:loadbalancer/app/kassa/....", required: true);

        private static readonly ConnectorConfigField ListenerField = Field("listener", "Listener", "Its ARN - from Get listeners.", required: true);

        private static readonly ConnectorConfigField CertificateField = Field("certificate", "Certificate", "Its ARN - an ACM certificate's, arn:aws:acm:eu-west-1:123456789012:certificate/....", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("load_balancers", "Get load balancers", "n8n's Get Many.") { Parameters = new(new[] { Field("names", "Names", "Only these - kassa, kafe, by commas."), MaxField }) },
            new ConnectorOperation("load_balancer", "Get a load balancer", "n8n's Get.") { Parameters = new(new[] { LoadBalancerField }) },
            new ConnectorOperation("load_balancer_create", "Create a load balancer", "n8n's Create.")
            {
                Parameters = new(new[]
                {
                    Field("name", "Name", "Unique in the region - letters, digits and -, at most 32.", required: true),
                    Field("subnets", "Subnets", "Their ids, by commas - subnet-0a1b2c3d, subnet-4e5f6a7b; two zones for an application one.", required: true),
                    Field("type", "Type", "application (the default) or network."),
                    Field("scheme", "Scheme", "internet-facing (the default) or internal."),
                    Field("ip_address_type", "IP addresses", "ipv4 (the default) or dualstack."),
                    Field("security_groups", "Security groups", "Their ids, by commas - sg-0a1b2c3d."),
                    Field("tags", "Tags", "A JSON object - {\"team\": \"kassa\"}."),
                }),
            },
            new ConnectorOperation("load_balancer_delete", "Delete a load balancer", "n8n's Delete.") { Parameters = new(new[] { LoadBalancerField }) },
            new ConnectorOperation("listeners", "Get listeners", "A load balancer's listeners.") { Parameters = new(new[] { LoadBalancerField, MaxField }) },
            new ConnectorOperation("certificates", "Get a listener's certificates", "n8n's Listener Certificate Get Many.") { Parameters = new(new[] { ListenerField, MaxField }) },
            new ConnectorOperation("certificate_add", "Add a certificate to a listener", "n8n's Listener Certificate Add.") { Parameters = new(new[] { ListenerField, CertificateField }) },
            new ConnectorOperation("certificate_remove", "Remove a certificate from a listener", "n8n's Listener Certificate Remove.") { Parameters = new(new[] { ListenerField, CertificateField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
                var answer = await session.CallAsync("DescribeLoadBalancers", new[] { ("PageSize", "1") }, cancellationToken);
                return ConnectionTestResult.Ok($"AWS Elastic Load Balancing in {session.Region} answers ({(AwsQuerySession.Members(answer, "LoadBalancers").Any() ? "its load balancers listed" : "no load balancers yet")}).");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "load_balancers").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"AWS Elastic Load Balancing has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var parameters = new List<(string Name, string Value)>();
            switch (operation)
            {
                case "load_balancers":
                    {
                        var names = List(p, "names", Name, "a load balancer's name");
                        for (var i = 0; i < names.Count; i++)
                        {
                            parameters.Add(($"Names.member.{i + 1}", names[i]));
                        }

                        break;
                    }

                case "load_balancer" or "load_balancer_delete" or "listeners":
                    parameters.Add((operation == "load_balancer" ? "LoadBalancerArns.member.1" : "LoadBalancerArn", Arn(p, "load_balancer", LoadBalancerArn, "a load balancer's ARN")));
                    break;
                case "certificates" or "certificate_add" or "certificate_remove":
                    parameters.Add(("ListenerArn", Arn(p, "listener", ListenerArn, "a listener's ARN")));
                    if (operation != "certificates")
                    {
                        parameters.Add(("Certificates.member.1.CertificateArn", Arn(p, "certificate", CertificateArn, "a certificate's ARN")));
                    }

                    break;
                default: // load_balancer_create
                    {
                        var name = Values.Get(p, "name") ?? throw new InvalidOperationException("Give the load balancer's name (name).");
                        parameters.Add(("Name", Name.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a load balancer's name - letters, digits and -, at most 32, not starting with internal- (name).")));
                        parameters.Add(("Type", Choice(p, "type", "application", "application", "network")));
                        parameters.Add(("Scheme", Choice(p, "scheme", "internet-facing", "internet-facing", "internal")));
                        parameters.Add(("IpAddressType", Choice(p, "ip_address_type", "ipv4", "ipv4", "dualstack")));
                        var subnets = List(p, "subnets", SubnetId, "a subnet's id");
                        if (subnets.Count == 0)
                        {
                            throw new InvalidOperationException("Give its subnets' ids - subnet-0a1b2c3d, subnet-4e5f6a7b (subnets).");
                        }

                        for (var i = 0; i < subnets.Count; i++)
                        {
                            parameters.Add(($"Subnets.member.{i + 1}", subnets[i]));
                        }

                        var groups = List(p, "security_groups", SecurityGroupId, "a security group's id");
                        for (var i = 0; i < groups.Count; i++)
                        {
                            parameters.Add(($"SecurityGroups.member.{i + 1}", groups[i]));
                        }

                        var index = 0;
                        foreach (var (key, value) in Tags(p))
                        {
                            index++;
                            parameters.Add(($"Tags.member.{index}.Key", key));
                            parameters.Add(($"Tags.member.{index}.Value", value));
                        }

                        break;
                    }
            }

            var session = Session(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "load_balancers":
                    await foreach (var found in PagesAsync(session, "DescribeLoadBalancers", parameters, "LoadBalancers", max, cancellationToken))
                    {
                        yield return Record(found);
                    }

                    break;
                case "load_balancer":
                    {
                        var found = AwsQuerySession.Members(await session.CallAsync("DescribeLoadBalancers", parameters, cancellationToken), "LoadBalancers").FirstOrDefault()
                            ?? throw new InvalidOperationException($"AWS Elastic Load Balancing knows no load balancer {parameters[0].Value}.");
                        yield return Record(found);
                        break;
                    }

                case "load_balancer_create":
                    foreach (var made in AwsQuerySession.Members(await session.CallAsync("CreateLoadBalancer", parameters, cancellationToken), "LoadBalancers"))
                    {
                        yield return Record(made);
                    }

                    break;
                case "load_balancer_delete":
                    await session.CallAsync("DeleteLoadBalancer", parameters, cancellationToken);
                    yield return Fields(("load_balancer", parameters[0].Value), ("deleted", true));
                    break;
                case "listeners":
                    await foreach (var found in PagesAsync(session, "DescribeListeners", parameters, "Listeners", max, cancellationToken))
                    {
                        yield return Record(found);
                    }

                    break;
                case "certificates":
                    await foreach (var found in PagesAsync(session, "DescribeListenerCertificates", parameters, "Certificates", max, cancellationToken))
                    {
                        yield return Record(found);
                    }

                    break;
                case "certificate_add":
                    await session.CallAsync("AddListenerCertificates", parameters, cancellationToken);
                    yield return Fields(("listener", parameters[0].Value), ("certificate", parameters[1].Value), ("added", true));
                    break;
                default:
                    await session.CallAsync("RemoveListenerCertificates", parameters, cancellationToken);
                    yield return Fields(("listener", parameters[0].Value), ("certificate", parameters[1].Value), ("removed", true));
                    break;
            }
        }

        private AwsQuerySession Session(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
        {
            var region = Values.Get(settings, "region") ?? throw new InvalidOperationException("Give the AWS region - eu-west-1.");
            if (!Region.IsMatch(region))
            {
                throw new InvalidOperationException($"'{region}' is not an AWS region - eu-west-1.");
            }

            return AwsQuerySession.Of("AWS Elastic Load Balancing", "elasticloadbalancing", Version, $"https://elasticloadbalancing.{region}.amazonaws.com", region, endpoint, credentials, egress, timeout, time);
        }

        // A list page by page by ELB's NextMarker, a page no larger than what is still wanted.
        private static async IAsyncEnumerable<XElement> PagesAsync(AwsQuerySession session, string action, List<(string Name, string Value)> parameters, string list, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var read = 0;
            string? marker = null;
            do
            {
                var page = new List<(string, string)>(parameters) { ("PageSize", Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)) };

                if (marker is not null)
                {
                    page.Add(("Marker", marker));
                }

                var answer = await session.CallAsync(action, page, cancellationToken);
                foreach (var member in AwsQuerySession.Members(answer, list))
                {
                    yield return member;
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                marker = AwsQuerySession.Child(answer, "NextMarker") is { Length: > 0 } next ? next : null;
            }
            while (marker is not null);
        }

        private static List<string> List(IReadOnlyDictionary<string, string> p, string name, Regex shape, string what) =>
            Values.Get(p, name) is { } given
                ? given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
                    .Select(each => shape.IsMatch(each) ? each : throw new InvalidOperationException($"'{each}' is not {what} ({name}).")).ToList()
                : new List<string>();

        private static string Arn(IReadOnlyDictionary<string, string> p, string name, Regex shape, string what) =>
            Values.Get(p, name) is { } given
                ? shape.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what} ({name}).")
                : throw new InvalidOperationException($"Give {what} ({name}).");

        private static string Choice(IReadOnlyDictionary<string, string> p, string name, string fallback, params string[] known) =>
            (Values.Get(p, name) ?? fallback).ToLowerInvariant() is var given && known.Contains(given) ? given : throw new InvalidOperationException($"'{Values.Get(p, name)}' is not {string.Join(" or ", known)} ({name}).");

        private static IEnumerable<(string Key, string Value)> Tags(IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "tags") is not { } given)
            {
                yield break;
            }

            JsonObject? tags;
            try
            {
                tags = JsonNode.Parse(given) as JsonObject;
            }
            catch (JsonException)
            {
                tags = null;
            }

            if (tags is null || tags.Count is 0 or > 50)
            {
                throw new InvalidOperationException("The tags are a JSON object of up to 50 keys and their values - {\"team\": \"kassa\"} (tags).");
            }

            foreach (var (key, value) in tags)
            {
                var text = value is JsonValue plain && plain.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? string.Empty;
                yield return key.Length is > 0 and <= 128 && text.Length <= 256 ? (key, text) : throw new InvalidOperationException($"A tag's key is 1 to 128 characters and its value at most 256 ('{key}', tags).");
            }
        }

        // An element as a record: its leaves as fields, a list or a nested element as JSON.
        private static ConnectorRecord Record(XElement element) => new(Json(element).ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => (object?)text,
            null => null,
            var other => other.ToJsonString(),
        }, StringComparer.Ordinal));

        private static JsonObject Json(XElement element)
        {
            var json = new JsonObject();
            foreach (var child in element.Elements())
            {
                json[child.Name.LocalName] = child.HasElements
                    ? child.Elements().All(member => member.Name.LocalName == "member")
                        ? new JsonArray(child.Elements().Select(member => member.HasElements ? (JsonNode)Json(member) : JsonValue.Create(member.Value)).ToArray())
                        : Json(child)
                    : JsonValue.Create(child.Value);
            }

            return json;
        }

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}
