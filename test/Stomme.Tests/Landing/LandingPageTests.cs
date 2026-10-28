#nullable enable
using System;
using System.Linq;
using Stomme.Landing;
using Stomme.Modules.Landing.Application;
using Stomme.Modules.Landing.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Landing
{
    /// <summary>BL-98 increment 1 - the landing page aggregate: one per site, made from the template, published, hidden, forgotten.</summary>
    public sealed class LandingPageTests
    {
        private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

        private static PredefinedLandingContent Content(params LandingSectionView[] sections) =>
            new("  Welcome to Acme  ", "  We make things.  ", sections);

        [Fact]
        public void A_site_has_one_landing_page_its_id_is_the_sites_with_its_tenant()
        {
            var tenant = TenantId.From(Guid.NewGuid());
            Assert.Equal(LandingPageIds.For(tenant, 3), LandingPageAggregate.Create(tenant, 3, Now).Id);
            Assert.NotEqual(LandingPageIds.For(tenant, 3), LandingPageIds.For(tenant, 4));
            Assert.NotEqual(LandingPageIds.For(tenant, 3), LandingPageIds.For(TenantId.From(Guid.NewGuid()), 3)); // a site id repeats across tenants
            Assert.Throws<ArgumentOutOfRangeException>(() => LandingPageAggregate.Create(tenant, 0, Now));
        }

        [Fact]
        public void A_new_page_has_no_source_and_cannot_be_published()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            Assert.Equal((LandingSource.None, LandingStatus.Draft, (PredefinedLandingContent?)null), (page.Source, page.Status, page.Predefined()));
            Assert.Throws<InvalidOperationException>(() => page.Publish(Now));
        }

        [Fact]
        public void The_template_content_is_trimmed_stored_and_read_back()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigurePredefined(Content(new LandingSectionView(" Start ", " Read this first. ", " Go ", " /start ")), Now.AddMinutes(1));

            var content = page.Predefined()!;
            Assert.Equal((LandingSource.Predefined, "Welcome to Acme", "We make things."), (page.Source, content.Headline, content.Introduction));
            Assert.Equal(new LandingSectionView("Start", "Read this first.", "Go", "/start"), Assert.Single(content.Sections));
            Assert.Equal(Now.AddMinutes(1), page.UpdatedUtc);
            Assert.Single(page.DomainEvents.OfType<LandingPageConfiguredEvent>());
        }

        [Fact]
        public void A_link_without_text_takes_the_section_title_and_a_section_without_a_link_has_no_link_text()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigurePredefined(Content(new LandingSectionView("Docs", "Read.", null, "https://example.com/docs"), new LandingSectionView("Plain", "Text.", "Orphan", null)), Now);

            var sections = page.Predefined()!.Sections;
            Assert.Equal("Docs", sections[0].LinkText);
            Assert.Equal((string?)null, sections[1].LinkText);
        }

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("data:text/html,<script>alert(1)</script>")]
        [InlineData("//evil.example/x")]
        [InlineData("/\\evil.example")]
        [InlineData("ftp://example.com/file")]
        [InlineData("about")]
        public void A_link_that_is_not_a_site_path_or_an_http_address_is_refused(string url)
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(Content(new LandingSectionView("Bad", "Link.", "Click", url)), Now));
            Assert.Equal(LandingSource.None, page.Source);
        }

        [Fact]
        public void Content_out_of_bounds_is_refused()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(new PredefinedLandingContent("   ", "Intro.", Array.Empty<LandingSectionView>()), Now));
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(new PredefinedLandingContent(new string('h', 151), "Intro.", Array.Empty<LandingSectionView>()), Now));
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(new PredefinedLandingContent("Hi", new string('i', 2001), Array.Empty<LandingSectionView>()), Now));
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(Content(Enumerable.Range(1, 7).Select(n => new LandingSectionView($"S{n}", "Text.", null, null)).ToArray()), Now));
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(Content(new LandingSectionView(" ", "No title.", null, null)), Now));
            Assert.Throws<ArgumentException>(() => page.ConfigurePredefined(Content(new LandingSectionView("Long", new string('t', 501), null, null)), Now));
        }

        [Fact]
        public void A_page_is_published_hidden_and_an_edit_to_a_published_page_stays_published()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigurePredefined(Content(), Now);
            page.Publish(Now.AddMinutes(2));
            Assert.Equal((LandingStatus.Published, (DateTime?)Now.AddMinutes(2)), (page.Status, page.PublishedUtc));
            Assert.Single(page.DomainEvents.OfType<LandingPagePublishedEvent>());

            page.ConfigurePredefined(new PredefinedLandingContent("Edited", "Live at once.", Array.Empty<LandingSectionView>()), Now.AddMinutes(3));
            Assert.Equal((LandingStatus.Published, "Edited"), (page.Status, page.Predefined()!.Headline));

            page.Unpublish(Now.AddMinutes(4));
            Assert.Equal((LandingStatus.Draft, "Edited"), (page.Status, page.Predefined()!.Headline));
        }

        [Fact]
        public void A_reset_forgets_the_page()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigurePredefined(Content(), Now);
            page.Publish(Now);

            page.Reset(Now.AddMinutes(5));

            Assert.Equal((LandingSource.None, LandingStatus.Draft, (DateTime?)null, (PredefinedLandingContent?)null), (page.Source, page.Status, page.PublishedUtc, page.Predefined()));
        }

        [Fact]
        public void The_template_a_manager_starts_from_is_valid_content()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigurePredefined(LandingTemplate.Default, Now);
            Assert.Equal(LandingTemplate.Default.Sections.Count, page.Predefined()!.Sections.Count);
        }

        // BL-98 increment 2 - the dashboard mode.

        [Fact]
        public void A_dashboard_page_names_its_dashboard_carries_no_content_and_publishes()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigurePredefined(Content(), Now);
            var dashboard = Guid.NewGuid();

            page.ConfigureDashboard(dashboard, Now.AddMinutes(1));

            Assert.Equal((LandingSource.Dashboard, (Guid?)dashboard, (PredefinedLandingContent?)null, Now.AddMinutes(1)), (page.Source, page.DashboardId, page.Predefined(), page.UpdatedUtc));
            Assert.Equal(2, page.DomainEvents.OfType<LandingPageConfiguredEvent>().Count());
            page.Publish(Now.AddMinutes(2));
            Assert.Equal(LandingStatus.Published, page.Status);
        }

        [Fact]
        public void A_dashboard_page_that_names_no_dashboard_is_refused()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            Assert.Throws<ArgumentException>(() => page.ConfigureDashboard(Guid.Empty, Now));
            Assert.Equal(LandingSource.None, page.Source);
        }

        [Fact]
        public void Remaking_a_dashboard_page_from_the_template_forgets_the_dashboard_and_a_reset_forgets_both()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, 3, Now);
            page.ConfigureDashboard(Guid.NewGuid(), Now);

            page.ConfigurePredefined(Content(), Now.AddMinutes(1));
            Assert.Equal((LandingSource.Predefined, (Guid?)null), (page.Source, page.DashboardId));

            page.ConfigureDashboard(Guid.NewGuid(), Now.AddMinutes(2));
            page.Reset(Now.AddMinutes(3));
            Assert.Equal((LandingSource.None, (Guid?)null), (page.Source, page.DashboardId));
        }
    }
}
