#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Ftp;
using FluentFTP;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The predecessor platform's FtpConnectorTests (13 + 8 wildcard rows) by name over the port, plus the pin and the
    /// data channel: the control connection is built on the address the host pinned, the data connection is EPSV only.
    /// CHANGED: <c>ResolveOptions_FallsBackToTheLegacySingleCredential_AsThePassword</c> - there is no single credential;
    /// the scheme's password is the password and its absence is an empty one.
    /// </summary>
    public sealed class FtpConnectorTests
    {
        private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

        private static readonly IReadOnlyDictionary<string, string> BasicCredentials = new Dictionary<string, string> { ["username"] = "alice", ["password"] = "s3cret" };

        [Fact]
        public void Declarations_ExposeTheFtpConnectorType_WithBasicAuthAndExtract()
        {
            IConnector connector = new FtpConnector();
            Assert.Equal(("ftp", "FTP / FTPS", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.TestConnection));
            Assert.True(connector.Capabilities.HasFlag(ConnectorCapabilities.Extract));
            Assert.Equal((ConnectorEndpointKind.ConnectionString, "ftp://ftp.kafe.example:21"), (connector.EndpointKind, connector.EndpointExample)); // not a web address
        }

        [Fact]
        public void ConfigSchema_DeclaresHostPortEncryptionAndCertificateFields()
        {
            var fields = new FtpConnector().ConfigSchema.Fields;
            Assert.Equal(new[] { "host", "port", "encryption_mode", "danger_accept_any_certificate" }, fields.Select(f => f.Name).ToArray());
            Assert.False(fields.Single(f => f.Name == "host").Required); // the address names the server; a host here, an older connection's, wins
            var port = fields.Single(f => f.Name == "port");
            Assert.Equal((ConnectorConfigFieldType.Integer, null), (port.Type, port.Default)); // a default would always be saved, and the address's port never read
            var encryption = fields.Single(f => f.Name == "encryption_mode");
            Assert.Equal((ConnectorConfigFieldType.Select, "none"), (encryption.Type, encryption.Default));
            Assert.Equal(new[] { "none", "explicit", "implicit", "auto" }, encryption.Options.Select(o => o.Value).ToArray());
            var certificate = fields.Single(f => f.Name == "danger_accept_any_certificate");
            Assert.Equal((ConnectorConfigFieldType.Boolean, "false"), (certificate.Type, certificate.Default));
        }

        [Fact]
        public void ConfigSchema_RejectsANonIntegerPort_AndAnUnknownEncryptionMode()
        {
            var schema = new FtpConnector().ConfigSchema;
            Assert.Empty(schema.Validate(new Dictionary<string, string>())); // the server: the address's
            Assert.Contains(schema.Validate(new Dictionary<string, string> { ["host"] = "h", ["port"] = "abc" }), e => e.Contains("port"));
            Assert.Contains(schema.Validate(new Dictionary<string, string> { ["host"] = "h", ["encryption_mode"] = "tls" }), e => e.Contains("encryption_mode"));
        }

        [Fact]
        public void Operations_AreTheFileServersOnes_ListAndDownloadKeepingTheirSharedPathAndPatternParameters()
        {
            var operations = new FtpConnector().Operations;
            Assert.Equal(new[] { "list", "download", "download_file", "upload", "delete", "rename" }, operations.Select(o => o.Name).ToArray());
            Assert.All(operations.Take(2), o => Assert.Contains(o.Parameters.Fields, f => f.Name == "path" && f.Default == "/"));
            Assert.All(operations.Take(2), o => Assert.Contains(o.Parameters.Fields, f => f.Name == "pattern" && f.Default == "*"));
            Assert.Contains(operations.Single(o => o.Name == "download").Parameters.Fields, f => f.Name == "target_folder" && f.Required);
            Assert.Contains("FTP server", operations[0].Description, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TestConnection_WithoutAnAddressOrAHostSetting_Fails_WithAnActionableMessage()
        {
            async Task<string> Failed(string endpoint)
            {
                var egress = new StubEgress();
                var result = await new FtpConnector().TestConnectionAsync(Contexts.Test(egress, endpoint, NoValues, BasicCredentials));
                Assert.False(result.Success);
                Assert.Empty(egress.Pins);
                return result.Message;
            }

            Assert.Equal("Give the FTP server's address - ftp://ftp.kafe.example:21 - or its 'host' setting.", await Failed(" "));
            Assert.Equal("'https://ftp.kafe.example' is not an FTP server's address - ftp://ftp.kafe.example:21.", await Failed("https://ftp.kafe.example"));
            Assert.Equal("The address carries a user - the connection's username signs in, never its address (ftp://ftp.kafe.example:21).", await Failed("ftp://alice:pw@ftp.kafe.example"));
            Assert.Equal("'ftp://ftp.kafe.example/in' names more than the server - its address alone (ftp://ftp.kafe.example:21); a step's path names its folder.", await Failed("ftp://ftp.kafe.example/in"));
            Assert.Contains("names more than the server", await Failed("ftp://ftp.kafe.example?x=1"), StringComparison.Ordinal);
            Assert.Contains("names more than the server", await Failed("ftp://ftp.kafe.example#x"), StringComparison.Ordinal);
        }

        [Fact]
        public void The_server_is_the_addresss_unless_a_host_setting_names_it()
        {
            Assert.Equal(("ftp.kafe.example", 21), FtpConnector.Server("ftp://FTP.kafe.example"));
            Assert.Equal(("ftp.kafe.example", 21), FtpConnector.Server("ftp.kafe.example/"));
            Assert.Equal(("ftp.kafe.example", 2121), FtpConnector.Server("ftp.kafe.example:2121"));
            Assert.Equal(("ftp.kafe.example", 21), FtpConnector.Server("ftps://ftp.kafe.example"));
            Assert.Equal(("ftp.kafe.example", 990), FtpConnector.Server("ftps://ftp.kafe.example:990"));
            Assert.Equal(("::1", 2121), FtpConnector.Server("ftp://[::1]:2121"));
            var byAddress = FtpConnector.ResolveOptions(new Dictionary<string, string> { ["port"] = "9" }, BasicCredentials, TimeSpan.FromSeconds(5), "ftp://ftp.kafe.example:2121");
            Assert.Equal(("ftp.kafe.example", 2121), (byAddress.Host, byAddress.Port)); // a port without a host setting is not read
            var bySetting = FtpConnector.ResolveOptions(new Dictionary<string, string> { ["host"] = " legacy.kafe.example " }, BasicCredentials, TimeSpan.FromSeconds(5), "https://whatever.example");
            Assert.Equal(("legacy.kafe.example", 21), (bySetting.Host, bySetting.Port)); // an older connection: its host setting, its address unread
        }

        [Fact]
        public async Task TestConnection_WithoutAUsernameCredential_Fails()
        {
            var result = await new FtpConnector().TestConnectionAsync(Contexts.Test(new StubEgress(), "ftp://ignored", new Dictionary<string, string> { ["host"] = "ftp.example.com" }));
            Assert.False(result.Success);
            Assert.Contains("username", result.Message);
        }

        [Fact]
        public async Task TestConnection_Fails_ForAnUnreachableServer()
        {
            var result = await new FtpConnector().TestConnectionAsync(Contexts.Test(new StubEgress { Pinned = IPAddress.Loopback }, "ftp://ignored", new Dictionary<string, string> { ["host"] = "ftp.internal", ["port"] = "1" }, BasicCredentials, timeoutSeconds: 3));
            Assert.False(result.Success);
            Assert.NotEmpty(result.Message);
        }

        [Theory]
        [InlineData("report.csv", "*.csv", true)]
        [InlineData("REPORT.CSV", "*.csv", true)]
        [InlineData("report.csv1", "*.csv", false)]
        [InlineData("report.csv", "report.???", true)]
        [InlineData("report.csv", "r?port.csv", true)]
        [InlineData("report.csv", "*", true)]
        [InlineData("report.csv", "data*", false)]
        [InlineData("a+b.txt", "a+b.*", true)]
        public void MatchesWildcard_AppliesAnchoredCaseInsensitiveStarAndQuestionSemantics(string name, string pattern, bool expected) => Assert.Equal(expected, FtpConnector.MatchesWildcard(name, pattern));

        [Fact]
        public async Task Extract_DownloadWithoutAFileSink_Throws_TheManagedStorageError()
        {
            var context = Contexts.Extract(new StubEgress(), "ftp://ignored", "download", new Dictionary<string, string> { ["target_folder"] = "imports" }, new Dictionary<string, string> { ["host"] = "ftp.example.com" }, BasicCredentials);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FtpConnector(), context));
            Assert.Contains("does not provide managed file storage", ex.Message);
        }

        [Fact]
        public async Task Extract_DownloadWithoutATargetFolder_Throws()
        {
            var context = Contexts.Extract(new StubEgress(), "ftp://ignored", "download", null, new Dictionary<string, string> { ["host"] = "ftp.example.com" }, BasicCredentials);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new FtpConnector(), context));
            Assert.Contains("target_folder", ex.Message);
        }

        [Fact]
        public void ResolveOptions_AppliesTheNamedCredentials_SettingsAndDefaults()
        {
            var options = FtpConnector.ResolveOptions(new Dictionary<string, string> { ["host"] = " ftp.example.com ", ["encryption_mode"] = "Explicit", ["danger_accept_any_certificate"] = "true" }, BasicCredentials, TimeSpan.FromSeconds(15));
            Assert.Equal(("ftp.example.com", 21, FtpEncryptionMode.Explicit, true, "alice", "s3cret"), (options.Host, options.Port, options.EncryptionMode, options.DangerAcceptAnyCertificate, options.Username, options.Password));
        }

        [Fact]
        public void ResolveOptions_FallsBackToTheLegacySingleCredential_AsThePassword()
        {
            // CHANGED: no single credential exists; without the scheme's password the password is empty.
            var options = FtpConnector.ResolveOptions(new Dictionary<string, string> { ["host"] = "ftp.example.com", ["port"] = "2121" }, new Dictionary<string, string> { ["username"] = "alice" }, TimeSpan.FromSeconds(5));
            Assert.Equal((2121, FtpEncryptionMode.None, false, string.Empty), (options.Port, options.EncryptionMode, options.DangerAcceptAnyCertificate, options.Password));
        }

        [Fact]
        public void ResolveOptions_RejectsAnOutOfRangePort()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => FtpConnector.ResolveOptions(new Dictionary<string, string> { ["host"] = "ftp.example.com", ["port"] = "70000" }, BasicCredentials, TimeSpan.FromSeconds(5)));
            Assert.Contains("port", ex.Message);
        }

        // ---- the port's additions: the pin and the data channel --------------------------------------------------------

        [Fact]
        public async Task A_refused_host_is_the_contracts_denial_and_the_pin_carries_the_configured_port()
        {
            var refused = new StubEgress { Pinned = null };
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => new FtpConnector().TestConnectionAsync(Contexts.Test(refused, "ftp://ignored", new Dictionary<string, string> { ["host"] = "ftp.internal", ["port"] = "2121" }, BasicCredentials)));
            Assert.Equal(("ftp.internal", 2121), refused.Pins.Single());
        }

        [Fact]
        public void The_client_dials_the_pinned_address_with_an_epsv_only_data_channel_and_the_timeout_on_every_channel()
        {
            var options = FtpConnector.ResolveOptions(new Dictionary<string, string> { ["host"] = "ftp.example.com", ["encryption_mode"] = "explicit" }, BasicCredentials, TimeSpan.FromSeconds(7));
            using var client = FtpConnector.CreateClient(options, IPAddress.Parse("203.0.113.9"));
            Assert.Equal(("203.0.113.9", 21, FtpDataConnectionType.EPSV, FtpEncryptionMode.Explicit, false), (client.Host, client.Port, client.Config.DataConnectionType, client.Config.EncryptionMode, client.Config.ValidateAnyCertificate));
            Assert.Equal((7000, 7000, 7000, 7000), (client.Config.ConnectTimeout, client.Config.ReadTimeout, client.Config.DataConnectionConnectTimeout, client.Config.DataConnectionReadTimeout));
        }
    }
}
