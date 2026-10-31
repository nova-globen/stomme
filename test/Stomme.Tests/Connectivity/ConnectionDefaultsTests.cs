#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Application;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// A connection created over the API gets its connector's field defaults, as the browser form fills them in (BL-251,
    /// BL-239's H#9: an API-created connection had settings <c>{}</c> and behaved unlike the same one made in the form).
    /// </summary>
    public sealed class ConnectionDefaultsTests
    {
        private static readonly IConnector Connector = new DefaultsConnector(new ConnectorConfigSchema(new[]
        {
            new ConnectorConfigField("region", "Region") { Default = "eu" },
            new ConnectorConfigField("mode", "Mode") { Default = "basic" },
            new ConnectorConfigField("tenant", "Tenant") { Default = "common", VisibleWhen = new ConnectorConfigFieldVisibility("mode", "Directory") },
            new ConnectorConfigField("label", "Label"),
        }));

        [Fact]
        public void A_missing_setting_takes_its_default_a_given_one_is_kept_and_a_field_without_a_default_stays_absent()
        {
            var settings = ConnectionAuthoring.WithDefaults(Connector, new Dictionary<string, string> { ["region"] = "us" });

            Assert.Equal("us", settings["region"]);
            Assert.Equal("basic", settings["mode"]);
            Assert.False(settings.ContainsKey("label"));
            Assert.False(settings.ContainsKey("tenant")); // shown only for mode = Directory
        }

        [Fact]
        public void A_field_shown_for_another_fields_value_takes_its_default_when_that_value_is_set_whatever_its_case()
        {
            var settings = ConnectionAuthoring.WithDefaults(Connector, new Dictionary<string, string> { ["mode"] = "directory" });

            Assert.Equal("common", settings["tenant"]);
        }

        [Fact]
        public void No_settings_at_all_get_every_unconditional_default()
        {
            var settings = ConnectionAuthoring.WithDefaults(Connector, null);

            Assert.Equal(new Dictionary<string, string> { ["region"] = "eu", ["mode"] = "basic" }, settings);
        }

        private sealed class DefaultsConnector(ConnectorConfigSchema schema) : IConnector
        {
            public string ConnectorType => "defaults";

            public string DisplayName => "Defaults";

            public ConnectorConfigSchema ConfigSchema => schema;

            public Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
