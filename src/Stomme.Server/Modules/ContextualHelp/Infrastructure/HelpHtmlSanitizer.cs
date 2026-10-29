#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Stomme.Modules.ContextualHelp.Infrastructure
{
    /// <summary>
    /// The allowlist every piece of help HTML passes through before it leaves the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an allowlist behind a Markdown pipeline that already disables HTML.</b> <c>DisableHtml()</c> is one
    /// switch in a pipeline other code can change, and Markdig's own documentation says <c>UseAdvancedExtensions</c>
    /// includes <c>UseGenericAttributes</c>, which writes arbitrary attributes (<c>{onclick=...}</c>) that
    /// <c>DisableHtml()</c> does NOT stop. Help text is shown to ANONYMOUS readers, so the rendered output is held to
    /// this list whatever the pipeline did - and the suite drives it both with and without <c>DisableHtml()</c>.
    /// </para>
    /// <para>
    /// <b>It parses with a real HTML5 parser and edits the tree</b> - never a regex over markup - so a construct that a
    /// browser would parse differently from how it reads (a stray end tag, a comment, an entity-encoded scheme) is
    /// judged by what it IS after parsing. The fragment is parsed in a <c>body</c> context, so nothing can escape into a
    /// <c>head</c>.
    /// </para>
    /// <para>
    /// Three outcomes for an element: ALLOWED (kept, attributes filtered), DROPPED WITH ITS CONTENT (script, style,
    /// frames, embedded documents, forms, SVG/MathML - whose content is code or a second parser), or UNWRAPPED (anything
    /// else - its text stays, the tag goes). A link or image whose URL is refused loses the element, not the page.
    /// </para>
    /// </remarks>
    public sealed class HelpHtmlSanitizer
    {
        /// <summary>Added to every link that leaves the site. <c>nofollow</c>: help text must not lend the site's rank.</summary>
        public const string ExternalRel = "noopener noreferrer nofollow";

        private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6",
            "strong", "em", "b", "i", "del", "s", "ins", "mark", "sub", "sup",
            "code", "pre", "blockquote", "ul", "ol", "li",
            "table", "thead", "tbody", "tr", "th", "td",
            "a", "img", "input",
        };

        private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "noembed",
            "template", "svg", "math", "form", "textarea", "select", "option", "button", "link", "meta", "base",
            "title", "head", "audio", "video", "source", "track", "canvas", "portal", "xmp", "plaintext",
        };

        private static readonly Dictionary<string, HashSet<string>> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(StringComparer.OrdinalIgnoreCase) { "href", "title" },
            ["img"] = new(StringComparer.OrdinalIgnoreCase) { "src", "alt", "title" },
            ["input"] = new(StringComparer.OrdinalIgnoreCase) { "type", "checked", "disabled" },
            ["ol"] = new(StringComparer.OrdinalIgnoreCase) { "start" },
            ["th"] = new(StringComparer.OrdinalIgnoreCase) { "style" },
            ["td"] = new(StringComparer.OrdinalIgnoreCase) { "style" },
            ["ul"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
            ["li"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
            ["code"] = new(StringComparer.OrdinalIgnoreCase) { "class" },
        };

        // What Markdig itself writes, and nothing more: pipe-table alignment, task-list classes, a code block's language.
        private static readonly Regex TableAlignment = new(@"^\s*text-align:\s*(left|right|center)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LanguageClass = new(@"^language-[a-z0-9+#-]{1,40}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> TaskListClasses = new(StringComparer.Ordinal) { "contains-task-list", "task-list-item", "task-list-item-checkbox" };
        private static readonly Regex OrderedStart = new(@"^\d{1,6}$", RegexOptions.CultureInvariant);

        /// <summary>Returns <paramref name="html"/> reduced to the allowlist. Never throws on content.</summary>
        /// <remarks>A parser per call: this type is a singleton, and <c>HtmlParser</c> does not document concurrent use.</remarks>
        public string Sanitize(string? html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            var parser = new HtmlParser();
            var document = parser.ParseDocument(string.Empty);
            var container = document.CreateElement("div");
            foreach (var node in parser.ParseFragment(html, document.Body!).ToList())
            {
                container.AppendChild(node);
            }

            CleanChildren(container);
            return container.InnerHtml;
        }

        private static void CleanChildren(INode parent)
        {
            foreach (var node in parent.ChildNodes.ToList())
            {
                switch (node)
                {
                    case IText:
                        break;

                    case IElement element:
                        CleanElement(element);
                        break;

                    default:
                        // comments, processing instructions, doctypes - none of them is help text
                        Remove(node);
                        break;
                }
            }
        }

        private static void CleanElement(IElement element)
        {
            var name = element.LocalName;

            // The namespace test is a SECOND, independent guard, and removing it is an EQUIVALENT mutant (measured): in a
            // body-context fragment the tree builder creates foreign elements only inside <svg>/<math>, which the list
            // already drops whole. It stays so that editing the list cannot let SVG or MathML - a second parser - through.
            if (DroppedWithContent.Contains(name) || !string.Equals(element.NamespaceUri, AngleSharp.Dom.NamespaceNames.HtmlUri, StringComparison.Ordinal))
            {
                Remove(element);
                return;
            }

            if (!Allowed.Contains(name))
            {
                CleanChildren(element);
                Unwrap(element);
                return;
            }

            FilterAttributes(element, name);

            switch (name.ToLowerInvariant())
            {
                case "a":
                    var href = element.GetAttribute("href");
                    if (!TryClassifyUrl(href, allowMailto: true, out var external))
                    {
                        // A link to nowhere safe is just its text.
                        CleanChildren(element);
                        Unwrap(element);
                        return;
                    }

                    if (external)
                    {
                        element.SetAttribute("rel", ExternalRel);
                        element.SetAttribute("target", "_blank");
                    }

                    break;

                case "img":
                    if (!TryClassifyUrl(element.GetAttribute("src"), allowMailto: false, out _))
                    {
                        Remove(element);
                        return;
                    }

                    break;

                case "input":
                    // Markdig's task-list box and nothing else, and never one a reader can operate.
                    if (!string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase))
                    {
                        Remove(element);
                        return;
                    }

                    element.SetAttribute("disabled", "disabled");
                    break;
            }

            CleanChildren(element);
        }

        private static void FilterAttributes(IElement element, string name)
        {
            AllowedAttributes.TryGetValue(name, out var allowed);
            foreach (var attribute in element.Attributes.ToList())
            {
                if (allowed is null || !allowed.Contains(attribute.Name) || !ValueIsAllowed(attribute.Name, attribute.Value))
                {
                    element.RemoveAttribute(attribute.Name);
                }
            }
        }

        private static bool ValueIsAllowed(string attribute, string value) => attribute.ToLowerInvariant() switch
        {
            "style" => TableAlignment.IsMatch(value),
            "class" => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(token => TaskListClasses.Contains(token) || LanguageClass.IsMatch(token)),
            "start" => OrderedStart.IsMatch(value),
            _ => true,                              // href/src are judged by the element's own case, which can unwrap it
        };

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

        /// <summary>
        /// Decides whether a URL may stay, and whether it leaves the site. <b>Relative, http, https, and (for links)
        /// mailto</b> - everything else, <c>javascript:</c> and <c>data:</c> included, is refused.
        /// </summary>
        /// <remarks>
        /// The scheme is read from the value with EVERY whitespace and control character removed: a browser strips tabs
        /// and newlines inside a scheme (<c>java&#9;script:</c> runs), so the check has to see what the browser sees.
        /// Entity-encoded schemes (<c>&amp;#106;avascript:</c>) are already decoded by the parser before this runs.
        /// </remarks>
        internal static bool TryClassifyUrl(string? raw, bool allowMailto, out bool external)
        {
            external = false;
            if (raw is null)
            {
                return false;
            }

            var cleaned = new string(raw.Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character)).ToArray());
            if (cleaned.Length == 0)
            {
                return false;
            }

            // Protocol-relative, including the backslash spellings a browser normalizes to "//".
            if (cleaned.StartsWith("//", StringComparison.Ordinal) || cleaned.StartsWith(@"\\", StringComparison.Ordinal)
                || cleaned.StartsWith(@"/\", StringComparison.Ordinal) || cleaned.StartsWith(@"\/", StringComparison.Ordinal))
            {
                external = true;
                return true;
            }

            var colon = cleaned.IndexOf(':');
            var delimiter = cleaned.IndexOfAny(new[] { '/', '?', '#', '\\' });
            if (colon < 0 || (delimiter >= 0 && delimiter < colon))
            {
                return true;                         // relative: no scheme at all
            }

            switch (cleaned.Substring(0, colon).ToLowerInvariant())
            {
                case "http":
                case "https":
                    external = true;
                    return true;

                case "mailto":
                    return allowMailto;

                default:
                    return false;
            }
        }
    }
}
