#nullable enable annotations // project is nullable-disabled; make the string? parameter annotations legal (CS8632) without turning on flow analysis
using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Providers;

public class DatabaseSearchProvider(
    ISearchContentRepository searchContentRepository,
    TimeProvider timeProvider) : ISearchProvider
{
    // Preserves the original public constructor and requires no DI change.
    // Tests or applications that already register TimeProvider can use the
    // two-parameter constructor for deterministic timestamps.
    public DatabaseSearchProvider(ISearchContentRepository searchContentRepository)
        : this(searchContentRepository, TimeProvider.System)
    {
    }

    private const string SearchIgnoreWordsSetting = "Search_IgnoreWords";
    private const string SearchMinimumWordLengthSetting = "Search_MinimumWordLength";

    // Keep supporting the misspelled setting name used by existing installations.
    private const string LegacySearchMinimumWordLengthSetting = "Search_MininumWordLength";

    private const string IgnoreWordsCsv = "the,be,to,of,and,a,i,in,that,have,it,for,not,on,with,he,as,you,do,at,this,but,his,by,from,they,we,say,her,she,or,an,will,my,one,all,would,there,their,what,so,up,out,if,about,who,get,which,go,me,when,make,can,like,time,no,just,him,know,take,people,into,year,your,good,some,could,them,see,other,than,then,now,look,only,come,its,over,think,also,back,after,use,two,how,our,work,first,well,way,even,new,want,because,any,these,give,day,most,us";

    private const int DefaultMinimumWordLength = 3;
    private const int SnippetLeadingContextLength = 20;

    private static readonly HtmlParserOptions CompatibilityParserOptions = new()
    {
        // The legacy pipeline first called WebUtility.HtmlDecode and then used
        // HtmlAgilityPack text nodes, which did not decode remaining entities.
        // This option prevents AngleSharp from performing an unintended second decode.
        IsNotConsumingCharacterReferences = true
    };

    private static readonly SearchValues<char> IndexPunctuation =
        SearchValues.Create(".?!,;:_()[]{}'\"/\\");

    private static readonly FrozenSet<string> DefaultIgnoreWords = IgnoreWordsCsv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ISearchContentRepository _searchContentRepository =
        searchContentRepository ?? throw new ArgumentNullException(nameof(searchContentRepository));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public string Name => Constants.DefaultSearchProviderName;

    public async Task<List<SearchResult>> GetSearchResultsAsync(SearchQuery searchQuery)
    {
        ArgumentNullException.ThrowIfNull(searchQuery);

        var searchContents = await _searchContentRepository
            .GetSearchContentsAsync(searchQuery)
            .ConfigureAwait(false);

        return [.. searchContents.Select(item => ConvertToSearchResult(item, searchQuery))];
    }

    public async Task SaveSearchContent(
        SearchContent searchContent,
        Dictionary<string, string> siteSettings)
    {
        ArgumentNullException.ThrowIfNull(searchContent);
        ArgumentNullException.ThrowIfNull(siteSettings);

        if (searchContent.IsDeleted)
        {
            await _searchContentRepository.DeleteSearchContentAsync(
                searchContent.EntityName,
                searchContent.EntityId).ConfigureAwait(false);

            return;
        }

        // Parse and clean before deleting the old entry. If parsing fails, the
        // previously indexed entry remains available instead of being lost.
        CleanSearchContent(searchContent);

        await _searchContentRepository.DeleteSearchContentAsync(
            searchContent.EntityName,
            searchContent.EntityId).ConfigureAwait(false);

        await _searchContentRepository.AddSearchContentAsync(searchContent).ConfigureAwait(false);
        await AnalyzeSearchContentAsync(searchContent, siteSettings).ConfigureAwait(false);
    }

    public async Task DeleteSearchContent(int siteId)
    {
        await _searchContentRepository.DeleteAllSearchContentAsync(siteId).ConfigureAwait(false);
    }

    private static SearchResult ConvertToSearchResult(
        SearchContent searchContent,
        SearchQuery searchQuery) => new()
    {
        SearchContentId = searchContent.SearchContentId,
        SiteId = searchContent.SiteId,
        EntityName = searchContent.EntityName,
        EntityId = searchContent.EntityId,
        Title = searchContent.Title,
        Description = searchContent.Description,
        Body = searchContent.Body,
        Url = Utilities.TenantUrl(searchQuery.Alias, searchContent.Url),
        Permissions = searchContent.Permissions,
        ContentModifiedBy = searchContent.ContentModifiedBy,
        ContentModifiedOn = searchContent.ContentModifiedOn,
        SearchContentProperties = searchContent.SearchContentProperties,
        Snippet = BuildSnippet(searchContent, searchQuery),
        Score = searchContent.Count / 100f
    };

    private static string BuildSnippet(
        SearchContent searchContent,
        SearchQuery searchQuery)
    {
        if (searchQuery.BodyLength <= 0)
        {
            return string.Empty;
        }

        var content = string.Concat(
            searchContent.Title,
            " ",
            searchContent.Description,
            " ",
            searchContent.Body);

        // This produces the same visible text the old raw-HTML snippet produced,
        // while allowing us to safely HTML-encode all untrusted text below.
        var displayContent = WebUtility.HtmlDecode(content) ?? string.Empty;
        if (displayContent.Length == 0)
        {
            return string.Empty;
        }

        var keywords = GetKeywords(searchQuery.Keywords);

        var start = 0;
        var matchedKeyword = false;

        foreach (var keyword in keywords)
        {
            var keywordIndex = displayContent.IndexOf(
                keyword,
                StringComparison.OrdinalIgnoreCase);

            if (keywordIndex < 0)
            {
                continue;
            }

            start = Math.Max(0, keywordIndex - SnippetLeadingContextLength);
            matchedKeyword = true;
            break;
        }

        var availableLength = displayContent.Length - start;
        var snippetLength = Math.Min(searchQuery.BodyLength, availableLength);
        var snippetText = displayContent.Substring(start, snippetLength);

        var prefix = matchedKeyword && start > 0 ? "..." : string.Empty;
        var suffix = matchedKeyword && snippetLength < availableLength
            ? "..."
            : string.Empty;

        return string.Concat(
            prefix,
            HighlightAndEncode(snippetText, keywords),
            suffix);
    }

    private static string[] GetKeywords(string? keywords)
    {
        var parsedKeywords = SearchUtils.GetKeywords(keywords ?? string.Empty);

        if (parsedKeywords is null)
        {
            return [];
        }

        return
        [
            .. parsedKeywords
                .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(static keyword => keyword.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static string HighlightAndEncode(
        string text,
        IReadOnlyList<string> keywords)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (keywords.Count == 0)
        {
            return WebUtility.HtmlEncode(text) ?? string.Empty;
        }

        var result = new StringBuilder(text.Length + 32);
        var position = 0;

        while (position < text.Length)
        {
            var nextMatchIndex = -1;
            var nextMatchLength = 0;
            var nextKeywordOrder = int.MaxValue;

            for (var keywordIndex = 0; keywordIndex < keywords.Count; keywordIndex++)
            {
                var keyword = keywords[keywordIndex];
                var matchIndex = text.IndexOf(
                    keyword,
                    position,
                    StringComparison.OrdinalIgnoreCase);

                if (matchIndex < 0)
                {
                    continue;
                }

                if (nextMatchIndex < 0 ||
                    matchIndex < nextMatchIndex ||
                    (matchIndex == nextMatchIndex && keywordIndex < nextKeywordOrder))
                {
                    nextMatchIndex = matchIndex;
                    nextMatchLength = keyword.Length;
                    nextKeywordOrder = keywordIndex;
                }
            }

            if (nextMatchIndex < 0)
            {
                break;
            }

            AppendHtmlEncoded(
                result,
                text,
                position,
                nextMatchIndex - position);

            result.Append("<b>");
            AppendHtmlEncoded(
                result,
                text,
                nextMatchIndex,
                nextMatchLength);
            result.Append("</b>");

            position = nextMatchIndex + nextMatchLength;
        }

        AppendHtmlEncoded(result, text, position, text.Length - position);
        return result.ToString();
    }

    private static void AppendHtmlEncoded(
        StringBuilder target,
        string value,
        int start,
        int length)
    {
        if (length <= 0)
        {
            return;
        }

        target.Append(WebUtility.HtmlEncode(value.Substring(start, length)));
    }

    private static void CleanSearchContent(SearchContent searchContent)
    {
        searchContent.Title = GetCleanContent(searchContent.Title);
        searchContent.Description = GetCleanContent(searchContent.Description);
        searchContent.Body = GetCleanContent(searchContent.Body);
        searchContent.AdditionalContent = GetCleanContent(searchContent.AdditionalContent);
    }

    private static string GetCleanContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        // Preserve the original pipeline: decode one layer before parsing so
        // encoded markup such as &lt;b&gt; is treated as markup and removed.
        var decodedContent = WebUtility.HtmlDecode(content) ?? string.Empty;
        var document = new HtmlParser(CompatibilityParserOptions)
            .ParseDocument(decodedContent);

        var phrases = EnumerateTextNodes(document)
            .Where(static text =>
            {
                var parentName = text.ParentElement?.LocalName;

                return parentName is not "script" and not "style" &&
                       !string.IsNullOrWhiteSpace(text.Data);
            })
            // Do not trim here: the legacy implementation filtered using Trim(),
            // but returned the original InnerText including its whitespace.
            .Select(static text => text.Data);

        return string.Join(" ", phrases);
    }

    private static IEnumerable<IText> EnumerateTextNodes(INode root)
    {
        var stack = new Stack<INode>();
        PushChildrenInReverse(root, stack);

        while (stack.TryPop(out var node))
        {
            if (node is IText text)
            {
                yield return text;
                continue;
            }

            // HtmlAgilityPack treated template contents as ordinary descendants.
            // In an HTML5 DOM, template contents live in a document fragment.
            if (node is IHtmlTemplateElement template)
            {
                PushChildrenInReverse(template.Content, stack);
            }
            else
            {
                PushChildrenInReverse(node, stack);
            }
        }
    }

    private static void PushChildrenInReverse(INode parent, Stack<INode> stack)
    {
        var children = parent.ChildNodes;

        for (var index = children.Length - 1; index >= 0; index--)
        {
            stack.Push(children[index]);
        }
    }

    private async Task AnalyzeSearchContentAsync(
        SearchContent searchContent,
        IReadOnlyDictionary<string, string> siteSettings)
    {
        var ignoreWords = GetIgnoreWords(siteSettings);
        var minimumWordLength = GetMinimumWordLength(siteSettings);

        var indexContent = string.Concat(
            searchContent.Title,
            " ",
            searchContent.Description,
            " ",
            searchContent.Body,
            " ",
            searchContent.AdditionalContent);

        var words = GetWords(indexContent, ignoreWords, minimumWordLength);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var existingByWord = new Dictionary<string, SearchContentWord>(
            StringComparer.Ordinal);

        foreach (var existing in await _searchContentRepository
                     .GetSearchContentWordsAsync(searchContent.SearchContentId)
                     .ConfigureAwait(false))
        {
            var existingWord = existing.SearchWord?.Word;

            if (!string.IsNullOrEmpty(existingWord))
            {
                existingByWord.TryAdd(existingWord, existing);
            }
        }

        foreach (var (word, count) in words)
        {
            if (existingByWord.TryGetValue(word, out var existingSearchContentWord))
            {
                existingSearchContentWord.Count = count;
                existingSearchContentWord.ModifiedOn = now;
                await _searchContentRepository
                    .UpdateSearchContentWordAsync(existingSearchContentWord)
                    .ConfigureAwait(false);
                continue;
            }

            var searchWord = await _searchContentRepository.GetSearchWordAsync(word).ConfigureAwait(false) ??
                             await _searchContentRepository.AddSearchWordAsync(new SearchWord
                             {
                                 Word = word,
                                 CreatedOn = now
                             }).ConfigureAwait(false);

            await _searchContentRepository.AddSearchContentWordAsync(new SearchContentWord
            {
                SearchContentId = searchContent.SearchContentId,
                SearchWordId = searchWord.SearchWordId,
                Count = count,
                CreatedOn = now,
                ModifiedOn = now
            }).ConfigureAwait(false);
        }
    }

    private static IReadOnlySet<string> GetIgnoreWords(
        IReadOnlyDictionary<string, string> siteSettings)
    {
        if (!siteSettings.TryGetValue(SearchIgnoreWordsSetting, out var configured) ||
            string.IsNullOrWhiteSpace(configured))
        {
            return DefaultIgnoreWords;
        }

        return configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int GetMinimumWordLength(
        IReadOnlyDictionary<string, string> siteSettings)
    {
        if (!TryGetMinimumWordLengthSetting(siteSettings, out var configured) ||
            !int.TryParse(
                configured,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            return DefaultMinimumWordLength;
        }

        return parsed;
    }

    private static bool TryGetMinimumWordLengthSetting(
        IReadOnlyDictionary<string, string> siteSettings,
        out string value)
    {
        if (siteSettings.TryGetValue(SearchMinimumWordLengthSetting, out value!) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return siteSettings.TryGetValue(LegacySearchMinimumWordLengthSetting, out value!) &&
               !string.IsNullOrWhiteSpace(value);
    }

    private static Dictionary<string, int> GetWords(
        string content,
        IReadOnlySet<string> ignoreWords,
        int minimumWordLength)
    {
        var formattedContent = FormatContent(content);
        var words = new Dictionary<string, int>(StringComparer.Ordinal);

        // Split only on ordinary spaces to preserve the legacy index-key behavior.
        // RemoveEmptyEntries + TrimEntries produces the same useful terms without
        // iterating over empty tokens.
        foreach (var term in formattedContent.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var word = term.ToLower(CultureInfo.CurrentCulture);

            if (word.Length < minimumWordLength || ignoreWords.Contains(word))
            {
                continue;
            }

            words[word] = words.TryGetValue(word, out var count)
                ? count + 1
                : 1;
        }

        return words;
    }

    private static string FormatContent(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Replaces HtmlEntity.DeEntitize with the Unicode-safe built-in decoder.
        var decodedText = WebUtility.HtmlDecode(text) ?? string.Empty;

        if (decodedText.AsSpan().IndexOfAny(IndexPunctuation) < 0)
        {
            return decodedText;
        }

        return string.Create(
            decodedText.Length,
            decodedText,
            static (destination, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var character = source[index];
                    destination[index] = IndexPunctuation.Contains(character)
                        ? ' '
                        : character;
                }
            });
    }
}
