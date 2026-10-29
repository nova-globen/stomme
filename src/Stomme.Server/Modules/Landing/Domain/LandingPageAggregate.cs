#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stomme.Landing;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Landing.Domain
{
    public sealed record LandingPageConfiguredEvent(Guid LandingPageId, TenantId TenantId, int SiteId, LandingSource Source) : DomainEvent;

    public sealed record LandingPagePublishedEvent(Guid LandingPageId, TenantId TenantId, int SiteId, LandingSource Source) : DomainEvent;

    /// <summary>
    /// ONE landing page per site, made structural: the id is the site's, with its tenant - the module store is one per
    /// installation and a site id repeats across tenants (the lesson of the Datasets demo ids, BL-94 increment 14).
    /// </summary>
    public static class LandingPageIds
    {
        public static Guid For(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', "landing.page", tenantId.Value.ToString("N", CultureInfo.InvariantCulture), siteId.ToString(CultureInfo.InvariantCulture))));
            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            return new Guid(bytes);
        }
    }

    /// <summary>
    /// A site's landing page (BL-98; the predecessor platform's <c>LandingPage</c>, planned there and never built): how it was made, its
    /// content, and whether visitors see it. Tenant-owned and one per SITE of that tenant (the predecessor platform's "one per tenant" - a
    /// Stomme site is the unit with its own home page).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An edit to a published page is live</b>, as an edit to a page's text is: the status tracks whether visitors see it
    /// at all, not whether the latest edit was approved. <see cref="Unpublish"/> hides it; <see cref="Reset"/> forgets it.
    /// </para>
    /// <para>
    /// <b>The predefined content is structured, not HTML</b>: text and links rendered by the page's component, so there is
    /// nothing to sanitise. A link is a site-relative path or an http(s) address - a <c>javascript:</c> or <c>data:</c> link
    /// on a public page is refused here, not escaped later.
    /// </para>
    /// </remarks>
    public sealed class LandingPageAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxHeadlineLength = 150;
        public const int MaxIntroductionLength = 2000;
        public const int MaxSections = 6;
        public const int MaxSectionTitleLength = 80;
        public const int MaxSectionTextLength = 500;
        public const int MaxLinkTextLength = 60;
        public const int MaxLinkUrlLength = 500;
        public const int MaxGeneratedHtmlLength = 100_000;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // Materialisation constructor - every store builds the aggregate through it. Not for application use.
        private LandingPageAggregate()
        {
            TenantId = null!;
            ContentJson = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public LandingSource Source { get; private set; }

        public LandingStatus Status { get; private set; }

        /// <summary>The predefined content, serialised - one scalar column on every store.</summary>
        public string ContentJson { get; private set; }

        public Guid? DashboardId { get; private set; }

        /// <summary>
        /// A generated page's HTML (BL-98 increment 3) - stored only as the landing sanitizer left it; the request sanitises
        /// before it reaches here, and the page is shown in a sandboxed frame.
        /// </summary>
        public string? GeneratedHtml { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public DateTime? PublishedUtc { get; private set; }

        /// <summary>A site's landing page before anything was made: no source, a draft.</summary>
        public static LandingPageAggregate Create(TenantId tenantId, int siteId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A landing page belongs to a site.");
            }

            return new LandingPageAggregate
            {
                Id = LandingPageIds.For(tenantId, siteId),
                TenantId = tenantId,
                SiteId = siteId,
                Source = LandingSource.None,
                Status = LandingStatus.Draft,
                UpdatedUtc = nowUtc,
            };
        }

        /// <summary>Makes (or remakes) the page from the predefined template with this content.</summary>
        public void ConfigurePredefined(PredefinedLandingContent content, DateTime nowUtc)
        {
            ContentJson = JsonSerializer.Serialize(Validate(content), Json);
            Source = LandingSource.Predefined;
            DashboardId = null;
            GeneratedHtml = null;
            UpdatedUtc = nowUtc;
            RaiseDomainEvent(new LandingPageConfiguredEvent(Id, TenantId, SiteId, Source));
        }

        /// <summary>
        /// Makes (or remakes) the page as one of the site's dashboards, shown in place. Which dashboards qualify - the site's
        /// own, published - is the request's to check; the domain refuses only a page that names none.
        /// </summary>
        public void ConfigureDashboard(Guid dashboardId, DateTime nowUtc)
        {
            if (dashboardId == Guid.Empty)
            {
                throw new ArgumentException("A dashboard landing page needs a dashboard.", nameof(dashboardId));
            }

            ContentJson = string.Empty;
            Source = LandingSource.Dashboard;
            DashboardId = dashboardId;
            GeneratedHtml = null;
            UpdatedUtc = nowUtc;
            RaiseDomainEvent(new LandingPageConfiguredEvent(Id, TenantId, SiteId, Source));
        }

        /// <summary>
        /// Makes (or remakes) the page from generated HTML the administrator accepted (BL-98 increment 3). The HTML must be the
        /// landing sanitizer's output - the request's to ensure; the domain refuses only an empty or oversized page.
        /// </summary>
        public void ConfigureGenerated(string sanitizedHtml, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(sanitizedHtml))
            {
                throw new ArgumentException("A generated landing page has content.", nameof(sanitizedHtml));
            }

            if (sanitizedHtml.Length > MaxGeneratedHtmlLength)
            {
                throw new ArgumentException($"A generated landing page is at most {MaxGeneratedHtmlLength} characters.", nameof(sanitizedHtml));
            }

            ContentJson = string.Empty;
            Source = LandingSource.GeneratedHtml;
            DashboardId = null;
            GeneratedHtml = sanitizedHtml;
            UpdatedUtc = nowUtc;
            RaiseDomainEvent(new LandingPageConfiguredEvent(Id, TenantId, SiteId, Source));
        }

        /// <summary>Shows the page to the site's visitors. A page that was never made has nothing to show.</summary>
        public void Publish(DateTime nowUtc)
        {
            if (Source == LandingSource.None)
            {
                throw new InvalidOperationException("There is no landing page to publish; make one first.");
            }

            Status = LandingStatus.Published;
            PublishedUtc = nowUtc;
            UpdatedUtc = nowUtc;
            RaiseDomainEvent(new LandingPagePublishedEvent(Id, TenantId, SiteId, Source));
        }

        /// <summary>Hides the page from visitors; its content stays, for the administrators.</summary>
        public void Unpublish(DateTime nowUtc)
        {
            Status = LandingStatus.Draft;
            UpdatedUtc = nowUtc;
        }

        /// <summary>Forgets the page: the site is back to having none, and administrators see the chooser again.</summary>
        public void Reset(DateTime nowUtc)
        {
            Source = LandingSource.None;
            Status = LandingStatus.Draft;
            ContentJson = string.Empty;
            DashboardId = null;
            GeneratedHtml = null;
            PublishedUtc = null;
            UpdatedUtc = nowUtc;
        }

        /// <summary>The predefined content, when the page was made from the template.</summary>
        public PredefinedLandingContent? Predefined() =>
            Source == LandingSource.Predefined && ContentJson.Length > 0 ? JsonSerializer.Deserialize<PredefinedLandingContent>(ContentJson, Json) : null;

        /// <summary>Trims and bounds the content, and refuses a link that is not a site path or an http(s) address.</summary>
        public static PredefinedLandingContent Validate(PredefinedLandingContent content)
        {
            ArgumentNullException.ThrowIfNull(content);
            var headline = Bounded(content.Headline, 1, MaxHeadlineLength, "headline");
            var introduction = Bounded(content.Introduction, 0, MaxIntroductionLength, "introduction");
            var sections = (content.Sections ?? Array.Empty<LandingSectionView>()).ToList();
            if (sections.Count > MaxSections)
            {
                throw new ArgumentException($"A landing page has at most {MaxSections} sections.", nameof(content));
            }

            return new PredefinedLandingContent(headline, introduction, sections.Select(Section).ToList());
        }

        private static LandingSectionView Section(LandingSectionView section)
        {
            ArgumentNullException.ThrowIfNull(section);
            var title = Bounded(section.Title, 1, MaxSectionTitleLength, "section title");
            var text = Bounded(section.Text, 0, MaxSectionTextLength, "section text");
            var url = string.IsNullOrWhiteSpace(section.LinkUrl) ? null : SafeLink(section.LinkUrl.Trim());
            var linkText = url is null ? null : Bounded(string.IsNullOrWhiteSpace(section.LinkText) ? title : section.LinkText, 1, MaxLinkTextLength, "link text");
            return new LandingSectionView(title, text, linkText, url);
        }

        private static string SafeLink(string url)
        {
            if (url.Length > MaxLinkUrlLength)
            {
                throw new ArgumentException($"A link is at most {MaxLinkUrlLength} characters.");
            }

            var sitePath = url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\');
            var web = Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp);
            return sitePath || web ? url : throw new ArgumentException($"'{url}' is not a link a landing page can carry: use a site path such as /about, or an http(s) address.");
        }

        private static string Bounded(string? value, int min, int max, string what)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length < min || trimmed.Length > max)
            {
                throw new ArgumentException(min > 0 ? $"A landing page's {what} is {min} to {max} characters." : $"A landing page's {what} is at most {max} characters.");
            }

            return trimmed;
        }
    }
}
