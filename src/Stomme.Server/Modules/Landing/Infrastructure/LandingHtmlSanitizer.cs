#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Stomme.Modules.Landing.Infrastructure
{
    /// <summary>
    /// The allowlist a generated landing page passes through before it is stored and again before it is shown (BL-98 increment
    /// 3; the predecessor platform's plan: "sanitized to a page-level allowlist - structural and inline-style tags, no script, no inline event
    /// handlers, constrained external resource origins"). The Help module's sanitizer is the precedent: a real HTML5
    /// parser and an edit of the tree, never a regex over markup; an element is ALLOWED (its attributes filtered), DROPPED
    /// WITH ITS CONTENT (code, a second parser, a form, a frame) or UNWRAPPED (its text stays).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Wider than help text, on purpose, and never alone.</b> A landing page is laid out, so it keeps sections, blocks,
    /// figures and an inline style held to a property allowlist - with no <c>url()</c>, no <c>expression</c>, no escapes and
    /// no <c>position</c> (a positioned block could lie over the site's own controls). The page is ALSO shown in a sandboxed
    /// frame with no scripts and a content policy of its own (<c>Stomme.Landing.LandingFrame</c>): the sanitizer is the first guard, the
    /// frame the second.
    /// </para>
    /// <para>
    /// <b>Sections are addressable</b>: a block may carry <c>data-section</c>, a lowercase token - the edit mode's handle.
    /// A token repeated keeps its first use only.
    /// </para>
    /// </remarks>
    public sealed class LandingHtmlSanitizer
    {
        public const int MaxLength = 100_000;

        private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "section", "header", "footer", "article", "aside", "div", "span", "figure", "figcaption",
            "h1", "h2", "h3", "h4", "h5", "h6", "p", "br", "hr",
            "strong", "em", "b", "i", "u", "s", "small", "mark", "sub", "sup", "blockquote", "q", "cite", "abbr", "time",
            "ul", "ol", "li", "dl", "dt", "dd",
            "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
            "a", "img", "picture",
        };

        private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "noembed",
            "template", "svg", "math", "form", "input", "textarea", "select", "option", "button", "link", "meta", "base",
            "title", "head", "audio", "video", "source", "track", "canvas", "portal", "xmp", "plaintext", "dialog",
        };

        private static readonly HashSet<string> CommonAttributes = new(StringComparer.OrdinalIgnoreCase) { "style", "title", "data-section", "lang", "dir" };

        private static readonly Dictionary<string, HashSet<string>> ElementAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(StringComparer.OrdinalIgnoreCase) { "href" },
            ["img"] = new(StringComparer.OrdinalIgnoreCase) { "src", "alt", "width", "height" },
            ["td"] = new(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan" },
            ["th"] = new(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan", "scope" },
            ["ol"] = new(StringComparer.OrdinalIgnoreCase) { "start" },
            ["time"] = new(StringComparer.OrdinalIgnoreCase) { "datetime" },
        };

        // Layout, type and colour - nothing that fetches, positions over the page, or runs.
        private static readonly HashSet<string> StyleProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "background-color", "opacity",
            "font-family", "font-size", "font-weight", "font-style", "line-height", "letter-spacing", "text-align",
            "text-decoration", "text-transform", "white-space", "word-break",
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
            "border", "border-top", "border-right", "border-bottom", "border-left", "border-color", "border-width",
            "border-style", "border-radius", "box-shadow", "box-sizing",
            "display", "flex", "flex-direction", "flex-wrap", "flex-grow", "flex-shrink", "flex-basis", "align-items",
            "align-self", "align-content", "justify-content", "justify-items", "gap", "row-gap", "column-gap", "order",
            "grid-template-columns", "grid-template-rows", "grid-column", "grid-row",
            "width", "max-width", "min-width", "height", "max-height", "min-height", "object-fit", "overflow",
        };

        private static readonly Regex SectionToken = new(@"^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new(@"^\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex UnsafeStyleValue = new(@"url\s*\(|expression|javascript|vbscript|@|\\|/\*|<|>|behavior|binding|image-set|element\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The sanitized page, and what was taken out of it - for the agent to put right, and for a test to read.</summary>
        public LandingSanitizeResult Sanitize(string? html)
        {
            var removed = new List<string>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return new LandingSanitizeResult(string.Empty, Array.Empty<string>(), removed);
            }

            if (html.Length > MaxLength)
            {
                removed.Add($"the page is longer than {MaxLength} characters");
                return new LandingSanitizeResult(string.Empty, Array.Empty<string>(), removed);
            }

            var parser = new HtmlParser();
            var document = parser.ParseDocument(string.Empty);
            var container = document.CreateElement("div");
            foreach (var node in parser.ParseFragment(html, document.Body!).ToList())
            {
                container.AppendChild(node);
            }

            var sections = new List<string>();
            CleanChildren(container, removed, sections);
            return new LandingSanitizeResult(container.InnerHtml, sections, removed);
        }

        private static void CleanChildren(INode parent, List<string> removed, List<string> sections)
        {
            foreach (var node in parent.ChildNodes.ToList())
            {
                switch (node)
                {
                    case IText:
                        break;

                    case IElement element:
                        CleanElement(element, removed, sections);
                        break;

                    default:
                        Remove(node);
                        break;
                }
            }
        }

        private static void CleanElement(IElement element, List<string> removed, List<string> sections)
        {
            var name = element.LocalName;
            if (DroppedWithContent.Contains(name) || !string.Equals(element.NamespaceUri, NamespaceNames.HtmlUri, StringComparison.Ordinal))
            {
                removed.Add("<" + name + ">");
                Remove(element);
                return;
            }

            if (!Allowed.Contains(name))
            {
                removed.Add("<" + name + "> (its text kept)");
                CleanChildren(element, removed, sections);
                Unwrap(element);
                return;
            }

            FilterAttributes(element, name, removed, sections);
            switch (name.ToLowerInvariant())
            {
                case "a":
                    if (!TryClassifyUrl(element.GetAttribute("href"), allowMailto: true, out var external))
                    {
                        removed.Add("a link to " + Shown(element.GetAttribute("href")));
                        CleanChildren(element, removed, sections);
                        Unwrap(element);
                        return;
                    }

                    // A sandboxed frame: a site link leaves it for the site, an external one opens apart and cannot reach back.
                    element.SetAttribute("target", external ? "_blank" : "_top");
                    if (external)
                    {
                        element.SetAttribute("rel", "noopener noreferrer nofollow");
                    }

                    break;

                case "img":
                    if (!TryClassifyUrl(element.GetAttribute("src"), allowMailto: false, out _))
                    {
                        removed.Add("an image from " + Shown(element.GetAttribute("src")));
                        Remove(element);
                        return;
                    }

                    break;
            }

            CleanChildren(element, removed, sections);
        }

        private static void FilterAttributes(IElement element, string name, List<string> removed, List<string> sections)
        {
            ElementAttributes.TryGetValue(name, out var specific);
            foreach (var attribute in element.Attributes.ToList())
            {
                var attributeName = attribute.Name;
                if (!CommonAttributes.Contains(attributeName) && specific?.Contains(attributeName) != true)
                {
                    element.RemoveAttribute(attributeName);
                    removed.Add(attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ? "an event handler (" + attributeName + ")" : "the attribute " + attributeName);
                    continue;
                }

                switch (attributeName.ToLowerInvariant())
                {
                    case "style":
                        var style = CleanStyle(attribute.Value, removed);
                        if (style.Length == 0)
                        {
                            element.RemoveAttribute(attributeName);
                        }
                        else
                        {
                            element.SetAttribute(attributeName, style);
                        }

                        break;

                    case "data-section":
                        var token = attribute.Value.Trim();
                        if (!SectionToken.IsMatch(token) || sections.Contains(token, StringComparer.Ordinal))
                        {
                            element.RemoveAttribute(attributeName);
                            removed.Add("a section name " + Shown(token));
                        }
                        else
                        {
                            element.SetAttribute(attributeName, token);
                            sections.Add(token);
                        }

                        break;

                    case "width":
                    case "height":
                    case "colspan":
                    case "rowspan":
                    case "start":
                        if (!Number.IsMatch(attribute.Value.Trim()))
                        {
                            element.RemoveAttribute(attributeName);
                        }

                        break;
                }
            }
        }

        private static string CleanStyle(string value, List<string> removed)
        {
            var kept = new List<string>();
            foreach (var declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = declaration.IndexOf(':');
                var property = colon > 0 ? declaration[..colon].Trim() : string.Empty;
                var propertyValue = colon > 0 ? declaration[(colon + 1)..].Trim() : string.Empty;
                if (property.Length == 0 || propertyValue.Length == 0 || !StyleProperties.Contains(property) || UnsafeStyleValue.IsMatch(propertyValue))
                {
                    removed.Add("the style " + Shown(declaration));
                    continue;
                }

                kept.Add(property.ToLowerInvariant() + ": " + propertyValue);
            }

            return string.Join("; ", kept);
        }

        private static string Shown(string? value)
        {
            var clean = (value ?? string.Empty).Trim();
            return "'" + (clean.Length <= 60 ? clean : clean[..60] + "…") + "'";
        }

        private static void Remove(INode node) => node.Parent?.RemoveChild(node);

        private static void Unwrap(IElement element)
        {
            var parent = element.Parent;
            if (parent is null)
            {
                return;
            }

            foreach (var child in element.ChildNodes.ToList())
            {
                parent.InsertBefore(child, element);
            }

            Remove(element);
        }

        // The Help sanitizer's rule, the same reading of a URL a browser makes: relative, http, https and (for a link) mailto.
        private static bool TryClassifyUrl(string? raw, bool allowMailto, out bool external) =>
            Stomme.Modules.ContextualHelp.Infrastructure.HelpHtmlSanitizer.TryClassifyUrl(raw, allowMailto, out external);
    }

    public sealed record LandingSanitizeResult(string Html, IReadOnlyList<string> Sections, IReadOnlyList<string> Removed);
}
