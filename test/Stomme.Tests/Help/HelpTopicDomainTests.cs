#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Stomme.Help;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// The help topic aggregate. Every case answers a measured refutation.
    /// </summary>
    public class HelpTopicDomainTests
    {
        private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        private const string Contributor = "stomme";

        [Fact]
        public void A_seeded_topic_is_published_owned_and_keyed_by_its_deterministic_id()
        {
            var topic = Seeded();

            Assert.Equal(HelpTopicIds.For(Tenant.Value, 4, "page:admin/users", "en"), topic.Id);
            Assert.Equal(4, topic.SiteId);
            Assert.Equal(Tenant, topic.TenantId);
            Assert.Equal("page:admin/users", topic.Anchor);
            Assert.Equal("en", topic.Locale);
            Assert.True(topic.IsPublished);          // a topic nobody can read is not help
            Assert.True(topic.IsSeeded);
            Assert.Equal(Contributor, topic.SeedContributorId);
            Assert.Equal(1, topic.ContentVersion);
            Assert.Equal(DateTimeKind.Utc, topic.CreatedOnUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, topic.UpdatedOnUtc.Kind);
        }

        [Fact]
        public void The_same_site_anchor_and_locale_in_TWO_TENANTS_are_two_different_rows()
        {
            // BL-151's class, caught before it shipped: every tenant database numbers its sites from 1 and the module
            // store is ONE store for the install, so a key without the tenant gives two tenants' topics one primary key.
            var other = TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
            var contribution = new HelpTopicContribution("page:admin/users", "en", "Users", "Manage users.");

            var mine = HelpTopicAggregate.Seed(Tenant, 1, contribution, Contributor, Now);
            var theirs = HelpTopicAggregate.Seed(other, 1, contribution, Contributor, Now);
            var authored = HelpTopicAggregate.Author(other, 1, "page:admin/users", "en", "Users", "Manage users.", Now);

            Assert.NotEqual(mine.Id, theirs.Id);
            Assert.Equal(theirs.Id, authored.Id);      // both factories key the same way
        }

        [Fact]
        public void A_hand_authored_topic_is_a_DRAFT_and_belongs_to_nobody()
        {
            var topic = HelpTopicAggregate.Author(Tenant, 4, "page:admin/pages", "en", "Pages", "Manage pages.", Now);

            Assert.False(topic.IsPublished);         // an operator publishes it deliberately
            Assert.False(topic.IsSeeded);
            Assert.Equal(string.Empty, topic.SeedContributorId);
        }

        [Fact]
        public void A_hand_authored_topic_is_held_to_the_SAME_bounds_as_a_contribution()
        {
            Assert.Throws<ArgumentException>(() => HelpTopicAggregate.Author(Tenant, 4, "has space", "en", "T", "B", Now));
            Assert.Throws<ArgumentException>(() => HelpTopicAggregate.Author(Tenant, 4, "page:x", "en", "", "B", Now));
            Assert.Throws<ArgumentException>(() =>
                HelpTopicAggregate.Author(Tenant, 4, "page:x", "en", "T", new string('x', HelpTopicContribution.MaximumMarkdownCharacters + 1), Now));
        }

        // ---- D103 refutation 7: an UNCHANGED seed writes NOTHING -------------------------------------------------

        [Fact]
        public void An_unchanged_seed_update_changes_nothing_and_says_so()
        {
            var topic = Seeded();
            var version = topic.ContentVersion;
            var updated = topic.UpdatedOnUtc;

            Assert.False(topic.TryApplySeedUpdate(Contribution("Users", "Manage users."), Contributor, Now.AddDays(1)));

            // the predecessor platform bumped BOTH on every call, so every boot rewrote every seeded row - and ContentVersion is part
            // of the cache key, so every boot also invalidated every topic's cache.
            Assert.Equal(version, topic.ContentVersion);
            Assert.Equal(updated, topic.UpdatedOnUtc);
        }

        [Fact]
        public void A_CHANGED_seed_update_applies_and_bumps_the_version()
        {
            var topic = Seeded();

            Assert.True(topic.TryApplySeedUpdate(Contribution("Users", "Manage users **here**."), Contributor, Now.AddDays(1)));
            Assert.Equal("Manage users **here**.", topic.Markdown);
            Assert.Equal(2, topic.ContentVersion);
            Assert.Equal(Now.AddDays(1), topic.UpdatedOnUtc);

            // A title-only change is a change too.
            Assert.True(topic.TryApplySeedUpdate(Contribution("Site users", "Manage users **here**."), Contributor, Now.AddDays(2)));
            Assert.Equal("Site users", topic.Title);
            Assert.Equal(3, topic.ContentVersion);
        }

        // ---- D103 refutation 6: a seed update is the OWNER'S alone -----------------------------------------------

        [Fact]
        public void ANOTHER_contributor_cannot_update_a_seeded_topic()
        {
            var topic = Seeded();

            // the predecessor platform's TryApplySeedUpdate never read SeedContributorId, so any contributor could rewrite any other's
            // topic - including a third-party package rewriting a framework one.
            Assert.False(topic.TryApplySeedUpdate(Contribution("Hijacked", "Somebody else's text."), "someone-else", Now.AddDays(1)));
            Assert.Equal("Users", topic.Title);
            Assert.Equal(Contributor, topic.SeedContributorId);
        }

        [Fact]
        public void An_OPERATOR_EDIT_ends_the_contributors_claim_for_good()
        {
            var topic = Seeded();
            topic.UpdateContent("Our users", "What *we* say about users.", Now.AddDays(1));

            Assert.False(topic.IsSeeded);
            Assert.Equal(string.Empty, topic.SeedContributorId);
            Assert.Equal(2, topic.ContentVersion);

            // ...and the original contributor cannot take it back on the next boot.
            Assert.False(topic.TryApplySeedUpdate(Contribution("Users", "Manage users."), Contributor, Now.AddDays(2)));
            Assert.Equal("Our users", topic.Title);
        }

        [Fact]
        public void RESET_TO_SEED_is_the_only_way_an_ownership_change_happens()
        {
            var topic = Seeded();
            topic.UpdateContent("Our users", "Ours.", Now.AddDays(1));

            // The operator's explicit help:manage action - not something a boot does.
            topic.ResetToSeed(Contribution("Users", "Manage users."), Contributor, Now.AddDays(2));

            Assert.True(topic.IsSeeded);
            Assert.Equal(Contributor, topic.SeedContributorId);
            Assert.Equal("Users", topic.Title);
            Assert.True(topic.TryApplySeedUpdate(Contribution("Users", "Newer text."), Contributor, Now.AddDays(3)));
        }

        // ---- publishing -----------------------------------------------------------------------------------------

        [Fact]
        public void Publishing_something_already_published_writes_nothing()
        {
            var topic = Seeded();
            var updated = topic.UpdatedOnUtc;

            topic.Publish(Now.AddDays(1));
            Assert.Equal(updated, topic.UpdatedOnUtc);       // the same no-op rule as an unchanged seed

            topic.Unpublish(Now.AddDays(1));
            Assert.False(topic.IsPublished);
            Assert.Equal(Now.AddDays(1), topic.UpdatedOnUtc);
        }

        // ---- the UTC rule and the storage-blind shape ------------------------------------------------------------

        [Fact]
        public void A_non_UTC_instant_is_REFUSED_rather_than_silently_converted()
        {
            var local = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Local);

            Assert.Throws<ArgumentException>(() => HelpTopicAggregate.Seed(Tenant, 4, Contribution("T", "B"), Contributor, local));
            Assert.Throws<ArgumentException>(() => Seeded().TryApplySeedUpdate(Contribution("T", "Changed."), Contributor, local));
            Assert.Throws<ArgumentException>(() => Seeded().UpdateContent("T", "Changed.", local));
        }

        [Fact]
        public void Every_property_is_a_SCALAR_because_the_shared_context_maps_nothing_else()
        {
            // D103 refutation 11: the predecessor platform's three "value objects" are plain records, so they fall through to EF's
            // conventions inside an initializer whose error is SWALLOWED - a missed reshape would read as EMPTY
            // rather than fail. Zero value objects and zero collections is the shape that cannot go wrong quietly.
            var scalar = new[] { typeof(string), typeof(int), typeof(bool), typeof(Guid), typeof(DateTime), typeof(DateTime?), typeof(TenantId) };

            // DECLARED only: Version (the concurrency token) and DomainEvents come from AggregateRoot<>, which every
            // module aggregate carries and the shared context already handles. The claim is about THIS shape.
            var offenders = typeof(HelpTopicAggregate)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => !scalar.Contains(property.PropertyType))
                .Select(property => property.Name + " : " + property.PropertyType.Name)
                .ToList();

            Assert.True(offenders.Count == 0, "the aggregate carries a property the shared context cannot map:\n" + string.Join("\n", offenders));

            // Non-vacuity: it really does have properties.
            Assert.True(typeof(HelpTopicAggregate).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length >= 10);
        }

        [Fact]
        public void Every_computed_member_is_a_METHOD_so_a_document_store_does_not_write_it()
        {
            // Both document resolvers serialise read-only properties, so a computed PROPERTY would be written into
            // the document and then read back as real state.
            var computed = typeof(HelpTopicAggregate)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.CanRead && !property.CanWrite && property.GetSetMethod(nonPublic: true) is null)
                .Select(property => property.Name)
                .ToList();

            Assert.True(computed.Count == 0, "a computed property would be persisted: " + string.Join(", ", computed));
        }

        [Fact]
        public void It_is_a_tenant_owned_aggregate_that_also_names_its_SITE()
        {
            Assert.True(typeof(Stomme.Persistence.Abstractions.ITenantOwned).IsAssignableFrom(typeof(HelpTopicAggregate)));
            Assert.True(typeof(AggregateRoot<Guid>).IsAssignableFrom(typeof(HelpTopicAggregate)));
            Assert.NotNull(typeof(HelpTopicAggregate).GetProperty(nameof(HelpTopicAggregate.SiteId)));
        }

        [Fact]
        public void An_edit_bumps_the_version_and_UNPUBLISHING_returns_a_topic_to_a_draft()
        {
            // the predecessor platform's UpdateContent_IncrementsVersion and Unpublish_RevertsToDraft, reconciled by name: the version is
            // the cache key's moving part, so an edit that did not bump it would serve the old text.
            var topic = Seeded();
            Assert.Equal(1, topic.ContentVersion);

            topic.UpdateContent("Users", "Edited.", Now.AddMinutes(1));
            Assert.Equal(2, topic.ContentVersion);
            Assert.Equal(Now.AddMinutes(1), topic.UpdatedOnUtc);

            Assert.True(topic.IsPublished);
            topic.Unpublish(Now.AddMinutes(2));
            Assert.False(topic.IsPublished);
            Assert.Equal(Now.AddMinutes(2), topic.UpdatedOnUtc);
            Assert.Equal(2, topic.ContentVersion);          // visibility is not text: the version stays
        }

        private static HelpTopicAggregate Seeded() =>
            HelpTopicAggregate.Seed(Tenant, 4, Contribution("Users", "Manage users."), Contributor, Now);

        private static HelpTopicContribution Contribution(string title, string markdown) =>
            new("page:admin/users", "en", title, markdown);
    }
}
