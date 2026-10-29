#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectors.Cost;
using Stomme.Modules.Connectors.Startup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>The thirty in-tree connectors as the family's own startup composes them - the catalogue the host serves, not a hand list.</summary>
    internal static class InTreeConnectors
    {
        public static IReadOnlyList<IConnector> All()
        {
            var services = new ServiceCollection();
            new ServerStartup().ConfigureServices(services);
            using var provider = services.BuildServiceProvider();
            return provider.GetServices<IConnector>().ToList();
        }
    }

    /// <summary>
    /// The ConnectorCatalogueMetadataTests cases (5), kept by name, over the port, plus the brand rules of the
    /// icon and logo licensing review as tests: every catalogue icon is the brand-neutral monogram (no in-tree connector
    /// declares an icon of its own, no image asset sits under the family), and the connections page carries the
    /// third-party trademark notice.
    /// </summary>
    public sealed class ConnectorCatalogueMetadataTests
    {
        private static readonly HashSet<string> GenericConnectorTypes = new(StringComparer.Ordinal) { "rest", "ftp", "sftp", "sftpkey", "ssh", "sshkey", "xml", "imap" };

        [Fact]
        public void EveryConnector_DeclaresCategories_AndVendorsDeclareHttpsLinks()
        {
            var connectors = InTreeConnectors.All();
            Assert.Equal(183, connectors.Count);

            foreach (var connector in connectors)
            {
                Assert.NotEmpty(connector.Categories);
                Assert.DoesNotContain(ConnectorCategory.Uncategorized, connector.Categories);
                if (!GenericConnectorTypes.Contains(connector.ConnectorType))
                {
                    Assert.NotNull(connector.WebsiteUrl);
                }

                foreach (var url in new[] { connector.WebsiteUrl, connector.ApiDocumentationUrl })
                {
                    if (url is not null)
                    {
                        Assert.True(url.IsAbsoluteUri, $"{connector.ConnectorType}: '{url}' is not absolute.");
                        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
                    }
                }
            }
        }

        [Fact]
        public void EveryConnector_ProjectsANonEmptyInlineSvgIcon()
        {
            var views = InTreeConnectors.All().Select(ConnectorViews.From).ToList();

            Assert.Equal(183, views.Count);
            foreach (var view in views)
            {
                Assert.False(string.IsNullOrWhiteSpace(view.Icon), $"{view.ConnectorType}: icon is empty.");
                Assert.Contains("<svg", view.Icon, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void MicrosoftCostConnectors_AreDualListed_UnderCloudCostAndMicrosoftCloudDirectory()
        {
            foreach (var categories in new[] { new AzureCostConnector().Categories, new M365CostConnector().Categories })
            {
                Assert.Contains(ConnectorCategory.CloudAndSaasCost, categories);
                Assert.Contains(ConnectorCategory.MicrosoftCloudDirectory, categories);
            }
        }

        [Fact]
        public void HyperscalerCostConnectors_CarryTheCloudAndSaasCostCategory()
        {
            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(new AwsCostConnector().Categories));
            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(new GcpCostConnector().Categories));
        }

        [Fact]
        public void SaasCostConnectors_CarryTheCloudAndSaasCostCategory()
        {
            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(new DatadogCostConnector().Categories));
            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(new SnowflakeCostConnector().Categories));
            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(new GithubCostConnector().Categories));
            Assert.Equal(ConnectorCategory.CloudAndSaasCost, Assert.Single(new SalesforceCostConnector().Categories));
        }

        [Fact]
        public void Every_catalogue_icon_is_the_neutral_monogram_and_no_vendor_asset_ships_with_the_family()
        {
            // The licensing review cleared no vendor logo for the catalogue: a connector declares no icon of its own, so every
            // icon is the monogram - a rounded rectangle in the category's colour and two letters, nothing drawn or embedded.
            foreach (var connector in InTreeConnectors.All())
            {
                Assert.True(string.IsNullOrWhiteSpace(connector.Icon), $"{connector.ConnectorType} declares an icon of its own; the catalogue shows the neutral monogram only.");
                var icon = ConnectorIcons.For(connector);
                Assert.Equal(ConnectorIcons.Monogram(connector.DisplayName, connector.Categories[0]), icon);
                Assert.DoesNotContain("<image", icon, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("<path", icon, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("href", icon, StringComparison.OrdinalIgnoreCase);
            }

            var family = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Modules", "Connectors");
            var assets = Directory.GetFiles(family, "*.*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(assets);
        }

        [Fact]
        public void The_connections_page_carries_the_trademark_notice_beside_the_catalogue()
        {
            var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Client", "Modules", "Connectivity", "Index.razor"));

            Assert.Contains("ConnectorTrademarkNotice.Text", page, StringComparison.Ordinal);
            Assert.Contains("connections-trademarks", page, StringComparison.Ordinal);
            Assert.Contains("not affiliated with", ConnectorTrademarkNotice.Text, StringComparison.Ordinal);
            Assert.Contains("property of their respective owners", ConnectorTrademarkNotice.Text, StringComparison.Ordinal);
        }

        // A connector's own example endpoint is one a connection to it can be made with: an address that is not HTTP's declares the
        // connection-string kind. The Active Directory connector named ldap:// and ldaps:// hosts while keeping the URL kind, so
        // every connection it was offered was refused at the API.
        [Fact]
        public void Every_connectors_example_endpoint_is_one_a_connection_accepts()
        {
            static bool Accepts(ConnectorEndpointKind kind, string endpoint)
            {
                try
                {
                    Stomme.Modules.Connectivity.Domain.ConnectionEndpoint.For(kind, endpoint);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            var connectors = InTreeConnectors.All();
            var refused = connectors.Where(connector => connector.EndpointExample is { } example && !Accepts(connector.EndpointKind, example)).Select(connector => $"{connector.ConnectorType}: '{connector.EndpointExample}'").ToList();
            Assert.True(refused.Count == 0, "A connection refuses its connector's own example: " + string.Join(", ", refused));
            Assert.True(connectors.Count(connector => connector.EndpointExample is not null) > 20); // the rule reaches connectors, not none
            Assert.Contains(connectors, connector => connector.EndpointKind == ConnectorEndpointKind.ConnectionString && connector.EndpointExample is { } example && !example.StartsWith("http", StringComparison.Ordinal));
        }

        [Fact]
        public void Every_catalogue_name_is_the_name_the_licensing_review_records()
        {
            // A connector is named as the licensing review names the product - its current owner's spelling, with the
            // former name beside it after a rename (Cylance, Taegis) - so a name is changed in the review first, never here alone.
            var registry = Path.Combine(RepositoryRoot(), "docs", "legal", "connector-icon-licensing-review", "BRAND_ASSET_REGISTRY.json");
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(registry));
            var reviewed = document.RootElement.GetProperty("products").EnumerateArray()
                .Select(product => product.GetProperty("displayName").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(reviewed.Count >= 29, "The registry lists fewer products than the catalogue holds connectors.");

            var unreviewed = InTreeConnectors.All()
                .Where(connector => !reviewed.Contains(connector.DisplayName))
                .Select(connector => $"{connector.ConnectorType}: '{connector.DisplayName}'")
                .ToList();
            Assert.True(unreviewed.Count == 0, "Not named as the licensing review names them: " + string.Join(", ", unreviewed));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }

    /// <summary>The ConnectorTypeIdStabilityTests case, kept by name: the persisted connector type ids are exactly this set, and a rename is a migration, not an edit.</summary>
    public sealed class ConnectorTypeIdStabilityTests
    {
        [Fact]
        public void PluginConnectorTypeIds_AreExactlyTheStablePersistedSet()
        {
            string[] expected =
            {
                "rest", "postgresql", "sqlserver", "ftp", "intune", "entra", "azurevm", "taegis", "tenable", "rapid7", "cylance", "qualys",
                "flexeraone", "servicenow", "fnms", "ciscodnac", "entuity", "susemanager", "satellite", "activedirectory", "xml",
                "awscost", "azurecost", "gcpcost", "datadogcost", "snowflakecost", "githubcost", "m365cost", "salesforcecost", "imap",
                "slack", "telegram", "discord", "googlesheets", "airtable", "notion", "gmail", "outlook", "hubspot", "jira", "mattermost",
                "twilio", "github", "googlecalendar", "trello", "asana", "teams", "clickup", "stripe", "salesforce", "shopify", "pipedrive", "whatsapp", "zendesk", "supabase", "mailchimp", "woocommerce", "linear", "gitlab", "freshdesk", "monday", "todoist", "wordpress", "sendgrid", "googletasks", "microsofttodo", "microsoftexcel", "googlecontacts", "googledocs", "odoo", "brevo", "activecampaign", "zohocrm", "intercom", "zoom", "mailgun", "webflow", "youtube", "linkedin", "reddit", "awsses", "awssqs", "awssns", "awsdynamodb", "awslambda", "nocodb", "grist", "coda", "ghost", "clockify", "harvest", "strapi", "contentful", "elasticsearch", "deepl", "pagerduty", "bitly", "facebookgraph", "zulip", "mailerlite", "mautic", "netlify", "rocketchat", "jenkins", "kit", "matrix", "sentry", "discourse", "zammad", "pushover", "gotify", "uptimerobot", "grafana", "okta", "splunk", "bitwarden", "googleworkspaceadmin", "mailjet", "mailjetsms", "microsoftgraphsecurity", "microsoftdynamics", "googlebigquery", "freshservice", "microsoftsharepoint", "halopsa", "snowflake", "helpscout", "awss3", "googledrive", "microsoftonedrive", "mysql", "redis", "mongodb", "sftp", "sftpkey", "ssh", "sshkey", "dropbox", "baserow", "rabbitmq", "mqtt", "nextcloud", "azurestorage", "googlecloudstorage", "box", "awstextract", "awsrekognition", "awscomprehend", "ldap", "homeassistant", "azurecosmosdb", "googlefirestore", "googlechat", "awstranscribe", "awscognito", "googletranslate", "awsacm", "googleslides", "metabase", "posthog", "awsiam", "awselb", "googleanalytics", "hunter", "highlevel", "openweathermap", "googlebusinessprofile", "vonage", "circleci", "misp", "urlscanio", "elasticsecurity", "thehive", "yourls", "wekan", "rundeck", "taiga", "quickchart", "monicacrm", "invoiceninja", "erpnext", "amqp", "seatable",
            };

            var actual = InTreeConnectors.All().Select(connector => connector.ConnectorType).ToArray();

            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
            Assert.Equal(183, new ConnectorCatalog(InTreeConnectors.All()).All.Count); // the host's catalogue admits every one, no duplicate key
        }
    }

    /// <summary>
    /// A connector names its credential fields in the service's own words (Trello's "Token", Twilio's "Account SID"):
    /// each label is for a field its scheme declares, one line, never empty - the field's NAME, and so where its secret is kept,
    /// stays the scheme's - and the catalogue the editor reads shows the label beside the unchanged name.
    /// </summary>
    public sealed class ConnectorCredentialLabelTests
    {
        [Fact]
        public void Every_label_is_for_a_field_of_the_scheme_and_says_something()
        {
            var relabelled = 0;
            foreach (var connector in InTreeConnectors.All())
            {
                if (connector.CredentialLabels is not { } labels)
                {
                    continue;
                }

                relabelled++;
                var declared = AuthenticationScheme.FieldsFor(connector.AuthType).Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var (name, label) in labels)
                {
                    Assert.True(declared.Contains(name), $"{connector.ConnectorType} labels '{name}', which its scheme ({connector.AuthType}) does not declare.");
                    Assert.False(string.IsNullOrWhiteSpace(label) || label.Contains('\n', StringComparison.Ordinal), $"{connector.ConnectorType}'s label for '{name}' is empty or not one line.");
                }
            }

            Assert.True(relabelled >= 30, $"only {relabelled} connectors name their credentials"); // a floor: the ones that do today
        }

        [Fact]
        public void The_catalogue_shows_the_label_beside_the_schemes_own_name()
        {
            var trello = ConnectorViews.From(InTreeConnectors.All().Single(connector => connector.ConnectorType == "trello"));
            Assert.Equal(new[] { ("access-key", "API key", false), ("secret-key", "Token", true) }, trello.AuthFields.Select(field => (field.Name, field.DisplayName, field.Secret)));
            var twilio = ConnectorViews.From(InTreeConnectors.All().Single(connector => connector.ConnectorType == "twilio"));
            Assert.Equal(new[] { ("username", "Account SID (AC...)"), ("password", "Auth token") }, twilio.AuthFields.Select(field => (field.Name, field.DisplayName)));
            var gmail = ConnectorViews.From(InTreeConnectors.All().Single(connector => connector.ConnectorType == "gmail")); // none given: the scheme's
            Assert.Equal(AuthenticationScheme.FieldsFor(AuthenticationType.OAuth2RefreshToken).Select(field => field.DisplayName), gmail.AuthFields.Select(field => field.DisplayName));
        }
    }
}
