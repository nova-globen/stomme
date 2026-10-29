using Stomme.FeatureManagement.Application;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Pins the feature catalog's union/dedupe/lookup rules, as the cases
/// <c>Known_UnionsContributors_DedupesByKeyFirstWins_AndOrders</c> and
/// <c>Find_IsCaseInsensitive_TrimsAndMissesUnknown</c>): <see cref="IFeatureRegistry.Known"/> unions every
/// contributor ordered by key, the FIRST contributor to declare a key wins (a module cannot silently redefine
/// another's flag), and <see cref="IFeatureRegistry.Find"/> is case-insensitive, trims, and misses unknown and
/// empty keys.
/// </summary>
public sealed class FeatureRegistryDedupeTests
{
    [Fact]
    public void Known_unions_contributors_orders_by_key_and_the_first_contributor_wins_a_duplicate()
    {
        var registry = new FeatureRegistry(
        [
            new FeatureContributor([new FeatureDescriptor("z.flag", "Z"), new FeatureDescriptor("a.flag", "A")]),
            new FeatureContributor([new FeatureDescriptor("a.flag", "duplicate - ignored"), new FeatureDescriptor("m.flag", "M")]),
        ]);

        Assert.Equal(["a.flag", "m.flag", "z.flag"], registry.Known.Select(descriptor => descriptor.Key));
        Assert.Equal("A", registry.Find("a.flag")!.Description);
        Assert.Equal("A", Assert.Single(registry.Known, descriptor => descriptor.Key == "a.flag").Description);
    }

    [Fact]
    public void A_duplicate_differing_only_in_case_is_the_same_key_and_the_first_still_wins()
    {
        var registry = new FeatureRegistry(
        [
            new FeatureContributor([new FeatureDescriptor("Reports.Export", "first")]),
            new FeatureContributor([new FeatureDescriptor("reports.export", "second")]),
        ]);

        var descriptor = Assert.Single(registry.Known);
        Assert.Equal("first", descriptor.Description);
    }

    [Fact]
    public void Find_is_case_insensitive_trims_and_misses_unknown_and_empty()
    {
        var registry = new FeatureRegistry([new FeatureContributor([new FeatureDescriptor("Reports.Export", "x")])]);

        Assert.NotNull(registry.Find("reports.export"));
        Assert.NotNull(registry.Find("  reports.export  "));
        Assert.Null(registry.Find("unknown"));
        Assert.Null(registry.Find(""));
        Assert.Null(registry.Find("   "));
    }
}