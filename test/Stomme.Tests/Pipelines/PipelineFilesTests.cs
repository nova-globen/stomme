#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Audit;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - the site's pipeline folder as the Files page sees it: the folder the file steps read and write, listed a folder
    /// at a time, a file taken in whole or not at all, a folder made, a file or a folder deleted, a file given back - each site in
    /// a folder of its own, every path that leaves it refused by the steps' own rule.
    /// </summary>
    public sealed class PipelineFilesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-pipeline-files-" + Guid.NewGuid().ToString("N"));
        private readonly DefaultTenantAccessor _tenants = new();
        private readonly IOptions<PipelineStepOptions> _options;

        public PipelineFilesTests()
        {
            var options = new PipelineStepOptions();
            options.Files.Root = _root;
            options.Files.MaxFileBytes = 64;
            _options = Options.Create(options);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private IDisposable Site(string id) => _tenants.PushContext(new Tenant { Id = id, Name = id });

        private Task<PipelineFolderView?> ListAsync(string? folder) => new ListPipelineFilesQueryHandler(_options, _tenants).Handle(new ListPipelineFilesQuery(folder), CancellationToken.None);

        private Task<PipelineFileEntryView> UploadAsync(string? folder, string? name, string content) =>
            new UploadPipelineFileCommandHandler(_options, _tenants).Handle(new UploadPipelineFileCommand(folder, name, new MemoryStream(Encoding.UTF8.GetBytes(content))), CancellationToken.None);

        private Task<PipelineFileEntryView> MakeFolderAsync(string? path) => new CreatePipelineFolderCommandHandler(_options, _tenants).Handle(new CreatePipelineFolderCommand(path), CancellationToken.None);

        private Task<bool> DeleteAsync(string? path) => new DeletePipelineFileCommandHandler(_options, _tenants).Handle(new DeletePipelineFileCommand(path), CancellationToken.None);

        private Task<PipelineFileContent?> FileAsync(string? path) => new GetPipelineFileQueryHandler(_options, _tenants).Handle(new GetPipelineFileQuery(path), CancellationToken.None);

        [Fact]
        public async Task A_folder_lists_its_folders_then_its_files_and_one_not_there_is_none()
        {
            using var site = Site("site-a");
            var empty = (await ListAsync(""))!; // the site's folder before any file: there, and empty
            Assert.Equal(("", 0, 64L), (empty.Folder, empty.Entries.Count, empty.MaxFileBytes));

            await MakeFolderAsync("reports");
            await UploadAsync("", "b.csv", "x,y");
            await UploadAsync(null, "A.txt", "hello");
            await MakeFolderAsync("reports/2026");
            await UploadAsync("reports", "q3.csv", "1,2");

            var top = (await ListAsync(null))!;
            Assert.Equal(new[] { ("reports", "reports", true, 0L), ("A.txt", "A.txt", false, 5L), ("b.csv", "b.csv", false, 3L) },
                top.Entries.Select(entry => (entry.Name, entry.Path, entry.IsFolder, entry.Length)));
            var reports = (await ListAsync("/reports/"))!; // a path as a person might type it
            Assert.Equal("reports", reports.Folder);
            Assert.Equal(new[] { "reports/2026", "reports/q3.csv" }, reports.Entries.Select(entry => entry.Path));
            Assert.Null(await ListAsync("nope"));
            Assert.Null(await ListAsync("b.csv")); // a file is not a folder
        }

        [Fact]
        public async Task An_upload_is_refused_saying_why_and_leaves_nothing_behind()
        {
            using var site = Site("site-a");
            await UploadAsync("", "kept.csv", "a");

            var taken = await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync("", "kept.csv", "b"));
            Assert.Equal("'kept.csv' is already in this folder - delete it first, or upload the file under another name.", taken.Message);
            Assert.Equal("a", File.ReadAllText((await FileAsync("kept.csv"))!.FullPath)); // not replaced

            var large = await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync("", "big.bin", new string('x', 65)));
            Assert.Equal("'big.bin' is larger than the 64 bytes a pipeline file may be.", large.Message);
            Assert.Null(await FileAsync("big.bin"));
            await UploadAsync("", "edge.bin", new string('x', 64)); // exactly the limit is a file

            foreach (var (name, why) in new[] { ("", "A name is needed."), ("..", "'..' cannot be a name."), ("con.txt", "device name"), ("a?.csv", "character a file name cannot"), ("ends.", "ends with a dot") })
            {
                Assert.Contains(why, (await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync("", name, "x"))).Message);
            }

            Assert.Equal("There is no folder 'nope' - make it first.", (await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync("nope", "a.csv", "x"))).Message);
            Assert.Contains("resolves outside the site's folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync("../site-b", "a.csv", "x"))).Message);

            // A browser that sends a path sends a name: the file lands in the folder chosen, under its own name.
            Assert.Equal("named.csv", (await UploadAsync("", @"C:\Users\someone\named.csv", "x")).Path);

            Assert.Equal(new[] { "edge.bin", "kept.csv", "named.csv" }, (await ListAsync(""))!.Entries.Select(entry => entry.Name));
            Assert.Empty(Directory.EnumerateFileSystemEntries(PipelineSiteFiles.Incoming(_options.Value))); // no part file left by a refusal
        }

        [Fact]
        public async Task Each_site_sees_its_own_folder_the_one_its_file_steps_read()
        {
            using (Site("site-a"))
            {
                await UploadAsync("", "orders.csv", "a");
                var file = (await FileAsync("orders.csv"))!;
                Assert.Equal(Path.Combine(PipelineFileSandbox.SiteRoot(_options.Value, "site-a"), "orders.csv"), file.FullPath);
                Assert.Equal(("orders.csv", 1L), (file.Name, file.Length));
            }

            using (Site("site-b"))
            {
                Assert.Empty((await ListAsync(""))!.Entries);
                Assert.Null(await FileAsync("orders.csv"));
                Assert.Contains("resolves outside the site's folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => FileAsync("../site-a/orders.csv"))).Message);
                Assert.False(await DeleteAsync("orders.csv"));
            }

            using (Site("site-a"))
            {
                Assert.NotNull(await FileAsync("orders.csv")); // site B's delete touched nothing of site A's
            }
        }

        [Fact]
        public async Task A_file_or_a_folder_with_what_is_in_it_is_deleted_and_the_site_folder_is_not()
        {
            using var site = Site("site-a");
            await MakeFolderAsync("reports");
            await MakeFolderAsync("reports/2026");
            await UploadAsync("reports/2026", "q3.csv", "x");
            await UploadAsync("", "keep.txt", "x");

            Assert.True(await DeleteAsync("reports"));
            Assert.Null(await ListAsync("reports"));
            Assert.True(await DeleteAsync("keep.txt"));
            Assert.False(await DeleteAsync("keep.txt")); // not there any more
            Assert.Equal("The site's folder itself is not deleted - delete what is in it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteAsync("/"))).Message);
            Assert.Contains("resolves outside the site's folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteAsync(".."))).Message);
            Assert.True(Directory.Exists(PipelineFileSandbox.SiteRoot(_options.Value, "site-a")));
        }

        [Fact]
        public async Task A_folder_is_made_once_inside_one_that_is_there()
        {
            using var site = Site("site-a");
            Assert.Equal(("2026", "reports/2026", true), await MakeAsync("reports/2026", firstMaking: "reports"));
            Assert.Equal("'2026' is already in this folder.", (await Assert.ThrowsAsync<InvalidOperationException>(() => MakeFolderAsync("reports/2026"))).Message);
            Assert.Equal("There is no folder 'nope' to make 'x' in.", (await Assert.ThrowsAsync<InvalidOperationException>(() => MakeFolderAsync("nope/x"))).Message);
            Assert.Equal("A name is needed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => MakeFolderAsync(""))).Message);
            Assert.Contains("resolves outside the site's folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => MakeFolderAsync("../escape"))).Message);
            Assert.False(Directory.Exists(Path.Combine(_root, "escape")));

            async Task<(string, string, bool)> MakeAsync(string path, string firstMaking)
            {
                await MakeFolderAsync(firstMaking);
                var made = await MakeFolderAsync(path);
                return (made.Name, made.Path, made.IsFolder);
            }
        }

        // The activity ledger names a file by its path in the site's folder - never its content.
        [Fact]
        public void The_ledger_says_which_file_and_nothing_of_what_is_in_it()
        {
            var before = PipelineAuditBefore.Nothing;
            Assert.Equal(new PipelineAuditFact("uploaded a file", Detail: "'reports/q3.csv'"),
                PipelineAudit.Describe(new UploadPipelineFileCommand("reports", "q3.csv", Stream.Null), new PipelineFileEntryView { Path = "reports/q3.csv" }, before));
            Assert.Equal(new PipelineAuditFact("made a folder of files", Detail: "'reports'"),
                PipelineAudit.Describe(new CreatePipelineFolderCommand("reports"), new PipelineFileEntryView { Path = "reports", IsFolder = true }, before));
            Assert.Equal(new PipelineAuditFact("deleted a file", Detail: "'reports/q3.csv'"), PipelineAudit.Describe(new DeletePipelineFileCommand("/reports/q3.csv"), true, before));
            Assert.Null(PipelineAudit.Describe(new DeletePipelineFileCommand("gone.csv"), false, before)); // nothing was there
        }

        // Every step input that takes a path in the site's pipeline folder offers the folder to choose from in the editor: one
        // added without the hint would be the only file input an author must type blind. A path is text: a switch that SPEAKS of
        // the folder ("Keep attachments" - each kept in the pipeline folder) takes no path, so only text inputs are held to it.
        [Fact]
        public void Every_input_that_takes_a_pipeline_folder_path_is_marked_to_be_chosen_from_it()
        {
            var inputs = typeof(PipelineFileSandbox).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(PipelineFileSandbox).Namespace && !type.IsAbstract)
                .SelectMany(type => type.GetProperties().Select(property => (Type: type, Property: property,
                    Input: (Stomme.Engine.Pipelines.Attributes.InputAttribute?)Attribute.GetCustomAttribute(property, typeof(Stomme.Engine.Pipelines.Attributes.InputAttribute)))))
                .Where(entry => entry.Input?.Description?.Contains("pipeline folder", StringComparison.Ordinal) == true)
                .Where(entry => entry.Property.PropertyType == typeof(Stomme.Engine.Pipelines.Models.Input<string>))
                .ToList();

            Assert.True(inputs.Count >= 11, $"vacuous: only {inputs.Count} pipeline-folder inputs found");
            Assert.Empty(inputs.Where(entry => entry.Input!.UIHint != PipelineInputHints.PipelineFile).Select(entry => entry.Type.Name + "." + entry.Property.Name));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("\\reports\\\\2026/", "reports/2026")]
        [InlineData(" reports / q3.csv ", "reports/q3.csv")]
        public void A_path_is_spoken_relative_and_slash_separated(string path, string expected)
        {
            Assert.Equal(expected, PipelineFilePaths.Normalize(path));
            Assert.Equal(new[] { ("reports", "reports"), ("2026", "reports/2026") }, PipelineFilePaths.Trail("reports/2026"));
            Assert.Equal(("reports", "2026", "reports/2026/q3.csv"), (PipelineFilePaths.Parent("reports/2026"), PipelineFilePaths.Name("reports/2026"), PipelineFilePaths.Combine("reports/2026", "q3.csv")));
            Assert.Equal(new[] { "512 bytes", "1.5 KB", "64 MB" }, new[] { 512L, 1536L, 64L * 1024 * 1024 }.Select(PipelineFilePaths.Size));
        }
    }
}
