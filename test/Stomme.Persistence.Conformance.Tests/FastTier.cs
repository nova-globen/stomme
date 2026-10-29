// Every test in this project reaches PostgreSQL or RavenDB, so the whole assembly leaves the fast tier
// (--filter "Category!=ExternalDatabase"). The full, unfiltered gate is the commit rule. See FastTierTraitTests.
[assembly: Xunit.AssemblyTrait("Category", "ExternalDatabase")]
