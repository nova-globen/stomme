using System;
using System.IO;
using System.Linq;
using Stomme.CloudUsage;
using Xunit;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// BL-22 (2026-10-22, the module-page walk): the Cloud Usage budget page listed its providers by their member names -
    /// "Aws", "Gcp", "Microsoft365", "GenericRest". Every provider now has a name for a page, and the page shows no
    /// provider any other way.
    /// </summary>
    public class CloudProviderNamesTests
    {
        [Theory]
        [InlineData(CloudProvider.Aws, "AWS")]
        [InlineData(CloudProvider.Gcp, "Google Cloud")]
        [InlineData(CloudProvider.Microsoft365, "Microsoft 365")]
        [InlineData(CloudProvider.GenericRest, "Generic REST")]
        [InlineData(CloudProvider.Azure, "Azure")]
        [InlineData(CloudProvider.GitHub, "GitHub")]
        public void A_provider_has_a_name_for_a_page(CloudProvider provider, string expected)
        {
            Assert.Equal(expected, CloudProviderNames.Display(provider));
            Assert.Equal(expected, CloudProviderNames.Display(provider.ToString()));
        }

        [Fact]
        public void Every_provider_has_a_name_and_a_member_that_is_not_a_name_is_never_shown_as_it_stands()
        {
            // The members that are identifiers rather than names; a new one joins this list with its display name.
            var identifiers = new[] { CloudProvider.Aws, CloudProvider.Gcp, CloudProvider.Microsoft365, CloudProvider.GenericRest };
            foreach (var provider in Enum.GetValues<CloudProvider>())
            {
                var shown = CloudProviderNames.Display(provider);
                Assert.False(string.IsNullOrWhiteSpace(shown), $"{provider} has no name");
                if (identifiers.Contains(provider))
                {
                    Assert.NotEqual(provider.ToString(), shown);
                }
            }
        }

        [Fact]
        public void An_unknown_value_is_shown_as_it_is()
        {
            Assert.Equal("Oracle", CloudProviderNames.Display("Oracle"));
            Assert.Equal(string.Empty, CloudProviderNames.Display((string?)null));
        }

        [Fact]
        public void The_cloud_usage_page_shows_providers_only_through_their_names()
        {
            var page = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Stomme.Client", "Modules", "CloudUsage", "Index.razor"));

            Assert.DoesNotContain(">@provider<", page);
            Assert.DoesNotContain("Provider?.ToString() ??", page);
            Assert.DoesNotContain("<td>@line.Provider</td>", page);
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
        }
    }
}
