using System;
using System.Collections.Generic;
using Stomme.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests.Licensing
{
    /// <summary>D105 "The gate": the pure <c>Deployment:Mode</c> reader. (Step 3 adds the composition tests beside it.)</summary>
    public sealed class DeploymentModeReadTests
    {
        private static IConfiguration Config(string? mode)
        {
            var values = new Dictionary<string, string?>();
            if (mode is not null)
            {
                values[DeploymentMode.ConfigKey] = mode;
            }

            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Hosted")]
        [InlineData("hosted")]
        [InlineData("HOSTED")]
        public void Absent_empty_and_Hosted_read_as_Hosted(string? mode)
        {
            Assert.Equal(DeploymentModeKind.Hosted, DeploymentMode.Read(Config(mode)));
            Assert.False(DeploymentMode.IsOnPrem(Config(mode)));
        }

        [Theory]
        [InlineData("OnPrem")]
        [InlineData("onprem")]
        [InlineData("ONPREM")]
        [InlineData(" OnPrem ")]
        public void OnPrem_reads_as_OnPrem_case_insensitively(string mode)
        {
            Assert.Equal(DeploymentModeKind.OnPrem, DeploymentMode.Read(Config(mode)));
            Assert.True(DeploymentMode.IsOnPrem(Config(mode)));
        }

        [Theory]
        [InlineData("OnPremise")]
        [InlineData("On-Prem")]
        [InlineData("1")]
        public void Any_other_value_throws_naming_the_key_the_value_and_the_valid_values(string mode)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DeploymentMode.Read(Config(mode)));

            Assert.Contains("Deployment:Mode", ex.Message);
            Assert.Contains(mode, ex.Message);
            Assert.Contains("Hosted", ex.Message);
            Assert.Contains("OnPrem", ex.Message);
        }

        [Fact]
        public void The_key_is_Deployment_colon_Mode()
        {
            Assert.Equal("Deployment:Mode", DeploymentMode.ConfigKey);
        }
    }
}