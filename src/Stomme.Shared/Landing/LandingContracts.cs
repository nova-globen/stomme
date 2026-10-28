#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stomme.Landing
{
    // ============================================================================================================
    // The Landing module's Shared contracts (BL-98, the predecessor platform's feature 44 - planned there, never started): a site's landing
    // page on its home page, made one of three ways. BCL-only like every Shared contract.
    // ============================================================================================================

    /// <summary>How a site's landing page was made. <see cref="None"/> is a site that has not made one.</summary>
    public enum LandingSource
    {
        None = 0,

        /// <summary>From the predefined template: a headline, an introduction and up to six sections.</summary>
        Predefined = 1,

        /// <summary>One of the site's dashboards, shown in place (BL-98 increment 2).</summary>
        Dashboard = 2,

        /// <summary>
        /// Static HTML the AI assistant's Landing Page Designer generated from a brief (BL-98 increment 3), sanitised on the
        /// server and shown in a sandboxed frame.
        /// </summary>
        GeneratedHtml = 3,
    }

    /// <summary>A landing page is shown to the site's visitors only once published.</summary>
    public enum LandingStatus
    {
        Draft = 1,
        Published = 2,
    }

    /// <summary>
    /// The module's keys. Viewing is every visitor's - the landing page sits on the site's home page, which is public by default;
    /// the page's own permissions still decide who reaches it. Making, publishing and resetting it is the administrators'.
    /// </summary>
    public static class LandingPermissions
    {
        public const string Resource = "landing";

        public const string View = "landing:view";

        public const string Manage = "landing:manage";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "All Users" }),
            (Manage, new[] { "Administrators" }),
        };
    }

    /// <summary>One section of a predefined landing page: a title, some text, and an optional link.</summary>
    public sealed record LandingSectionView(string Title, string Text, string? LinkText, string? LinkUrl);

    /// <summary>A predefined landing page's content.</summary>
    public sealed record PredefinedLandingContent(string Headline, string Introduction, IReadOnlyList<LandingSectionView> Sections);

    /// <summary>A site's landing page as the page and the API see it.</summary>
    public sealed record LandingPageView(
        Guid Id,
        LandingSource Source,
        LandingStatus Status,
        PredefinedLandingContent? Predefined,
        Guid? DashboardId,
        DateTime UpdatedUtc,
        DateTime? PublishedUtc,
        string? GeneratedHtml = null,
        IReadOnlyList<string>? Sections = null);

    /// <summary>What an administrator sends to have a page generated from a brief (BL-98 increment 3).</summary>
    public sealed class GenerateLandingRequest
    {
        public string Brief { get; set; } = string.Empty;
    }

    /// <summary>What an administrator sends to have ONE section of the generated page revised (BL-98 increment 4).</summary>
    public sealed class ProposeLandingSectionRequest
    {
        public string Section { get; set; } = string.Empty;

        public string Instruction { get; set; } = string.Empty;
    }

    /// <summary>What an administrator sends to make the page from generated HTML they accepted; sanitised again on the server.</summary>
    public sealed class ConfigureGeneratedLandingRequest
    {
        public string Html { get; set; } = string.Empty;
    }

    /// <summary>
    /// A generated page PROPOSED, nothing stored (D113): the sanitised HTML and its sections, or why the designer could not -
    /// what the sanitizer still had to take out after its last attempt.
    /// </summary>
    public sealed record LandingDraftView(bool Succeeded, string Html, IReadOnlyList<string> Sections, IReadOnlyList<string> Problems);

    /// <summary>
    /// The frame document a generated page is shown in (BL-98 increment 3), on the home page and in the editor's preview: its own
    /// content policy - no script, no connection, styles only inline, images and fonts over http(s) - so even a page the
    /// sanitizer let through cannot run or call home. The frame itself carries no allow-scripts and no allow-same-origin.
    /// </summary>
    public static class LandingFrame
    {
        public static string Srcdoc(string sanitizedHtml) =>
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src https: http:; font-src https:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<style>body{margin:0;font-family:system-ui,-apple-system,'Segoe UI',sans-serif;line-height:1.5}img{max-width:100%;height:auto}</style>" +
            "</head><body>" + (sanitizedHtml ?? string.Empty) + "</body></html>";
    }

    /// <summary>What an administrator sends to make the landing page from the predefined template.</summary>
    public sealed class ConfigurePredefinedLandingRequest
    {
        public string Headline { get; set; } = string.Empty;

        public string Introduction { get; set; } = string.Empty;

        public List<LandingSectionView> Sections { get; set; } = new();
    }

    /// <summary>What an administrator sends to show one of the site's PUBLISHED dashboards as the landing page.</summary>
    public sealed class ConfigureDashboardLandingRequest
    {
        public Guid DashboardId { get; set; }
    }

    /// <summary>
    /// The module's service contract - over HTTP in a browser, the requests directly on the server. <see cref="GetAsync"/>
    /// answers the PUBLISHED landing page to anyone who may view it and a draft only to an administrator; a site with none, or
    /// a draft a visitor may not see, answers null - and so does a dashboard landing page to a viewer who may not read
    /// dashboards (<c>dashboards:view</c>), since the board would show that viewer nothing.
    /// </summary>
    public interface ILandingService
    {
        Task<LandingPageView?> GetAsync();

        Task<PredefinedLandingContent> GetTemplateAsync();

        Task<LandingPageView> ConfigurePredefinedAsync(ConfigurePredefinedLandingRequest request);

        Task<LandingPageView> ConfigureDashboardAsync(ConfigureDashboardLandingRequest request);

        Task<LandingPageView> PublishAsync();

        Task<LandingPageView> UnpublishAsync();

        Task ResetAsync();

        /// <summary>A page generated from a brief - PROPOSED, nothing stored; a refusal (the assistant off, its key missing) is thrown with its reason.</summary>
        Task<LandingDraftView> GenerateAsync(GenerateLandingRequest request);

        /// <summary>The generated page with one section revised - PROPOSED, nothing stored.</summary>
        Task<LandingDraftView> ProposeSectionAsync(ProposeLandingSectionRequest request);

        /// <summary>Makes the page from generated HTML the administrator accepted; the server sanitises it again.</summary>
        Task<LandingPageView> ConfigureGeneratedAsync(ConfigureGeneratedLandingRequest request);
    }
}
