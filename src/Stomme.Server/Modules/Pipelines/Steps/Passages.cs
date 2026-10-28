#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// BL-229 - the retrieval half of "answer from your documents" (n8n's document loader, text splitter and vector store
    /// retriever, by keyword relevance until the site's model offers embeddings): a text split into passages, kept as rows of a
    /// data table - one per passage, its source and number beside it - and the passages that best answer a question found again
    /// by BM25 over their words. One place for both steps' rules.
    /// </summary>
    public static class Passages
    {
        public const string SourceColumn = "source";
        public const string NumberColumn = "passage";
        public const string TextColumn = "text";
        public const int MinLength = 200;
        public const int MaxLength = 4000;
        public const int MaxTop = 20;

        /// <summary>
        /// The text in passages of about <paramref name="length"/> characters: paragraphs kept whole where they fit, a longer one cut
        /// between sentences (or words), each passage after the first beginning with the last <paramref name="overlap"/> characters of
        /// the one before - so a sentence on a boundary is in both.
        /// </summary>
        public static List<string> Split(string? text, int length, int overlap)
        {
            length = Math.Clamp(length, MinLength, MaxLength);
            overlap = Math.Clamp(overlap, 0, length / 2);
            var pieces = new List<string>();
            foreach (var paragraph in (text ?? "").ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                pieces.AddRange(paragraph.Length <= length ? new[] { paragraph } : Cut(paragraph, length));
            }

            var passages = new List<string>();
            var current = new StringBuilder();
            foreach (var piece in pieces)
            {
                if (current.Length > 0 && current.Length + 2 + piece.Length > length)
                {
                    passages.Add(current.ToString());
                    current.Clear().Append(Tail(passages[^1], overlap));
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(piece);
            }

            if (current.Length > 0 && (passages.Count == 0 || current.ToString() != Tail(passages[^1], overlap)))
            {
                passages.Add(current.ToString());
            }

            return passages;
        }

        // A paragraph longer than a passage, cut between sentences - or, a sentence too long, between words.
        private static IEnumerable<string> Cut(string paragraph, int length)
        {
            var piece = new StringBuilder();
            foreach (var sentence in Sentences(paragraph))
            {
                foreach (var part in sentence.Length <= length ? new[] { sentence } : WordPieces(sentence, length))
                {
                    if (piece.Length > 0 && piece.Length + 1 + part.Length > length)
                    {
                        yield return piece.ToString();
                        piece.Clear();
                    }

                    piece.Append(piece.Length > 0 ? " " : "").Append(part);
                }
            }

            if (piece.Length > 0)
            {
                yield return piece.ToString();
            }
        }

        private static IEnumerable<string> Sentences(string text)
        {
            var start = 0;
            for (var index = 0; index < text.Length - 1; index++)
            {
                if (text[index] is '.' or '?' or '!' && char.IsWhiteSpace(text[index + 1]))
                {
                    yield return text[start..(index + 1)].Trim();
                    start = index + 1;
                }
            }

            if (start < text.Length && text[start..].Trim() is { Length: > 0 } rest)
            {
                yield return rest;
            }
        }

        private static IEnumerable<string> WordPieces(string sentence, int length)
        {
            var piece = new StringBuilder();
            foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length > 0 && piece.Length + 1 + word.Length > length)
                {
                    yield return piece.ToString();
                    piece.Clear();
                }

                piece.Append(piece.Length > 0 ? " " : "").Append(word.Length > length ? word[..length] : word);
            }

            if (piece.Length > 0)
            {
                yield return piece.ToString();
            }
        }

        // The end of a passage to carry into the next, from a word's start.
        private static string Tail(string passage, int overlap)
        {
            if (overlap == 0 || passage.Length <= overlap)
            {
                return overlap == 0 ? "" : passage;
            }

            var tail = passage[^overlap..];
            var space = tail.IndexOf(' ');
            return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
        }

        /// <summary>A text's words as BM25 counts them: letters and digits, lower case, any language.</summary>
        public static List<string> Words(string? text)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            foreach (var character in (text ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    word.Append(character);
                }
                else if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
            }

            return words;
        }

        /// <summary>
        /// The passages that answer the question best, best first - BM25 (k1 1.2, b 0.75) over their words, a passage holding none
        /// of the question's words left out - with each one's score.
        /// </summary>
        public static List<(int Index, decimal Score)> Rank(string? question, IReadOnlyList<string> passages, int top)
        {
            var asked = Words(question).Distinct(StringComparer.Ordinal).ToList();
            var documents = passages.Select(passage => Words(passage)).ToList();
            if (asked.Count == 0 || documents.Count == 0)
            {
                return new List<(int, decimal)>();
            }

            var average = documents.Average(words => (double)words.Count);
            var holding = asked.ToDictionary(term => term, term => documents.Count(words => words.Contains(term)), StringComparer.Ordinal);
            var ranked = new List<(int, decimal)>();
            for (var index = 0; index < documents.Count; index++)
            {
                var words = documents[index];
                var counts = words.GroupBy(word => word, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var score = 0d;
                foreach (var term in asked)
                {
                    if (!counts.TryGetValue(term, out var frequency))
                    {
                        continue;
                    }

                    var idf = Math.Log(1 + (documents.Count - holding[term] + 0.5) / (holding[term] + 0.5));
                    score += idf * frequency * 2.2 / (frequency + 1.2 * (0.25 + 0.75 * words.Count / Math.Max(1, average)));
                }

                if (score > 0)
                {
                    ranked.Add((index, Math.Round((decimal)score, 4)));
                }
            }

            return ranked.OrderByDescending(found => found.Item2).ThenBy(found => found.Item1).Take(Math.Clamp(top, 1, MaxTop)).ToList();
        }
    }

    /// <summary>
    /// BL-229 - Store passages: a text (a document Read document read, say) split into passages and kept as rows of a data table -
    /// its source, the passage's number and its text - the table made when the site has none of that name. Storing a source again
    /// replaces its passages, so a document read anew is not kept twice. The passages written are the result.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "AI", "Split a text into passages and keep them in a data table, for Find passages to search - storing a source again replaces its passages.", DisplayName = "Store passages")]
    public class StorePassages : DataStep
    {
        [Input(UIHint = PipelineInputHints.SiteTablePicker, Description = "The data table the passages are kept in - made when the site has none of that name.", DefaultValue = "Passages", Order = 1)]
        public Input<string> Table { get; set; } = new("Passages");

        [Input(UIHint = InputUIHints.MultiLine, Description = "The text to split. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Text { get; set; }

        [Input(Description = "What the text is - a file's name, an address: its passages are found under it and replaced when it is stored again.", Order = 3)]
        public Input<string> Source { get; set; } = null!;

        [Input(DisplayName = "Passage length", Description = "About how many characters a passage holds (200 to 4,000).", DefaultValue = 800, Order = 4)]
        public Input<int> Length { get; set; } = new(800);

        [Input(Description = "How many characters of a passage's end the next one begins with, so a sentence on the boundary is in both.", DefaultValue = 100, Order = 5)]
        public Input<int> Overlap { get; set; } = new(100);

        [Output(Description = "How many passages it kept.")]
        public Output<int>? Count { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var keyValues = context.GetService<IKeyValueStore>()
                ?? throw new FaultException("DATA_TABLES_UNAVAILABLE", "Pipelines", "Configuration", "The engine's key-value store is not composed in this host.");
            var tenantId = context.GetService<ITenantAccessor>()?.TenantId;
            var name = Required(context, Table, "Table").Trim();
            var source = Required(context, Source, "Source").Trim();
            var passages = Passages.Split(TextOrLastResult(context, Text), context.Get(Length), context.Get(Overlap));
            var now = context.GetService<ISystemClock>()?.UtcNow ?? DateTimeOffset.UtcNow;

            StoredSiteTable? table;
            try
            {
                table = await PipelineSiteTableStore.FindAsync(keyValues, tenantId, name, context.CancellationToken).ConfigureAwait(false)
                    ?? await PipelineSiteTableStore.CreateAsync(keyValues, tenantId, new PipelineSiteTableRequest
                    {
                        Name = name,
                        Columns =
                        {
                            new PipelineTableColumn { Name = Passages.SourceColumn, Type = PipelineTableColumnType.Text },
                            new PipelineTableColumn { Name = Passages.NumberColumn, Type = PipelineTableColumnType.Number },
                            new PipelineTableColumn { Name = Passages.TextColumn, Type = PipelineTableColumnType.Text },
                        },
                    }, context.GetRequiredService<IIdentityGenerator>().GenerateId(), now, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await FailAsync(context, "PASSAGES_TABLE", $"The data table '{name}' could not be used for passages: {ex.Message}").ConfigureAwait(false);
                return;
            }

            if (table.Column(Passages.SourceColumn) is null || table.Column(Passages.TextColumn) is null || table.Column(Passages.NumberColumn) is null)
            {
                await FailAsync(context, "PASSAGES_TABLE", $"The data table '{table.Name}' is no passages table - it needs the columns {Passages.SourceColumn}, {Passages.NumberColumn} and {Passages.TextColumn}.").ConfigureAwait(false);
                return;
            }

            var rows = await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenantId, table.Id, context.CancellationToken).ConfigureAwait(false);
            var sourceName = table.Column(Passages.SourceColumn)!.Name;
            var earlier = rows.Where(row => string.Equals(PipelineSiteTables.Text(PipelineSiteTableStore.Project(table, row)[sourceName]), source, StringComparison.Ordinal)).ToList();
            if (rows.Count - earlier.Count + passages.Count > PipelineSiteTables.MaxRows)
            {
                await FailAsync(context, "PASSAGES_TOO_MANY", $"'{table.Name}' would hold more than {PipelineSiteTables.MaxRows} rows - split the documents over more tables.").ConfigureAwait(false);
                return;
            }

            foreach (var row in earlier)
            {
                await PipelineSiteTableStore.DeleteRowAsync(keyValues, tenantId, table.Id, PipelineSiteTableStore.IdOf(row), context.CancellationToken).ConfigureAwait(false);
            }

            var written = new JsonArray();
            for (var index = 0; index < passages.Count; index++)
            {
                var row = PipelineSiteTableStore.NewRow(PipelineSiteTableStore.NewRowId(now, index), now);
                var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    [Passages.SourceColumn] = source,
                    [Passages.NumberColumn] = index + 1,
                    [Passages.TextColumn] = passages[index],
                };
                if (!PipelineSiteTableStore.TrySet(table, row, values, now, out var error))
                {
                    await FailAsync(context, "PASSAGES_INVALID", error!).ConfigureAwait(false);
                    return;
                }

                await PipelineSiteTableStore.SaveRowAsync(keyValues, tenantId, table, row, context.CancellationToken).ConfigureAwait(false);
                written.Add(PipelineSiteTableStore.Project(table, row));
            }

            context.Set(Count, passages.Count);
            context.AddExecutionLogEntry("Stored", $"Kept {passages.Count} passage(s) of '{source}' in '{table.Name}'" + (earlier.Count > 0 ? $", in place of its {earlier.Count} earlier one(s)." : "."));
            await SucceedAsync(context, written.ToJsonString()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// BL-229 - Find passages: the passages of a data table Store passages filled that best answer a question - by BM25 over their
    /// words, best first, those holding none of the question's words left out - as records (source, passage, text, score), and as
    /// one Context text, numbered, to hand Ask AI with the question.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "AI", "Find the passages of a data table that best answer a question - by the words they share - for Ask AI to answer from.", DisplayName = "Find passages")]
    public class FindPassages : DataStep
    {
        [Input(UIHint = PipelineInputHints.SiteTablePicker, Description = "The data table Store passages keeps them in.", DefaultValue = "Passages", Order = 1)]
        public Input<string> Table { get; set; } = new("Passages");

        [Input(UIHint = InputUIHints.MultiLine, Description = "The question. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Question { get; set; }

        [Input(DisplayName = "How many", Description = "At most so many passages (1 to 20).", DefaultValue = 4, Order = 3)]
        public Input<int> Top { get; set; } = new(4);

        [Input(Description = "Only the passages of this source. Empty: every source.", Order = 4)]
        public Input<string?>? Source { get; set; }

        [Output(Description = "The passages found as one text, numbered, each with its source - for Ask AI's prompt.")]
        public Output<string>? Context { get; set; }

        [Output(Description = "How many passages it found.")]
        public Output<int>? Count { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var keyValues = context.GetService<IKeyValueStore>()
                ?? throw new FaultException("DATA_TABLES_UNAVAILABLE", "Pipelines", "Configuration", "The engine's key-value store is not composed in this host.");
            var tenantId = context.GetService<ITenantAccessor>()?.TenantId;
            var name = Required(context, Table, "Table");
            var question = TextOrLastResult(context, Question);
            if (string.IsNullOrWhiteSpace(question))
            {
                await FailAsync(context, "PASSAGES_NO_QUESTION", "There is no question to find passages for.").ConfigureAwait(false);
                return;
            }

            var table = await PipelineSiteTableStore.FindAsync(keyValues, tenantId, name, context.CancellationToken).ConfigureAwait(false);
            if (table?.Column(Passages.TextColumn) is not { } textColumn)
            {
                await FailAsync(context, "PASSAGES_TABLE", table is null
                    ? $"The site has no data table '{name}' - Store passages makes it."
                    : $"The data table '{table.Name}' has no '{Passages.TextColumn}' column - it is no passages table.").ConfigureAwait(false);
                return;
            }

            var only = Source?.GetOrDefault(context)?.Trim();
            var sourceColumn = table.Column(Passages.SourceColumn);
            var rows = (await PipelineSiteTableStore.ReadRowsAsync(keyValues, tenantId, table.Id, context.CancellationToken).ConfigureAwait(false))
                .Select(row => PipelineSiteTableStore.Project(table, row))
                .Where(row => string.IsNullOrEmpty(only) || (sourceColumn is not null && string.Equals(PipelineSiteTables.Text(row[sourceColumn.Name]), only, StringComparison.Ordinal)))
                .ToList();
            var ranked = Passages.Rank(question, rows.Select(row => PipelineSiteTables.Text(row[textColumn.Name])).ToList(), context.Get(Top));
            var found = new JsonArray();
            var text = new StringBuilder();
            foreach (var (index, score) in ranked)
            {
                var row = rows[index];
                var source = sourceColumn is null ? "" : PipelineSiteTables.Text(row[sourceColumn.Name]);
                var passage = PipelineSiteTables.Text(row[textColumn.Name]);
                found.Add(new JsonObject
                {
                    [Passages.SourceColumn] = source,
                    [Passages.NumberColumn] = row[Passages.NumberColumn]?.DeepClone(),
                    [Passages.TextColumn] = passage,
                    ["score"] = score,
                });
                text.Append(text.Length > 0 ? "\n\n" : "").Append('[').Append(found.Count.ToString(CultureInfo.InvariantCulture)).Append("] ")
                    .Append(source.Length > 0 ? "(" + source + ") " : "").Append(passage);
            }

            context.Set(Context, text.ToString());
            context.Set(Count, found.Count);
            context.AddExecutionLogEntry("Found", found.Count == 0
                ? $"No passage of '{table.Name}' shares a word with the question."
                : $"Found {found.Count} of {rows.Count} passage(s) of '{table.Name}'.");
            await SucceedAsync(context, found.ToJsonString()).ConfigureAwait(false);
        }
    }
}
