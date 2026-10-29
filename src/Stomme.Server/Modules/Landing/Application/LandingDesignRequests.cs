#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Stomme.Landing;
using Stomme.Landing.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Landing.Domain;
using Stomme.Modules.Landing.Infrastructure;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.Landing.Application
{
    // ============================================================================================================
    // The AI mode and the edit mode : an
    // administrator's brief becomes a proposed page, a section and an instruction a proposed revision - PROPOSED, never stored
    // (an agent proposes, its user applies) - and applying stores the HTML only after the landing sanitizer, run again
    // here whatever the page sent. Both need landing:manage AND the assistant's own key: the model is the site's, metered and
    // quota-gated, and assistant:use is who may spend it.
    // ============================================================================================================

    /// <summary>The designer the AI modes use - the AI assistant's Landing Page Designer, or none when it is not composed.</summary>
    public interface ILandingPageDesigner
    {
        Task<LandingDesignResult> GenerateAsync(string brief, CancellationToken cancellationToken = default);

        Task<LandingDesignResult> EditSectionAsync(string sectionHtml, string section, string instruction, CancellationToken cancellationToken = default);
    }

    public sealed record LandingDesignResult(bool Succeeded, string? Html, IReadOnlyList<string> Sections, IReadOnlyList<string> Problems)
    {
        public static LandingDesignResult Ok(string html, IReadOnlyList<string> sections) => new(true, html, sections, Array.Empty<string>());

        public static LandingDesignResult Failed(IReadOnlyList<string> problems) => new(false, null, Array.Empty<string>(), problems);
    }

    /// <summary>The designer when no AI is composed into the host: the AI modes say so rather than fail.</summary>
    public sealed class NoLandingPageDesigner : ILandingPageDesigner
    {
        public Task<LandingDesignResult> GenerateAsync(string brief, CancellationToken cancellationToken = default) => throw Unavailable();

        public Task<LandingDesignResult> EditSectionAsync(string sectionHtml, string section, string instruction, CancellationToken cancellationToken = default) => throw Unavailable();

        private static InvalidOperationException Unavailable() => new("No AI designer is composed into this installation.");
    }

    /// <summary>A page generated from the administrator's brief - proposed, nothing stored.</summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record GenerateLandingDraftCommand(string Brief) : ICommand<LandingDraftView>;

    public sealed class GenerateLandingDraftCommandHandler(ILandingPageDesigner designer, IAccessGuard accessGuard) : ICommandHandler<GenerateLandingDraftCommand, LandingDraftView>
    {
        public const int MaxBriefLength = 4000;

        public async Task<LandingDraftView> Handle(GenerateLandingDraftCommand request, CancellationToken ct)
        {
            var brief = (request.Brief ?? string.Empty).Trim();
            if (brief.Length == 0 || brief.Length > MaxBriefLength)
            {
                throw new ArgumentException($"A brief is 1 to {MaxBriefLength} characters.", nameof(request));
            }

            await LandingDesign.RequireAssistantAsync(accessGuard, ct);
            return LandingDesign.ToDraft(await designer.GenerateAsync(brief, ct));
        }
    }

    /// <summary>
    /// A revision of ONE section of the site's generated page, by the administrator's instruction - the whole page with the
    /// section replaced, proposed, nothing stored ("click a section, prompt, apply").
    /// </summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record ProposeLandingSectionCommand(string Section, string Instruction) : ICommand<LandingDraftView>;

    public sealed class ProposeLandingSectionCommandHandler(ILandingPageRepository pages, SiteState siteState, ILandingPageDesigner designer, IAccessGuard accessGuard, LandingHtmlSanitizer sanitizer)
        : ICommandHandler<ProposeLandingSectionCommand, LandingDraftView>
    {
        public const int MaxInstructionLength = 2000;

        public async Task<LandingDraftView> Handle(ProposeLandingSectionCommand request, CancellationToken ct)
        {
            var instruction = (request.Instruction ?? string.Empty).Trim();
            if (instruction.Length == 0 || instruction.Length > MaxInstructionLength)
            {
                throw new ArgumentException($"An instruction is 1 to {MaxInstructionLength} characters.", nameof(request));
            }

            var page = await LandingScope.RequireAsync(pages, siteState, ct);
            if (page.Source != LandingSource.GeneratedHtml || string.IsNullOrEmpty(page.GeneratedHtml))
            {
                throw new InvalidOperationException("Only a generated landing page is edited section by section.");
            }

            var sectionHtml = LandingSections.Find(page.GeneratedHtml, request.Section)
                ?? throw new KeyNotFoundException($"The landing page has no section '{request.Section}'.");
            await LandingDesign.RequireAssistantAsync(accessGuard, ct);
            var revised = await designer.EditSectionAsync(sectionHtml, request.Section, instruction, ct);
            if (!revised.Succeeded || revised.Html is null)
            {
                return LandingDesign.ToDraft(revised);
            }

            // The page as it would be with the section replaced - sanitised as a whole, as applying it will be.
            var page2 = sanitizer.Sanitize(LandingSections.Replace(page.GeneratedHtml, request.Section, revised.Html));
            return new LandingDraftView(true, page2.Html, page2.Sections, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Makes (or remakes) the site's landing page from generated HTML - a proposal the administrator accepted. The HTML is
    /// sanitised HERE, whatever the page sent; a page with nothing left, or with no section, is refused. An edit to a published
    /// page is live.
    /// </summary>
    [Requires(LandingPermissions.Manage)]
    public sealed record ConfigureGeneratedLandingCommand(string Html) : ICommand<LandingPageView>;

    public sealed class ConfigureGeneratedLandingCommandHandler(ILandingPageRepository pages, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, TimeProvider clock, LandingHtmlSanitizer sanitizer)
        : ICommandHandler<ConfigureGeneratedLandingCommand, LandingPageView>
    {
        public async Task<LandingPageView> Handle(ConfigureGeneratedLandingCommand request, CancellationToken ct)
        {
            var sanitized = sanitizer.Sanitize(request.Html);
            if (sanitized.Html.Length == 0 || sanitized.Sections.Count == 0)
            {
                throw new ArgumentException("A generated landing page needs at least one section with a data-section name, and nothing the sanitizer must remove it all for.", nameof(request));
            }

            var siteId = LandingScope.Site(siteState);
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await pages.FindForSiteAsync(siteId, ct);
            var page = existing ?? LandingPageAggregate.Create(LandingScope.Tenant(tenantContext), siteId, now);
            page.ConfigureGenerated(sanitized.Html, now);
            if (existing is null)
            {
                await pages.AddAsync(page, ct);
            }
            else
            {
                await pages.UpdateAsync(page, ct);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return LandingScope.ToView(page);
        }
    }

    internal static class LandingDesign
    {
        public static async Task RequireAssistantAsync(IAccessGuard accessGuard, CancellationToken ct)
        {
            if (!await accessGuard.IsAllowedAsync(null, Stomme.AiAssistant.AssistantPermissions.Use, ct))
            {
                throw new AccessDeniedException($"Designing a landing page with the AI assistant needs {Stomme.AiAssistant.AssistantPermissions.Use} as well.");
            }
        }

        public static LandingDraftView ToDraft(LandingDesignResult result) =>
            new(result.Succeeded, result.Html ?? string.Empty, result.Sections, result.Problems);
    }

    /// <summary>A generated page's sections, found and replaced by their <c>data-section</c> name, with a real HTML parser.</summary>
    public static class LandingSections
    {
        /// <summary>The page's section names, in the order they appear.</summary>
        public static IReadOnlyList<string> Names(string html)
        {
            var (container, _) = Parse(html, string.Empty);
            return container.QuerySelectorAll("[data-section]").Select(element => element.GetAttribute("data-section") ?? string.Empty).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }

        public static string? Find(string html, string section)
        {
            var (_, element) = Parse(html, section);
            return element?.OuterHtml;
        }

        public static string Replace(string html, string section, string replacement)
        {
            var (container, element) = Parse(html, section);
            if (element is null)
            {
                throw new KeyNotFoundException($"The landing page has no section '{section}'.");
            }

            var parser = new HtmlParser();
            var nodes = parser.ParseFragment(replacement, element.Owner!.Body!).ToList();
            foreach (var node in nodes)
            {
                element.Parent!.InsertBefore(node, element);
            }

            element.Parent!.RemoveChild(element);
            return container.InnerHtml;
        }

        private static (AngleSharp.Dom.IElement Container, AngleSharp.Dom.IElement? Element) Parse(string html, string section)
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(string.Empty);
            var container = document.CreateElement("div");
            foreach (var node in parser.ParseFragment(html ?? string.Empty, document.Body!).ToList())
            {
                container.AppendChild(node);
            }

            var element = container.QuerySelectorAll("[data-section]").FirstOrDefault(candidate => string.Equals(candidate.GetAttribute("data-section"), section, StringComparison.Ordinal));
            return (container, element);
        }
    }
}
