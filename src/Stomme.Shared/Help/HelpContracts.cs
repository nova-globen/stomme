#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Help
{
    /// <summary>
    /// The contextual-help CONTRIBUTION SEAM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This file names no Help domain type, no Markdig type and nothing from <c>Stomme.Server</c>.</b> A module
    /// contributing help topics must be able to reference <c>Stomme.Shared</c> and stop - the same bar the MCP seam
    /// is held to, and for the same reason: a contributor that had to reach into the host could not be a package.
    /// A reflective sweep asserts it, in both directions.
    /// </para>
    /// <para>
    /// <b>Topics do not travel with their modules by themselves.</b> A module ships a topic or an anchor only by
    /// contributing it explicitly, and the contribution seam is a real contributor contract, not a two-method READ port
    /// with one implementation.
    /// </para>
    /// </remarks>
    public static class HelpKeys
    {
        /// <summary>An anchor is at most this long - it is a key, and a key that can be any length is a column that cannot be.</summary>
        public const int MaximumAnchorLength = 200;

        /// <summary>A locale tag is at most this long (BCP-47 with a private-use suffix fits comfortably).</summary>
        public const int MaximumLocaleLength = 16;

        /// <summary>The anchor of a PAGE, as opposed to a control on it: <c>page:{normalized path}</c>.</summary>
        public const string PagePrefix = "page:";

        /// <summary>
        /// Normalizes an anchor, or answers false. <b>It does not throw, and that is the whole point</b>:
        /// the anchor arrives from the wire (<c>?anchor=</c>) on an endpoint an anonymous visitor can
        /// reach, so a throwing factory would turn a malformed query string into a 500. Empty, over-long or otherwise
        /// invalid input means "no topic", which is what a reader should see anyway.
        /// </summary>
        public static bool TryNormalizeAnchor(string? anchor, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(anchor))
            {
                return false;
            }

            var trimmed = anchor.Trim().ToLowerInvariant();
            if (trimmed.Length > MaximumAnchorLength)
            {
                return false;
            }

            // Letters, digits and the four separators an anchor is built from: a dot for a type name, a colon for the
            // page prefix, a slash for a path, a hyphen for a word break. Anything else is somebody's punctuation and
            // has no business in a cache key or a document id.
            if (!trimmed.All(character => char.IsLetterOrDigit(character) || character is '.' or ':' or '/' or '-' or '_'))
            {
                return false;
            }

            normalized = trimmed;
            return true;
        }

        /// <summary>
        /// Normalizes a locale tag, or answers false. Non-throwing for the same reason as
        /// <see cref="TryNormalizeAnchor"/>. A tag that <see cref="CultureInfo"/> does not recognise is refused here
        /// rather than later: the resolution chain walks <c>CultureInfo.Parent</c>, and a tag no culture can be made
        /// from has no parent to walk to.
        /// </summary>
        public static bool TryNormalizeLocale(string? locale, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(locale))
            {
                return false;
            }

            var trimmed = locale.Trim();
            if (trimmed.Length > MaximumLocaleLength)
            {
                return false;
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(trimmed);
                if (string.IsNullOrEmpty(culture.Name))
                {
                    return false;   // the invariant culture is not a locale a topic can be written in
                }

                normalized = culture.Name.ToLowerInvariant();
                return true;
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
        }

        /// <summary>The anchor for a page path - <c>page:admin/users</c>. False when the path cannot be an anchor.</summary>
        public static bool TryPageAnchor(string? path, out string anchor)
        {
            anchor = string.Empty;
            var trimmed = (path ?? string.Empty).Trim().Trim('/');
            return TryNormalizeAnchor(PagePrefix + trimmed, out anchor);
        }

        /// <summary>
        /// The anchor for a module CONTROL, from its type name - <c>stomme.modules.admin.users.index</c>. False when the
        /// type name cannot be an anchor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Accepts what the router puts in <c>PageState.Modules[].ModuleType</c> (<c>Namespace.Type, Assembly</c>) as well
        /// as a bare full name, and keeps only the TYPE part: an assembly name is packaging, and a control that moves
        /// between assemblies is the same surface to a reader. The anchor is therefore per control AND per action -
        /// <c>...users.index</c> and <c>...users.edit</c> are different anchors, and a contributor
        /// ships each one it wants.
        /// </para>
        /// <para>
        /// A nested (<c>+</c>) or generic (<c>`</c>) type answers false rather than being mangled into something that
        /// resembles another type's anchor. No module control is either.
        /// </para>
        /// </remarks>
        public static bool TryModuleAnchor(string? moduleType, out string anchor)
        {
            anchor = string.Empty;
            if (string.IsNullOrWhiteSpace(moduleType))
            {
                return false;
            }

            var comma = moduleType.IndexOf(',');
            var typeName = comma >= 0 ? moduleType.Substring(0, comma) : moduleType;
            return TryNormalizeAnchor(typeName, out anchor);
        }
    }

    /// <summary>
    /// One topic a contributor ships. A contribution is a STATEMENT, not a row: the reconciler turns it
    /// into a row per site, and an operator may then edit that row without the contributor overwriting the edit.
    /// </summary>
    /// <remarks>
    /// The anchor and locale are normalized by the CONSTRUCTOR and refused when they cannot be, because a contributor
    /// is in-process code shipping a constant - unlike the wire, where the same input is a query string and must
    /// answer "no topic" instead (<see cref="HelpKeys.TryNormalizeAnchor"/>). A contributor with a malformed anchor is
    /// a bug in that contributor, and it should be loud where it is written.
    /// </remarks>
    public sealed class HelpTopicContribution
    {
        public const int MaximumTitleLength = 200;

        /// <summary>
        /// The body's bound, in CHARACTERS. Deliberately not a <c>MaxLength</c> on the column: 32 000 is not a valid
        /// <c>nvarchar</c> length on SQL Server, so binding it in the domain and leaving the column unbounded is the
        /// only shape that works on every provider.
        /// </summary>
        public const int MaximumMarkdownCharacters = 32_000;

        public HelpTopicContribution(string anchor, string locale, string title, string markdown, int order = 0)
        {
            if (!HelpKeys.TryNormalizeAnchor(anchor, out var normalizedAnchor))
            {
                throw new ArgumentException($"'{anchor}' is not a usable help anchor.", nameof(anchor));
            }

            if (!HelpKeys.TryNormalizeLocale(locale, out var normalizedLocale))
            {
                throw new ArgumentException($"'{locale}' is not a usable locale tag.", nameof(locale));
            }

            var trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0 || trimmedTitle.Length > MaximumTitleLength)
            {
                throw new ArgumentException($"A help topic title is 1 to {MaximumTitleLength} characters.", nameof(title));
            }

            var body = markdown ?? string.Empty;
            if (body.Length == 0 || body.Length > MaximumMarkdownCharacters)
            {
                throw new ArgumentException($"A help topic body is 1 to {MaximumMarkdownCharacters} characters.", nameof(markdown));
            }

            Anchor = normalizedAnchor;
            Locale = normalizedLocale;
            Title = trimmedTitle;
            Markdown = body;
            Order = order;
        }

        public string Anchor { get; }

        public string Locale { get; }

        public string Title { get; }

        /// <summary>The body, as MARKDOWN. It is never rendered here - rendering is server-side, at read, through the sanitiser.</summary>
        public string Markdown { get; }

        /// <summary>Where this topic sits among several for one anchor. Ordering only; it decides nothing else.</summary>
        public int Order { get; }
    }

    /// <summary>
    /// A source of help topics. Discovered by the <c>IFeatureContributor</c> convention (a public parameterless
    /// constructor in an assembly whose name contains <c>Stomme</c>), plus explicit registration for a contributor
    /// with dependencies.
    /// </summary>
    /// <remarks>
    /// <b>A duplicate <c>(anchor, locale)</c> does NOT stop the host</b> (the same rule holds for every contribution seam). Discovery matches any assembly in <c>/bin</c> whose name contains <c>Stomme</c>,
    /// including a package unpacked there - so a fatal check would let a third-party package brick an install, with no
    /// admin UI left to remove it. Instead EVERY side of a collision is rejected - there is no "the framework wins",
    /// because a winner is a rule an attacker can aim at - logged at Error and listed on the admin page. In-tree
    /// duplicates fail a reflective BUILD gate.
    /// </remarks>
    public interface IHelpTopicContributor
    {
        /// <summary>Stable, and the thing a seeded row remembers so that one contributor cannot overwrite another's.</summary>
        string ContributorId { get; }

        IReadOnlyCollection<HelpTopicContribution> Topics { get; }
    }

    /// <summary>A topic as a READER receives it: already resolved, already rendered, already sanitised.</summary>
    /// <remarks>
    /// <b><see cref="SafeHtml"/> is the only rendered form that leaves the server</b>, and the name is a claim the
    /// sanitiser has to earn. Markdown is stored; HTML is produced at read, server-side, through an element,
    /// attribute and URL-scheme allowlist; no Markdig type is referenced from <c>Stomme.Client</c>.
    /// </remarks>
    public sealed class HelpTopicView
    {
        public HelpTopicView(string anchor, string locale, string title, string safeHtml)
        {
            Anchor = anchor ?? string.Empty;
            Locale = locale ?? string.Empty;
            Title = title ?? string.Empty;
            SafeHtml = safeHtml ?? string.Empty;
        }

        public string Anchor { get; }

        /// <summary>The locale actually RESOLVED, which may not be the one asked for - the chain falls back.</summary>
        public string Locale { get; }

        public string Title { get; }

        public string SafeHtml { get; }
    }

    /// <summary>A topic in a list: enough to choose one, and no body.</summary>
    public sealed class HelpTopicSummary
    {
        public HelpTopicSummary(Guid id, string anchor, string locale, string title, bool isPublished, bool isSeeded, string? seedContributorId)
        {
            Id = id;
            Anchor = anchor ?? string.Empty;
            Locale = locale ?? string.Empty;
            Title = title ?? string.Empty;
            IsPublished = isPublished;
            IsSeeded = isSeeded;
            SeedContributorId = seedContributorId;
        }

        public Guid Id { get; }

        public string Anchor { get; }

        public string Locale { get; }

        public string Title { get; }

        public bool IsPublished { get; }

        /// <summary>True while the row is still the contributor's to update - an operator edit clears it.</summary>
        public bool IsSeeded { get; }

        /// <summary>Which contributor seeded it. A seed update applies only when this MATCHES.</summary>
        public string? SeedContributorId { get; }
    }

    /// <summary>
    /// Reads help topics for the CURRENT SITE. The port the assistant consumes.
    /// </summary>
    /// <remarks>
    /// <b>PUBLISHED topics of the CURRENT site only, by construction</b>. This port sits outside
    /// the mediator's authorization behavior, and one that returned every topic on the site, drafts
    /// included, would let an assistant reading it quote unpublished text back to a user. Drafts are reachable
    /// only through the <c>help:manage</c>-gated list. A gate asserts a draft is never returned.
    /// </remarks>
    public interface IHelpTopicProvider
    {
        /// <summary>The topic for an anchor, or null. The locale chain falls back; the view says which locale answered.</summary>
        Task<HelpTopicView?> GetAsync(string anchor, string? locale = null, CancellationToken ct = default);

        /// <summary>Published topics of this site whose title or body contains <paramref name="query"/>.</summary>
        Task<IReadOnlyList<HelpTopicSummary>> SearchAsync(string query, CancellationToken ct = default);
    }
}
