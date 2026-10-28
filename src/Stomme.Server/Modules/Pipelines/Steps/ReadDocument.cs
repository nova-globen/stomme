#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// BL-229 - n8n's Extract from File, for documents: the text of a PDF (PdfPig, Apache-2.0) or a Word document (.docx, the
    /// OpenXml SDK the spreadsheets already use) in the site's pipeline folder - all of it, or chosen pages of a PDF, or page by
    /// page as a list - so an invoice saved by an HTTP request reaches Extract information or Ask AI as text. A scanned PDF holds
    /// pictures, not text: its pages come back empty, which the step says. Bounded by <c>Pipelines:Files:MaxFileBytes</c>.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Read the text of a PDF or a Word document (.docx) from the site's pipeline folder - all of it, chosen pages, or page by page.", DisplayName = "Read document")]
    public class ReadDocument : DataStep
    {
        /// <summary>The most pages a PDF is read for.</summary>
        public const int MaxPages = 2000;

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The document's path, relative to the site's pipeline folder: a .pdf or a .docx.", Order = 1)]
        public Input<string> Path { get; set; } = null!;

        [Input(Description = "A PDF's pages to read: '1-3, 5'. Empty: every page.", Order = 2)]
        public Input<string?>? Pages { get; set; }

        [Input(DisplayName = "Page by page", Description = "The result as a list of {page, text} - one record per page - instead of one text.", Order = 3)]
        public Input<bool> ByPage { get; set; } = new(false);

        [Output(Description = "How many pages the document has (a Word document: 1).")]
        public Output<int>? PageCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var relative = Required(context, Path, "Path");
            var full = PipelineFileSandbox.Resolve(context, relative, out var refusal);
            if (full is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(full))
            {
                await FailAsync(context, "FILE_NOT_FOUND", $"No file at '{relative}'.").ConfigureAwait(false);
                return;
            }

            var max = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;
            if (new FileInfo(full).Length > max)
            {
                await FailAsync(context, "FILE_TOO_LARGE", $"'{relative}' is larger than Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(full, context.CancellationToken).ConfigureAwait(false);
            List<(int Page, string Text)> pages;
            int count;
            try
            {
                switch (System.IO.Path.GetExtension(full).ToLowerInvariant())
                {
                    case ".pdf":
                        var wanted = ParsePages(Pages?.GetOrDefault(context), out var pagesError);
                        if (pagesError is not null)
                        {
                            await FailAsync(context, "DOCUMENT_PAGES_INVALID", pagesError).ConfigureAwait(false);
                            return;
                        }

                        (pages, count) = ReadPdf(bytes, wanted);
                        break;
                    case ".docx":
                        pages = new List<(int, string)> { (1, ReadWord(bytes)) };
                        count = 1;
                        break;
                    default:
                        await FailAsync(context, "DOCUMENT_UNSUPPORTED", $"'{relative}' is no PDF or Word document (.pdf, .docx).").ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync(context, "DOCUMENT_UNREADABLE", $"'{relative}' could not be read as a document: {ex.Message}").ConfigureAwait(false);
                return;
            }

            context.Set(PageCount, count);
            var characters = pages.Sum(page => page.Text.Length);
            context.AddExecutionLogEntry("Read", characters == 0
                ? $"Read {pages.Count} of {count} page(s) of '{relative}' and found no text - a scanned document holds pictures of its pages, not text."
                : $"Read {pages.Count} of {count} page(s) of '{relative}': {characters} character(s).");
            await SucceedAsync(context, ByPage.GetOrDefault(context)
                ? JsonSerializer.Serialize(pages.Select(page => new { page = page.Page, text = page.Text }))
                : string.Join("\n\n", pages.Select(page => page.Text).Where(text => text.Length > 0))).ConfigureAwait(false);
        }

        /// <summary>'1-3, 5' as page numbers, in order, each once; null for every page; the reason when it is not that.</summary>
        public static SortedSet<int>? ParsePages(string? text, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var pages = new SortedSet<int>();
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var range = part.Split('-', StringSplitOptions.TrimEntries);
                if (range.Length is < 1 or > 2
                    || !int.TryParse(range[0], out var from)
                    || !int.TryParse(range[^1], out var to)
                    || from < 1 || to < from || to - from >= MaxPages)
                {
                    error = $"'{part}' is no page or range of pages - write '1-3, 5'.";
                    return null;
                }

                for (var page = from; page <= to; page++)
                {
                    pages.Add(page);
                }
            }

            return pages;
        }

        private static (List<(int Page, string Text)> Pages, int Count) ReadPdf(byte[] bytes, SortedSet<int>? wanted)
        {
            using var document = PdfDocument.Open(bytes);
            var count = document.NumberOfPages;
            var pages = new List<(int, string)>();
            foreach (var number in (wanted ?? new SortedSet<int>(Enumerable.Range(1, Math.Min(count, MaxPages)))).Where(number => number <= count))
            {
                // The extractor breaks lines the operating system's way; a pipeline reads the same text on every host.
                pages.Add((number, ContentOrderTextExtractor.GetText(document.GetPage(number)).ReplaceLineEndings("\n").Trim()));
            }

            return (pages, count);
        }

        private static string ReadWord(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var document = WordprocessingDocument.Open(stream, false);
            var body = document.MainDocumentPart?.Document?.Body;
            return body is null ? "" : string.Join("\n", body.Descendants<Paragraph>().Select(paragraph => paragraph.InnerText)).Trim();
        }
    }
}
