#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Steps;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's Extract from File, for documents: Read document gives the text of a PDF - every page, chosen pages, or page
    /// by page as records - or of a Word document; a page with no text (a scan) comes back empty and the log says why; a range that
    /// is none, a file of another kind and a file that is no document fail saying so.
    /// </summary>
    public sealed class ReadDocumentTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly PipelineStepOptions _options = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-read-document-" + Guid.NewGuid().ToString("N"));

        public async Task InitializeAsync()
        {
            _options.Files.Root = _root;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(_options));
            services.AddEngine(module => module.AddStepsFrom<PipelineStepsMarker>());
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private static IDictionary<string, object> OutputsOf(RunPipelineResult run) =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is ReadDocument).GetOutputs();

        // The site's folder as the steps resolve it: found by writing a file there through a step.
        private async Task<string> SiteFolderAsync()
        {
            await RunAsync(new WriteTextFile { Path = new("probe.txt"), Content = new("probe") });
            return Path.GetDirectoryName(Directory.GetFiles(_root, "probe.txt", SearchOption.AllDirectories).Single())!;
        }

        private static byte[] Invoice()
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Invoice 42 from Ada", 12, new PdfPoint(25, 700), font);
            var second = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            second.AddText("Total 12.50 EUR", 12, new PdfPoint(25, 700), font);
            second.AddText("Due 2026-11-01", 12, new PdfPoint(25, 680), font);
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4); // a page with no text - as a scanned page is to a reader of text
            return builder.Build();
        }

        private static byte[] Letter()
        {
            using var stream = new MemoryStream();
            using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
            {
                document.AddMainDocumentPart().Document = new Document(new Body(
                    new Paragraph(new Run(new Text("Dear Ada,"))),
                    new Paragraph(new Run(new Text("Your order 42 has shipped.")))));
            }

            return stream.ToArray();
        }

        [Fact]
        public async Task A_pdf_is_read_in_full_by_chosen_pages_or_page_by_page_and_a_word_document_too()
        {
            var folder = await SiteFolderAsync();
            Directory.CreateDirectory(Path.Combine(folder, "docs"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "docs", "invoice.pdf"), Invoice());
            await File.WriteAllBytesAsync(Path.Combine(folder, "docs", "letter.docx"), Letter());

            var all = await RunAsync(new ReadDocument { Path = new("docs/invoice.pdf") });
            Assert.Equal(PipelineSubStatus.Finished, all.PipelineState.SubStatus);
            Assert.Equal(("Invoice 42 from Ada\n\nTotal 12.50 EUR\nDue 2026-11-01", 3), ((string)OutputsOf(all)["Result"], (int)OutputsOf(all)["PageCount"]));
            Assert.Contains(all.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Read" && entry.Message == "Read 3 of 3 page(s) of 'docs/invoice.pdf': 49 character(s).");

            Assert.Equal("Total 12.50 EUR\nDue 2026-11-01", OutputsOf(await RunAsync(new ReadDocument { Path = new("docs/invoice.pdf"), Pages = new("2") }))["Result"]);
            Assert.Equal("""[{"page":1,"text":"Invoice 42 from Ada"},{"page":2,"text":"Total 12.50 EUR\nDue 2026-11-01"}]""",
                OutputsOf(await RunAsync(new ReadDocument { Path = new("docs/invoice.pdf"), Pages = new(" 1 - 2 "), ByPage = new(true) }))["Result"]);

            // A page with no text: empty, and the log says what that usually means.
            var scan = await RunAsync(new ReadDocument { Path = new("docs/invoice.pdf"), Pages = new("3") });
            Assert.Equal("", OutputsOf(scan)["Result"]);
            Assert.Contains(scan.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Read" && entry.Message!.EndsWith("a scanned document holds pictures of its pages, not text."));

            var letter = await RunAsync(new ReadDocument { Path = new("docs/letter.docx") });
            Assert.Equal(("Dear Ada,\nYour order 42 has shipped.", 1), ((string)OutputsOf(letter)["Result"], (int)OutputsOf(letter)["PageCount"]));
        }

        [Fact]
        public async Task A_range_that_is_none_a_file_of_another_kind_and_a_broken_document_fail_saying_so()
        {
            var folder = await SiteFolderAsync();
            await File.WriteAllBytesAsync(Path.Combine(folder, "invoice.pdf"), Invoice());
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "plain text");
            await File.WriteAllTextAsync(Path.Combine(folder, "broken.pdf"), "this is no PDF");

            async Task<string?> Error(string path, string? pages = null) =>
                (string?)OutputsOf(await RunAsync(new ReadDocument { Path = new(path), Pages = new(pages), OnFailure = new(FailurePolicy.Continue) }))["Error"];

            Assert.Equal("DOCUMENT_PAGES_INVALID: '3-1' is no page or range of pages - write '1-3, 5'.", await Error("invoice.pdf", "3-1"));
            Assert.Equal("DOCUMENT_UNSUPPORTED: 'notes.txt' is no PDF or Word document (.pdf, .docx).", await Error("notes.txt"));
            Assert.StartsWith("DOCUMENT_UNREADABLE: 'broken.pdf' could not be read as a document: ", await Error("broken.pdf"));
            Assert.Equal("FILE_NOT_FOUND: No file at 'missing.pdf'.", await Error("missing.pdf"));
            Assert.Equal(new[] { 1, 2, 3, 5 }, ReadDocument.ParsePages("1-3, 5, 2", out var error)!);
            Assert.Null(error);
        }
    }
}
