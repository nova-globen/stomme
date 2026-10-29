#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Infrastructure.Security;
using Stomme.Models.Pipelines;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// Read RSS feed (n8n's RSS Read node): a feed's items as records - RSS 2.0, RSS 1.0 (RDF) and Atom alike - each with
    /// its <c>title</c>, <c>link</c>, <c>id</c> (the guid, or the link), <c>published</c> (ISO 8601, in UTC, when the feed's date
    /// reads as one; as written otherwise), <c>author</c>, <c>summary</c> and <c>categories</c>. Fetched from its URL by the
    /// guarded outbound path the HTTP request step takes (<c>Pipelines:Http:AllowedHosts</c> and the private-address refusal,
    /// every redirect judged again, <c>MaxResponseBytes</c>), or read from text - the previous step's result. A feed with a DTD is
    /// refused, so no entity reaches a file or the network.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Data", "Read an RSS or Atom feed's items as records - from its URL, or from the feed's text.", DisplayName = "Read RSS feed")]
    public class ReadFeed : DataStep
    {
        [Input(DisplayName = "URL", Description = "The feed's address. Empty: the feed is the text below, or the previous step's result.", Order = 1)]
        public Input<string?>? Url { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "The feed's XML, when there is no URL. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Feed { get; set; }

        [Input(DisplayName = "Max items", Description = "At most so many items, the feed's first. 0: every one.", DefaultValue = 0, Order = 3)]
        public Input<int> MaxItems { get; set; } = new(0);

        [Output(DisplayName = "Feed title")]
        public Output<string>? Title { get; set; }

        [Output(DisplayName = "Item count")]
        public Output<int>? Count { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            string text;
            var url = Url?.GetOrDefault(context);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var (fetched, code, refusal) = await FetchAsync(context, url.Trim()).ConfigureAwait(false);
                if (fetched is null)
                {
                    await FailAsync(context, code!, refusal!).ConfigureAwait(false);
                    return;
                }

                text = fetched;
            }
            else
            {
                text = TextOrLastResult(context, Feed) ?? "";
            }

            if (!TryRead(text, out var title, out var items, out var error))
            {
                await FailAsync(context, "FEED_INVALID", error!).ConfigureAwait(false);
                return;
            }

            var max = MaxItems.GetOrDefault(context);
            var kept = max > 0 ? items.Take(max).ToList() : items;
            context.Set(Title, title);
            context.Set(Count, kept.Count);
            context.AddExecutionLogEntry("Read", $"Read {kept.Count} of {items.Count} item(s) from '{title}'.");
            await SucceedAsync(context, Records.ToJson(kept)).ConfigureAwait(false);
        }

        /// <summary>A feed's title and items; false, with the reason, for text that is no RSS or Atom feed.</summary>
        public static bool TryRead(string text, out string title, out List<JsonObject> items, out string? error)
        {
            title = "";
            items = new List<JsonObject>();
            error = null;
            XDocument document;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = Xml.MaxCharacters };
                using var reader = XmlReader.Create(new StringReader(text.TrimStart('﻿', ' ', '\r', '\n', '\t')), settings);
                document = XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                error = "The feed is not XML this step reads: " + ex.Message;
                return false;
            }

            var root = document.Root!;
            var atom = root.Name.LocalName == "feed";
            if (!atom && root.Name.LocalName is not ("rss" or "RDF"))
            {
                error = $"The XML is not an RSS or Atom feed: its root is <{root.Name.LocalName}>.";
                return false;
            }

            var channel = atom ? root : Child(root, "channel") ?? root;
            title = Text(Child(channel, "title"));
            foreach (var entry in root.Descendants().Where(element => element.Name.LocalName == (atom ? "entry" : "item")))
            {
                items.Add(atom ? AtomItem(entry) : RssItem(entry));
            }

            return true;
        }

        private static JsonObject RssItem(XElement item)
        {
            var link = Text(Child(item, "link"));
            var guid = Text(Child(item, "guid"));
            return Item(
                Text(Child(item, "title")),
                link,
                guid.Length > 0 ? guid : link,
                Text(Child(item, "pubDate") ?? Child(item, "date")),
                Text(Child(item, "creator") ?? Child(item, "author")),
                Text(Child(item, "description") ?? Child(item, "encoded")),
                item.Elements().Where(element => element.Name.LocalName is "category" or "subject").Select(Text));
        }

        private static JsonObject AtomItem(XElement entry)
        {
            var links = entry.Elements().Where(element => element.Name.LocalName == "link").ToList();
            var link = (links.FirstOrDefault(element => (string?)element.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault())?.Attribute("href")?.Value ?? "";
            var id = Text(Child(entry, "id"));
            return Item(
                Text(Child(entry, "title")),
                link,
                id.Length > 0 ? id : link,
                Text(Child(entry, "published") ?? Child(entry, "updated")),
                Text(Child(Child(entry, "author"), "name")),
                Text(Child(entry, "summary") ?? Child(entry, "content")),
                entry.Elements().Where(element => element.Name.LocalName == "category").Select(element => element.Attribute("term")?.Value ?? Text(element)));
        }

        private static JsonObject Item(string title, string link, string id, string published, string author, string summary, IEnumerable<string> categories) => new()
        {
            ["title"] = title,
            ["link"] = link,
            ["id"] = id,
            ["published"] = Moment(published),
            ["author"] = author,
            ["summary"] = summary,
            ["categories"] = new JsonArray(categories.Where(category => category.Length > 0).Select(category => (JsonNode?)JsonValue.Create(category)).ToArray()),
        };

        // RSS dates are RFC 822 ("Tue, 07 Oct 2026 05:00:00 GMT"), Atom's ISO 8601; either becomes ISO 8601 in UTC. A date that
        // reads as neither stays as the feed wrote it.
        private static string Moment(string text)
        {
            if (text.Length == 0)
            {
                return "";
            }

            var trimmed = text.Trim();
            foreach (var zone in new[] { ("GMT", "+00:00"), ("UT", "+00:00"), ("EST", "-05:00"), ("EDT", "-04:00"), ("CST", "-06:00"), ("CDT", "-05:00"), ("MST", "-07:00"), ("MDT", "-06:00"), ("PST", "-08:00"), ("PDT", "-07:00") })
            {
                if (trimmed.EndsWith(" " + zone.Item1, StringComparison.Ordinal))
                {
                    trimmed = trimmed[..^zone.Item1.Length] + zone.Item2;
                    break;
                }
            }

            return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? PipelineSiteTables.Moment(moment)
                : text;
        }

        private static XElement? Child(XElement? parent, string name) =>
            parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

        private static string Text(XElement? element) => element?.Value.Trim() ?? "";

        // GET through the same guard the HTTP request step uses: the allowlist, the private-address refusal, every redirect judged.
        private static async Task<(string? Text, string? Code, string? Refusal)> FetchAsync(StepExecutionContext context, string url)
        {
            var options = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Http ?? new HttpStepOptions();
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var target))
            {
                return (null, "INVALID_URL", $"'{url}' is not an absolute URL.");
            }

            var allowedHosts = (IReadOnlyCollection<string>)options.AllowedHosts;
            using var handler = OutboundConnectGuard.CreateHandler();
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(Math.Clamp(30, 1, Math.Max(1, options.MaxTimeoutSeconds))) };
            try
            {
                for (var hop = 0; ; hop++)
                {
                    if (await OutboundConnectGuard.CheckDestinationAsync(target, allowedHosts, context.CancellationToken).ConfigureAwait(false) is { } refusal)
                    {
                        return (null, "EGRESS_REFUSED", hop == 0 ? refusal : $"Redirect to '{target}' refused: {refusal}");
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, target);
                    request.Options.Set(OutboundConnectGuard.AllowedHostsOption, allowedHosts);
                    request.Headers.TryAddWithoutValidation("Accept", "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.5");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hop < options.MaxRedirects)
                    {
                        target = location.IsAbsoluteUri ? location : new Uri(target, location);
                        continue;
                    }

                    var text = await SendHttpRequest.ReadBoundedAsync(response, options.MaxResponseBytes, context).ConfigureAwait(false);
                    return response.IsSuccessStatusCode
                        ? (text, null, null)
                        : (null, "HTTP_ERROR", $"HTTP {status} {response.ReasonPhrase} from '{target}'.");
                }
            }
            catch (Exception ex) when (OutboundConnectGuard.IsDenial(ex))
            {
                return (null, "EGRESS_REFUSED", OutboundConnectGuard.FindDenial(ex)!.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return (null, "HTTP_REQUEST_FAILED", ex is TaskCanceledException ? "No response within 30 seconds." : ex.Message);
            }
        }
    }
}
