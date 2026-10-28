#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// BL-94 increment 13 (D110 refutation 6) - a source scan: nothing under the Datasets module reads a whole upload into
    /// memory (the predecessor platform's parser did <c>reader.ReadToEnd()</c> and the file-manager source buffered every byte). The forms
    /// the gate refuses are the ones that materialise an unbounded input; both directions - the clean tree passes and a
    /// planted <c>ReadToEnd</c> is caught.
    /// </summary>
    public sealed class UploadNoWholeBufferTests
    {
        private static readonly Regex WholeBuffer = new(@"\.(ReadToEnd|ReadToEndAsync|ReadAllBytes|ReadAllBytesAsync|ReadAllText|ReadAllTextAsync)\s*\(|new MemoryStream\(\)\s*;\s*\r?\n?\s*await [^;]*CopyToAsync", RegexOptions.Compiled);

        [Fact]
        public void The_datasets_module_never_reads_a_whole_upload_into_memory()
        {
            var root = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "Modules", "Datasets");
            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.True(files.Length >= 8, "vacuous: the Datasets module was not found");

            var hits = files.SelectMany(file => Violations(File.ReadAllText(file)).Select(hit => Path.GetFileName(file) + ": " + hit)).ToList();
            Assert.Empty(hits);
        }

        [Fact]
        public void A_planted_whole_buffer_read_is_caught()
        {
            Assert.Single(Violations("using var reader = new StreamReader(content);\nvar text = reader.ReadToEnd();"));
            Assert.Single(Violations("var bytes = File.ReadAllBytes(path);"));
            Assert.Single(Violations("var text = await reader.ReadToEndAsync(ct);"));
            Assert.Empty(Violations("var n = content.Read(buffer, read, buffer.Length - read);"));
        }

        private static IReadOnlyList<string> Violations(string source) => WholeBuffer.Matches(source).Select(match => match.Value.Trim()).ToList();

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
        }
    }
}
