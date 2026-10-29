#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Connectors.AwsElb;
using Stomme.Modules.Connectors.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// n8n's AWS ELB node over Elastic Load Balancing's Query protocol (version 2015-12-01), each call a form signed with
    /// SigV4 for elasticloadbalancing in the connection's region: every ARN, name, id, choice and tag held to ELB's shape before
    /// anything is signed; load balancers by name, page by page by ELB's NextMarker up to the most; one by its ARN, created with its
    /// subnets, security groups and tags, deleted; a load balancer's listeners; a listener's certificates listed, added and removed;
    /// AWS's error code and message; a test.
    /// </summary>
    public sealed class AwsElbConnectorTests
    {
        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIAKAFE", ["secret-key"] = "kafe-secret" };
        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-west-1" };
        private const string Api = "https://elasticloadbalancing.eu-west-1.amazonaws.com/";
        private const string Ns = "xmlns=\"http://elasticloadbalancing.amazonaws.com/doc/2015-12-01/\"";
        private const string Kassa = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:loadbalancer/app/kassa/50dc6c495c0c9188";
        private const string Listener = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:listener/app/kassa/50dc6c495c0c9188/f2f7dc8efc522ab2";
        private const string Certificate = "arn:aws:acm:eu-west-1:123456789012:certificate/0d1e2f3a-4b5c-6d7e-8f90-a1b2c3d4e5f6";

        private sealed record Sent(string Url, string? Authorization, Dictionary<string, string> Form);

        private sealed class Elb(Func<Sent, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
        {
            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
                var sent = new Sent(request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var signed) ? signed.Single() : null, form);
                Requests.Add(sent);
                var (status, xml) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
            }
        }

        private static string Result(string action, string inner) => $"<{action}Response {Ns}><{action}Result>{inner}</{action}Result><ResponseMetadata><RequestId>r-1</RequestId></ResponseMetadata></{action}Response>";

        private static string LoadBalancer(string name) => $"<LoadBalancerArn>arn:aws:elasticloadbalancing:eu-west-1:123456789012:loadbalancer/app/{name}/50dc6c495c0c9188</LoadBalancerArn><DNSName>{name}-1.eu-west-1.elb.amazonaws.com</DNSName><LoadBalancerName>{name}</LoadBalancerName><Scheme>internet-facing</Scheme><Type>application</Type><State><Code>active</Code></State><AvailabilityZones><member><ZoneName>eu-west-1a</ZoneName><SubnetId>subnet-0a1b2c3d</SubnetId></member></AvailabilityZones>";

        private static Task<List<ConnectorRecord>> Run(Elb elb, string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsElbConnector(), Contexts.Extract(new StubEgress(elb), "", operation, parameters, Settings, Credentials));

        private static string Form(Sent sent) => string.Join(" ", sent.Form.Where(pair => pair.Key != "Version").Select(pair => pair.Key + "=" + pair.Value));

        [Fact]
        public void It_declares_itself_as_n8ns_aws_elb_node()
        {
            IConnector connector = new AwsElbConnector();
            Assert.Equal(("awselb", "AWS Elastic Load Balancing", AuthenticationType.ApiKeyPair), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "load_balancers", "load_balancer", "load_balancer_create", "load_balancer_delete", "listeners", "certificates", "certificate_add", "certificate_remove" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_its_shape_before_anything_is_signed()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters)
            {
                var elb = new Elb(_ => (HttpStatusCode.OK, "<x/>"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(elb, operation, parameters));
                Assert.Empty(elb.Requests);
                return refusal.Message;
            }

            Assert.StartsWith("AWS Elastic Load Balancing has no operation 'target_groups' - load_balancers, load_balancer,", await Refused("target_groups", new()), StringComparison.Ordinal);
            Assert.Equal("Give a load balancer's ARN (load_balancer).", await Refused("load_balancer", new()));
            Assert.Equal("'arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/kassa/50dc6c495c0c9188' is not a load balancer's ARN (load_balancer).", await Refused("load_balancer_delete", new() { ["load_balancer"] = "arn:aws:elasticloadbalancing:eu-west-1:123456789012:targetgroup/kassa/50dc6c495c0c9188" }));
            Assert.Equal("'" + Kassa + "' is not a listener's ARN (listener).", await Refused("certificates", new() { ["listener"] = Kassa }));
            Assert.Equal("'arn:aws:acm:eu-west-1:1234:certificate/x' is not a certificate's ARN (certificate).", await Refused("certificate_add", new() { ["listener"] = Listener, ["certificate"] = "arn:aws:acm:eu-west-1:1234:certificate/x" }));
            Assert.Equal("Give a certificate's ARN (certificate).", await Refused("certificate_remove", new() { ["listener"] = Listener }));
            Assert.Equal("'kassa_1' is not a load balancer's name (names).", await Refused("load_balancers", new() { ["names"] = "kassa, kassa_1" }));
            Assert.Equal("Give the load balancer's name (name).", await Refused("load_balancer_create", new() { ["subnets"] = "subnet-0a1b2c3d" }));
            Assert.Equal("'internal-kassa' is not a load balancer's name - letters, digits and -, at most 32, not starting with internal- (name).", await Refused("load_balancer_create", new() { ["name"] = "internal-kassa", ["subnets"] = "subnet-0a1b2c3d" }));
            Assert.Equal("'" + new string('k', 33) + "' is not a load balancer's name - letters, digits and -, at most 32, not starting with internal- (name).", await Refused("load_balancer_create", new() { ["name"] = new string('k', 33), ["subnets"] = "subnet-0a1b2c3d" }));
            Assert.Equal("Give its subnets' ids - subnet-0a1b2c3d, subnet-4e5f6a7b (subnets).", await Refused("load_balancer_create", new() { ["name"] = "kassa" }));
            Assert.Equal("'sub-1' is not a subnet's id (subnets).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "sub-1" }));
            Assert.Equal("'gateway' is not application or network (type).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["type"] = "gateway" }));
            Assert.Equal("'public' is not internet-facing or internal (scheme).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["scheme"] = "public" }));
            Assert.Equal("'ipv6' is not ipv4 or dualstack (ip_address_type).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["ip_address_type"] = "ipv6" }));
            Assert.Equal("'kassa' is not a security group's id (security_groups).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["security_groups"] = "kassa" }));
            Assert.Equal("The tags are a JSON object of up to 50 keys and their values - {\"team\": \"kassa\"} (tags).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["tags"] = "team" }));
            var many = new JsonObject();
            for (var i = 0; i < 51; i++)
            {
                many["team" + i] = "kassa";
            }

            Assert.Equal("The tags are a JSON object of up to 50 keys and their values - {\"team\": \"kassa\"} (tags).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["tags"] = many.ToJsonString() }));
            Assert.Equal("A tag's key is 1 to 128 characters and its value at most 256 ('team', tags).", await Refused("load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d", ["tags"] = "{\"team\": \"" + new string('k', 257) + "\"}" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("listeners", new() { ["load_balancer"] = Kassa, ["max"] = "0" }));

            var anywhere = new Elb(_ => (HttpStatusCode.OK, "<x/>"));
            var region = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AwsElbConnector(), Contexts.Extract(new StubEgress(anywhere), "", "load_balancers", new Dictionary<string, string>(), new Dictionary<string, string> { ["region"] = "Europe" }, Credentials)));
            Assert.Equal(("'Europe' is not an AWS region - eu-west-1.", 0), (region.Message, anywhere.Requests.Count));
        }

        [Fact]
        public async Task Load_balancers_are_listed_by_name_page_by_page_read_created_and_deleted()
        {
            string Page(int from, int count, string? next) => Result("DescribeLoadBalancers", "<LoadBalancers>" + string.Concat(Enumerable.Range(from, count).Select(i => "<member>" + LoadBalancer("kassa-" + i) + "</member>")) + "</LoadBalancers>" + (next is null ? "" : $"<NextMarker>{next}</NextMarker>"));
            var elb = new Elb(sent => sent.Form["Action"] switch
            {
                "DescribeLoadBalancers" when sent.Form.ContainsKey("LoadBalancerArns.member.1") => (HttpStatusCode.OK, Page(0, 1, null)),
                "DescribeLoadBalancers" => (HttpStatusCode.OK, sent.Form.TryGetValue("Marker", out var marker) && marker == "m2" ? Page(400, 2, null) : Page(0, 400, "m2")),
                "CreateLoadBalancer" => (HttpStatusCode.OK, Result("CreateLoadBalancer", "<LoadBalancers><member>" + LoadBalancer("kassa") + "</member></LoadBalancers>")),
                _ => (HttpStatusCode.OK, Result(sent.Form["Action"], string.Empty)),
            });
            var all = await Run(elb, "load_balancers", new() { ["names"] = "kassa, kafe, kassa", ["max"] = "1000" });
            Assert.Equal(402, all.Count);
            Assert.Equal(new[] { "Action=DescribeLoadBalancers Names.member.1=kassa Names.member.2=kafe PageSize=400", "Action=DescribeLoadBalancers Names.member.1=kassa Names.member.2=kafe PageSize=400 Marker=m2" }, elb.Requests.Select(Form));
            Assert.All(elb.Requests, sent => Assert.Equal(("2015-12-01", Api), (sent.Form["Version"], sent.Url)));
            Assert.Contains("/eu-west-1/elasticloadbalancing/aws4_request", elb.Requests[0].Authorization, StringComparison.Ordinal);
            var first = all[0].Fields;
            Assert.Equal(("kassa-0", "application", "{\"Code\":\"active\"}", "[{\"ZoneName\":\"eu-west-1a\",\"SubnetId\":\"subnet-0a1b2c3d\"}]"), ((string?)first["LoadBalancerName"], (string?)first["Type"], (string?)first["State"], (string?)first["AvailabilityZones"]));

            elb.Requests.Clear();
            Assert.Equal(3, (await Run(elb, "load_balancers", new() { ["max"] = "3" })).Count);
            Assert.Equal("Action=DescribeLoadBalancers PageSize=3", Form(elb.Requests.Single())); // a page no larger than what is wanted
            Assert.Equal("kassa-0", Assert.Single(await Run(elb, "load_balancer", new() { ["load_balancer"] = Kassa })).Fields["LoadBalancerName"]);
            Assert.Equal("Action=DescribeLoadBalancers LoadBalancerArns.member.1=" + Kassa, Form(elb.Requests.Last()));

            elb.Requests.Clear();
            var made = Assert.Single(await Run(elb, "load_balancer_create", new() { ["name"] = "kassa", ["subnets"] = "subnet-0a1b2c3d, subnet-4e5f6a7b", ["type"] = "Network", ["scheme"] = "INTERNAL", ["ip_address_type"] = "dualstack", ["security_groups"] = "sg-0a1b2c3d", ["tags"] = "{\"team\": \"kassa & kafe\"}" })).Fields;
            Assert.Equal("kassa", made["LoadBalancerName"]);
            Assert.Equal("Action=CreateLoadBalancer Name=kassa Type=network Scheme=internal IpAddressType=dualstack Subnets.member.1=subnet-0a1b2c3d Subnets.member.2=subnet-4e5f6a7b SecurityGroups.member.1=sg-0a1b2c3d Tags.member.1.Key=team Tags.member.1.Value=kassa & kafe", Form(elb.Requests.Single()));
            await Run(elb, "load_balancer_create", new() { ["name"] = "kafe", ["subnets"] = "subnet-0a1b2c3d" });
            Assert.Equal("Action=CreateLoadBalancer Name=kafe Type=application Scheme=internet-facing IpAddressType=ipv4 Subnets.member.1=subnet-0a1b2c3d", Form(elb.Requests.Last())); // n8n's defaults
            Assert.Equal(new Dictionary<string, object?> { ["load_balancer"] = Kassa, ["deleted"] = true }, Assert.Single(await Run(elb, "load_balancer_delete", new() { ["load_balancer"] = Kassa })).Fields);
            Assert.Equal("Action=DeleteLoadBalancer LoadBalancerArn=" + Kassa, Form(elb.Requests.Last()));

            var missing = new Elb(_ => (HttpStatusCode.OK, Result("DescribeLoadBalancers", "<LoadBalancers/>")));
            Assert.Equal("AWS Elastic Load Balancing knows no load balancer " + Kassa + ".", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "load_balancer", new() { ["load_balancer"] = Kassa }))).Message);
        }

        [Fact]
        public async Task Listeners_and_their_certificates_are_read_added_and_removed()
        {
            var elb = new Elb(sent => sent.Form["Action"] switch
            {
                "DescribeListeners" => (HttpStatusCode.OK, Result("DescribeListeners", "<Listeners><member><ListenerArn>" + Listener + "</ListenerArn><Port>443</Port><Protocol>HTTPS</Protocol></member></Listeners>")),
                "DescribeListenerCertificates" => (HttpStatusCode.OK, Result("DescribeListenerCertificates", "<Certificates><member><CertificateArn>" + Certificate + "</CertificateArn><IsDefault>false</IsDefault></member></Certificates>")),
                _ => (HttpStatusCode.OK, Result(sent.Form["Action"], "<Certificates><member><CertificateArn>" + Certificate + "</CertificateArn></member></Certificates>")),
            });
            var listener = Assert.Single(await Run(elb, "listeners", new() { ["load_balancer"] = Kassa })).Fields;
            Assert.Equal((Listener, "443", "HTTPS"), ((string?)listener["ListenerArn"], (string?)listener["Port"], (string?)listener["Protocol"]));
            Assert.Equal("Action=DescribeListeners LoadBalancerArn=" + Kassa + " PageSize=400", Form(elb.Requests.Last()));
            Assert.Equal(Certificate, Assert.Single(await Run(elb, "certificates", new() { ["listener"] = Listener })).Fields["CertificateArn"]);
            Assert.Equal(new Dictionary<string, object?> { ["listener"] = Listener, ["certificate"] = Certificate, ["added"] = true }, Assert.Single(await Run(elb, "certificate_add", new() { ["listener"] = Listener, ["certificate"] = Certificate })).Fields);
            Assert.Equal(new Dictionary<string, object?> { ["listener"] = Listener, ["certificate"] = Certificate, ["removed"] = true }, Assert.Single(await Run(elb, "certificate_remove", new() { ["listener"] = Listener, ["certificate"] = Certificate })).Fields);
            Assert.Equal(new[] { "Action=AddListenerCertificates ListenerArn=" + Listener + " Certificates.member.1.CertificateArn=" + Certificate, "Action=RemoveListenerCertificates ListenerArn=" + Listener + " Certificates.member.1.CertificateArn=" + Certificate }, elb.Requests.TakeLast(2).Select(Form));

            var refusing = new Elb(_ => (HttpStatusCode.BadRequest, $"<ErrorResponse {Ns}><Error><Type>Sender</Type><Code>CertificateNotFound</Code><Message>Certificate '{Certificate}' not found</Message></Error><RequestId>r-2</RequestId></ErrorResponse>"));
            Assert.Equal($"AWS Elastic Load Balancing answered HTTP 400 CertificateNotFound: Certificate '{Certificate}' not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(refusing, "certificate_add", new() { ["listener"] = Listener, ["certificate"] = Certificate }))).Message);
            var tested = await new AwsElbConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Elb(_ => (HttpStatusCode.OK, Result("DescribeLoadBalancers", "<LoadBalancers/>")))), "", Settings, Credentials));
            Assert.Equal((true, "AWS Elastic Load Balancing in eu-west-1 answers (no load balancers yet)."), (tested.Success, tested.Message));
        }
    }

    /// <summary>
    /// The AWS Elastic Load Balancing connector against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback),
    /// which checks every SigV4 signature against the IAM key it issued, through the HOST's egress: loopback refused until
    /// allowlisted; a test; an application load balancer created in two of moto's default subnets with a tag, found by name and by
    /// ARN; an HTTPS listener on it (made by the test, with a target group and an ACM certificate of moto's) listed, a second
    /// certificate added to it, listed and removed; the load balancer deleted and gone. Fails, never skips, when moto is absent.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsElbConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Credentials => new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = MotoTestEndpoint.SecretKey };

        private static Dictionary<string, string> Settings => new() { ["region"] = MotoTestEndpoint.Region };

        private static HostConnectorEgress Egress => new(new[] { MotoTestEndpoint.Host });

        private static Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new AwsElbConnector(), Contexts.Extract(Egress, MotoTestEndpoint.Endpoint, operation, parameters, Settings, Credentials, timeoutSeconds: 30));

        private static AwsQuerySession Query(string service, string version) =>
            AwsQuerySession.Of("moto", service, version, MotoTestEndpoint.Endpoint, MotoTestEndpoint.Region, MotoTestEndpoint.Endpoint, Credentials, Egress, TimeSpan.FromSeconds(30), TimeProvider.System);

        private static AwsJsonSession Acm() =>
            AwsJsonSession.Of("moto", "acm", "CertificateManager", MotoTestEndpoint.Endpoint, Settings, Credentials, Egress, TimeSpan.FromSeconds(30), TimeProvider.System);

        // EC2's Query answers carry no {Action}Result and list by <item>, so the test asks it for moto's default subnets itself.
        internal static async Task<List<(string Subnet, string Zone, string Vpc)>> SubnetsAsync()
        {
            var payload = Encoding.UTF8.GetBytes("Action=DescribeSubnets&Version=2016-11-15");
            using var client = Egress.CreateHttpClient(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Post, MotoTestEndpoint.Endpoint.TrimEnd('/') + "/") { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            AwsSigV4.Sign(request, payload, MotoTestEndpoint.Region, "ec2", MotoTestEndpoint.AccessKey, MotoTestEndpoint.SecretKey, null, DateTimeOffset.UtcNow);
            using var response = await client.SendAsync(request);
            var answer = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, answer);
            string Of(XElement item, string name) => item.Elements().Single(child => child.Name.LocalName == name).Value;
            return XElement.Parse(answer).Elements().Single(child => child.Name.LocalName == "subnetSet").Elements()
                .Where(item => Of(item, "defaultForAz") == "true") // the default VPC's, one a zone
                .Select(item => (Of(item, "subnetId"), Of(item, "availabilityZone"), Of(item, "vpcId"))).ToList();
        }

        [Fact]
        public async Task A_load_balancer_and_its_listeners_certificates_are_administered()
        {
            var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new AwsElbConnector().TestConnectionAsync(Contexts.Test(new HostConnectorEgress(Array.Empty<string>()), MotoTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30)));
            Assert.True(OutboundConnectGuard.IsDenial(denied));
            var tested = await new AwsElbConnector().TestConnectionAsync(Contexts.Test(Egress, MotoTestEndpoint.Endpoint, Settings, Credentials, timeoutSeconds: 30));
            output.WriteLine($"[live] {tested.Success}: {tested.Message}");
            Assert.True(tested.Success, tested.Message);

            // Two of moto's default subnets in two zones of one VPC, which an application load balancer needs.
            var zones = (await SubnetsAsync()).GroupBy(subnet => subnet.Vpc).First().GroupBy(subnet => subnet.Zone).Take(2).Select(zone => zone.First()).ToList();
            Assert.Equal(2, zones.Count);
            var name = "kassa-" + Guid.NewGuid().ToString("N")[..8];
            var made = Assert.Single(await Run("load_balancer_create", new() { ["name"] = name, ["subnets"] = string.Join(",", zones.Select(zone => zone.Subnet)), ["tags"] = "{\"team\": \"kassa\"}" })).Fields;
            var arn = (string)made["LoadBalancerArn"]!;
            var elbv2 = Query("elasticloadbalancing", "2015-12-01");
            string? group = null;
            var certificates = new List<string>();
            try
            {
                output.WriteLine($"[live] made {arn}");
                Assert.Equal(("application", "internet-facing"), ((string?)made["Type"], (string?)made["Scheme"]));
                Assert.Equal(arn, Assert.Single(await Run("load_balancers", new() { ["names"] = name })).Fields["LoadBalancerArn"]);
                Assert.Equal(name, Assert.Single(await Run("load_balancer", new() { ["load_balancer"] = arn })).Fields["LoadBalancerName"]);

                // An HTTPS listener of the test's own - a target group and a certificate of moto's ACM - for the certificates to go on.
                group = AwsQuerySession.Child(AwsQuerySession.Members(await elbv2.CallAsync("CreateTargetGroup", new[] { ("Name", name), ("Protocol", "HTTP"), ("Port", "80"), ("VpcId", zones[0].Vpc) }, default), "TargetGroups").First(), "TargetGroupArn")!;
                foreach (var domain in new[] { name + ".kafe.example", "www." + name + ".kafe.example" })
                {
                    certificates.Add((await Acm().CallAsync("RequestCertificate", new JsonObject { ["DomainName"] = domain }, default))!["CertificateArn"]!.GetValue<string>());
                }

                await elbv2.CallAsync("CreateListener", new[] { ("LoadBalancerArn", arn), ("Protocol", "HTTPS"), ("Port", "443"), ("Certificates.member.1.CertificateArn", certificates[0]), ("DefaultActions.member.1.Type", "forward"), ("DefaultActions.member.1.TargetGroupArn", group) }, default);

                var listener = Assert.Single(await Run("listeners", new() { ["load_balancer"] = arn })).Fields;
                Assert.Equal(("HTTPS", "443"), ((string?)listener["Protocol"], (string?)listener["Port"]));
                var listenerArn = (string)listener["ListenerArn"]!;
                Assert.True((bool)Assert.Single(await Run("certificate_add", new() { ["listener"] = listenerArn, ["certificate"] = certificates[1] })).Fields["added"]!);
                Assert.Contains(await Run("certificates", new() { ["listener"] = listenerArn }), found => (string?)found.Fields["CertificateArn"] == certificates[1]);
                Assert.True((bool)Assert.Single(await Run("certificate_remove", new() { ["listener"] = listenerArn, ["certificate"] = certificates[1] })).Fields["removed"]!);
                Assert.DoesNotContain(await Run("certificates", new() { ["listener"] = listenerArn }), found => (string?)found.Fields["CertificateArn"] == certificates[1]);
            }
            finally
            {
                Assert.True((bool)Assert.Single(await Run("load_balancer_delete", new() { ["load_balancer"] = arn })).Fields["deleted"]!);
                if (group is not null)
                {
                    await elbv2.CallAsync("DeleteTargetGroup", new[] { ("TargetGroupArn", group) }, default);
                }

                foreach (var certificate in certificates)
                {
                    await Acm().CallAsync("DeleteCertificate", new JsonObject { ["CertificateArn"] = certificate }, default);
                }
            }

            Assert.DoesNotContain(await Run("load_balancers", new()), found => (string?)found.Fields["LoadBalancerName"] == name);
            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("load_balancer", new() { ["load_balancer"] = arn }));
            output.WriteLine($"[live] gone: {gone.Message}");
        }
    }
}
