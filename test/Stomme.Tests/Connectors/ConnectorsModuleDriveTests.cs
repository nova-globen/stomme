#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-96 increment 1 - the family on installed hosts on SQLite and PostgreSQL, over HTTP, with the demo content on:
    /// the catalog lists <c>rest</c> and <c>postgresql</c>, the two demo connections say their connector IS installed now,
    /// the REST demo connection repointed at a private address is refused BY POLICY through the real egress without a
    /// socket, and the PostgreSQL demo connection - which declares a password it never received - is refused for its
    /// missing credential before any dial. Nothing in this drive reaches the internet.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ConnectorsModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_real_connectors_serve_the_demo_connections_and_the_egress_decides(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject { ["DemoContent"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var catalog = JsonSerializer.Deserialize<List<ConnectorView>>((await GateDrive.GetAsync(client, cookies, Host, "/api/Connections/connectors", output, "catalog")).Body, Json)!;
                Assert.Contains(catalog, c => c.ConnectorType == "rest" && c.AuthType == "Bearer" && c.EndpointKind == "Url");
                Assert.Contains(catalog, c => c.ConnectorType == "postgresql" && c.AuthType == "Basic" && c.EndpointKind == "ConnectionString");
                Assert.Equal(new[] { "activecampaign", "activedirectory", "airtable", "amqp", "asana", "awsacm", "awscognito", "awscomprehend", "awscost", "awsdynamodb", "awselb", "awsiam", "awslambda", "awsrekognition", "awss3", "awsses", "awssns", "awssqs", "awstextract", "awstranscribe", "azurecosmosdb", "azurecost", "azurestorage", "azurevm", "baserow", "bitly", "bitwarden", "box", "brevo", "circleci", "ciscodnac", "clickup", "clockify", "coda", "contentful", "cylance", "datadogcost", "deepl", "discord", "discourse", "dropbox", "elasticsearch", "elasticsecurity", "entra", "entuity", "erpnext", "facebookgraph", "flexeraone", "fnms", "freshdesk", "freshservice", "ftp", "gcpcost", "ghost", "github", "githubcost", "gitlab", "gmail", "googleanalytics", "googlebigquery", "googlebusinessprofile", "googlecalendar", "googlechat", "googlecloudstorage", "googlecontacts", "googledocs", "googledrive", "googlefirestore", "googlesheets", "googleslides", "googletasks", "googletranslate", "googleworkspaceadmin", "gotify", "grafana", "grist", "halopsa", "harvest", "helpscout", "highlevel", "homeassistant", "hubspot", "hunter", "imap", "intercom", "intune", "invoiceninja", "jenkins", "jira", "kit", "ldap", "linear", "linkedin", "m365cost", "mailchimp", "mailerlite", "mailgun", "mailjet", "mailjetsms", "matrix", "mattermost", "mautic", "metabase", "microsoftdynamics", "microsoftexcel", "microsoftgraphsecurity", "microsoftonedrive", "microsoftsharepoint", "microsofttodo", "misp", "monday", "mongodb", "monicacrm", "mqtt", "mysql", "netlify", "nextcloud", "nocodb", "notion", "odoo", "okta", "openweathermap", "outlook", "pagerduty", "pipedrive", "postgresql", "posthog", "pushover", "qualys", "quickchart", "rabbitmq", "rapid7", "reddit", "redis", "rest", "rocketchat", "rundeck", "salesforce", "salesforcecost", "satellite", "seatable", "sendgrid", "sentry", "servicenow", "sftp", "sftpkey", "shopify", "slack", "snowflake", "snowflakecost", "splunk", "sqlserver", "ssh", "sshkey", "strapi", "stripe", "supabase", "susemanager", "taegis", "taiga", "teams", "telegram", "tenable", "thehive", "todoist", "trello", "twilio", "uptimerobot", "urlscanio", "vonage", "webflow", "wekan", "whatsapp", "woocommerce", "wordpress", "xml", "yourls", "youtube", "zammad", "zendesk", "zohocrm", "zoom", "zulip" }, catalog.Select(c => c.ConnectorType).OrderBy(t => t, System.StringComparer.Ordinal));
                Assert.True(catalog.Single(c => c.ConnectorType == "xml").HostOnly);

                // HostOnly, driven with the real xml connector: an administrator holding connections:manage is refused, the Host is admitted.
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail, RoleNames.Admin);
                using var adminClient = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, adminClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var adminId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                var xmlRequest = new ConnectionRequest { Name = "Telecom XML", ConnectorType = "xml", Endpoint = "%TEMP%", Settings = new() { ["source_path"] = "%TEMP%", ["destination_path"] = "%TEMP%", ["arguments"] = "run" }, TimeoutSeconds = 5 };
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.SendJsonAsync(app, adminClient, adminCookies, Host, adminId, HttpMethod.Post, "/api/Connections", xmlRequest, output, "xml as an administrator")).Status);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Connections", xmlRequest, output, "xml as the Host")).Status);

                var connections = JsonSerializer.Deserialize<List<ConnectionView>>((await GateDrive.GetAsync(client, cookies, Host, "/api/Connections", output, "connections")).Body, Json)!;
                Assert.All(connections, c => Assert.True(c.ConnectorInstalled, c.Name));
                var rest = connections.Single(c => c.Name == "Demo REST API");
                var postgres = connections.Single(c => c.Name == "Demo PostgreSQL");
                Assert.Equal(("REST API", "PostgreSQL"), (rest.ConnectorDisplayName, postgres.ConnectorDisplayName));

                // The REST connection repointed at a private address: the real egress refuses it where the bytes would go - DeniedByPolicy, no socket.
                var edited = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Connections/{rest.Id}",
                    new ConnectionRequest { Name = rest.Name, ConnectorType = "rest", Endpoint = "http://10.255.255.1/status", TimeoutSeconds = 5 }, output, "repoint the REST demo");
                Assert.Equal(HttpStatusCode.OK, edited.Status);
                var restVerdict = JsonSerializer.Deserialize<ConnectionTestView>((await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Connections/{rest.Id}/test", null, output, "test the REST demo")).Body, Json)!;
                output.WriteLine($"[{storeName}] rest -> {restVerdict.Outcome}: {restVerdict.Message}");
                Assert.Equal((false, ConnectorOutcome.DeniedByPolicy, "Failing"), (restVerdict.Success, restVerdict.Outcome, restVerdict.Status));
                Assert.DoesNotContain("10.255.255.1", restVerdict.Message); // a category, never the connector's or the guard's words

                // The PostgreSQL demo connection declares a password it never received: refused for the missing credential, before any pin or dial.
                var pgVerdict = JsonSerializer.Deserialize<ConnectionTestView>((await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Connections/{postgres.Id}/test", null, output, "test the PostgreSQL demo")).Body, Json)!;
                output.WriteLine($"[{storeName}] postgresql -> {pgVerdict.Outcome}: {pgVerdict.Message}");
                Assert.Equal((false, ConnectorOutcome.CredentialsMissing), (pgVerdict.Success, pgVerdict.Outcome));

                // With a password stored, the pin refuses localhost (loopback is never permitted): DeniedByPolicy, the driver never dials.
                var withPassword = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Connections/{postgres.Id}",
                    new ConnectionRequest { Name = postgres.Name, ConnectorType = "postgresql", Endpoint = postgres.Endpoint, Credentials = new() { ["username"] = "demo", ["password"] = "demo" }, TimeoutSeconds = 5 }, output, "store the demo password");
                Assert.Equal(HttpStatusCode.OK, withPassword.Status);
                var pinned = JsonSerializer.Deserialize<ConnectionTestView>((await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Connections/{postgres.Id}/test", null, output, "test the PostgreSQL demo with a password")).Body, Json)!;
                output.WriteLine($"[{storeName}] postgresql (password) -> {pinned.Outcome}: {pinned.Message}");
                Assert.Equal(ConnectorOutcome.DeniedByPolicy, pinned.Outcome);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
