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
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-235 - a success answer a connector cannot read is a failure, never an empty result: every in-tree connector's
    /// connection test, against a stand-in answering each call with a sign-in page under HTTP 200 (a proxy's, or Cloudflare
    /// Access's) - a token issued where one is asked for, so the test reaches the service - does not call the connection
    /// healthy. The connectors that reach no HTTP service here are left out by kind (a driver's connection string) and by name
    /// (FTP, IMAP, LDAP), as is the REST connector, whose test asks only that the address answers.
    /// </summary>
    public sealed class ConnectorSignInPageTests
    {
        // Not HTTP services, or - the REST connector - one whose test asks only that the address answers.
        private static readonly HashSet<string> LeftOut = new(StringComparer.OrdinalIgnoreCase) { "rest", "ftp", "imap", "activedirectory" };

        private const string Page = "<!DOCTYPE html><html><head><title>Sign in</title></head><body><form>Sign in to continue</form></body></html>";

        private sealed class SignInPage : HttpMessageHandler
        {
            public int Calls;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                if (request.Method == HttpMethod.Post && body.Contains("grant_type=", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"sign-in-test\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json") };
                }

                Interlocked.Increment(ref Calls);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page, Encoding.UTF8, "text/html") };
            }
        }

        public static IEnumerable<object[]> Connectors() =>
            InTreeConnectors.All().Where(connector => connector.EndpointKind == ConnectorEndpointKind.Url && !LeftOut.Contains(connector.ConnectorType))
                .Select(connector => connector.ConnectorType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(type => type, StringComparer.Ordinal).Select(type => new object[] { type });

        // Each credential the scheme declares, a token endpoint an address; each setting with a default its default.
        private static async Task<(ConnectionTestResult? Result, Exception? Thrown, int Calls)> TestAsync(IConnector connector)
        {
            var handler = new SignInPage();
            var credentials = AuthenticationScheme.FieldsFor(connector.AuthType).ToDictionary(field => field.Name, field => field.Name switch
            {
                "token-url" => "https://login.example.test/oauth2/token",
                _ => "sign-in-test-" + field.Name,
            });
            var settings = connector.ConfigSchema.Fields.Where(field => !string.IsNullOrEmpty(field.Default)).ToDictionary(field => field.Name, field => field.Default!);
            var endpoint = connector.EndpointExample ?? "https://api.example.test";
            try
            {
                return (await connector.TestConnectionAsync(Contexts.Test(new StubEgress(handler), endpoint, settings, credentials)), null, handler.Calls);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or FormatException or System.Xml.XmlException)
            {
                return (null, ex, handler.Calls);
            }
        }

        [Theory]
        [MemberData(nameof(Connectors))]
        public async Task A_sign_in_page_under_HTTP_200_is_never_a_healthy_connection(string type)
        {
            var (result, _, calls) = await TestAsync(InTreeConnectors.All().First(connector => string.Equals(connector.ConnectorType, type, StringComparison.OrdinalIgnoreCase)));
            Assert.False(result is { Success: true } && calls > 0, $"{type} called a sign-in page healthy: {result?.Message}");
        }

        // A floor, not a count: 95 of the family's tests reached the stand-in when this was written, so the theory above is not vacuous for them.
        [Fact]
        public async Task Most_of_the_family_reaches_the_sign_in_page()
        {
            var reached = 0;
            foreach (var connector in InTreeConnectors.All().Where(connector => connector.EndpointKind == ConnectorEndpointKind.Url && !LeftOut.Contains(connector.ConnectorType)))
            {
                reached += (await TestAsync(connector)).Calls > 0 ? 1 : 0;
            }

            Assert.True(reached >= 90, $"only {reached} connectors' tests reached the stand-in");
        }
    }
}
