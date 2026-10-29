#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Help.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Modules.ContextualHelp.Infrastructure
{
    /// <summary>
    /// Resolves and renders help for the CURRENT site (D103 items 4 and 12) - the port the launcher, the
    /// controller and BL-97's assistant all read through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PUBLISHED topics of the CURRENT site only, by construction</b> (D103 refutation 12). This port sits outside
    /// the mediator's authorization behavior, and the predecessor platform's equivalent returned every topic on the site, drafts
    /// included - so an assistant reading it would have quoted unpublished text back to a user. Every path here ends in
    /// an <c>IsPublished</c> test, the site comes from the resolved alias (through the closed repository), and drafts
    /// are reachable only through the <c>help:manage</c> list.
    /// </para>
    /// <para>
    /// <b>ONE locale chain</b> (the predecessor platform duplicated it): the asked locale → its <see cref="CultureInfo.Parent"/> chain →
    /// the site's DEFAULT language → <see cref="Constants.DefaultCulture"/>. Each step is a by-id read on the
    /// deterministic id - never a string query, which this repository has measured three different answers to. The
    /// first PUBLISHED topic wins; a draft in a closer locale does not hide a published one further down.
    /// </para>
    /// <para>
    /// <b>What is cached is the RENDERED HTML, keyed on the row's id AND its <c>ContentVersion</c></b> - a key that
    /// cannot go stale, because every text change bumps the version (and an unchanged boot does not, refutation 7). The
    /// by-id read itself is not cached: it is what tells us the version, and on RavenDB it is a document load, which
    /// does not wait for an index (refutation 3(c)'s <c>WaitForNonStaleResults</c> is a QUERY cost).
    /// </para>
    /// </remarks>
    public sealed class HelpTopicProvider : IHelpTopicProvider
    {
        /// <summary>The most results a search returns - it serves an assistant's context window, not a listing page.</summary>
        public const int MaximumSearchResults = 20;

        private readonly IHelpTopicRepository _topics;
        private readonly HelpMarkdownRenderer _renderer;
        private readonly ICacheManager _cache;
        private readonly ILanguageRepository _languages;
        private readonly SiteState _siteState;

        public HelpTopicProvider(IHelpTopicRepository topics, HelpMarkdownRenderer renderer, ICacheManager cache, ILanguageRepository languages, SiteState siteState)
        {
            _topics = topics ?? throw new ArgumentNullException(nameof(topics));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _languages = languages ?? throw new ArgumentNullException(nameof(languages));
            _siteState = siteState ?? throw new ArgumentNullException(nameof(siteState));
        }

        public async Task<HelpTopicView?> GetAsync(string anchor, string? locale = null, CancellationToken ct = default)
        {
            var alias = _siteState.Alias;
            if (alias is null || alias.SiteId <= 0 || !HelpKeys.TryNormalizeAnchor(anchor, out var normalizedAnchor))
            {
                return null;   // from the wire: "no topic", never an exception (D103 refutation 10)
            }

            foreach (var candidate in await LocaleChainAsync(locale, alias.SiteId))
            {
                var topic = await _topics.GetAsync(normalizedAnchor, candidate, ct);
                if (topic is { IsPublished: true })
                {
                    return new HelpTopicView(topic.Anchor, topic.Locale, topic.Title, await RenderAsync(topic));
                }
            }

            return null;
        }

        public async Task<IReadOnlyList<HelpTopicSummary>> SearchAsync(string query, CancellationToken ct = default)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return Array.Empty<HelpTopicSummary>();
            }

            // No full-text search in Wave 1 (Contains/ToLower have no provider-neutral translation). An
            // in-memory filter over THIS site's published rows - the repository's own FindAsync on the site id - is
            // admin scale and one answer on every provider.
            return (await _topics.ListPublishedAsync(ct))
                .Where(topic => topic.IsPublished
                                && (topic.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                                    || topic.Markdown.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(topic => topic.Anchor, StringComparer.Ordinal)
                .ThenBy(topic => topic.Locale, StringComparer.Ordinal)
                .Take(MaximumSearchResults)
                .Select(ToSummary)
                .ToList();
        }

        /// <summary>The chain, deduplicated and normalized, in order. Public for the suite: the order IS the behaviour.</summary>
        public async Task<IReadOnlyList<string>> LocaleChainAsync(string? requested, int siteId)
        {
            var chain = new List<string>();

            void Add(string? tag)
            {
                if (HelpKeys.TryNormalizeLocale(tag, out var normalized) && !chain.Contains(normalized, StringComparer.Ordinal))
                {
                    chain.Add(normalized);
                }
            }

            if (HelpKeys.TryNormalizeLocale(requested, out var asked))
            {
                // sv-se -> sv: the culture's own parents, stopping at the invariant culture (which is not a locale).
                for (var culture = CultureInfo.GetCultureInfo(asked); !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
                {
                    Add(culture.Name);
                }
            }

            var siteDefault = (await _languages.GetLanguagesAsync(siteId)).FirstOrDefault(language => language.IsDefault)?.Code;
            Add(siteDefault);
            Add(Constants.DefaultCulture);
            return chain;
        }

        internal static HelpTopicSummary ToSummary(HelpTopicAggregate topic) =>
            new(topic.Id, topic.Anchor, topic.Locale, topic.Title, topic.IsPublished, topic.IsSeeded,
                string.IsNullOrEmpty(topic.SeedContributorId) ? null : topic.SeedContributorId);

        private Task<string> RenderAsync(HelpTopicAggregate topic) =>
            _cache.GetCacheAsync(_siteState.Alias!, $"help:{topic.Id:N}:{topic.ContentVersion}",
                _ => Task.FromResult(_renderer.Render(topic.Markdown)));
    }
}
