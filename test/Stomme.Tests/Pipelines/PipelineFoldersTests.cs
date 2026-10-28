#nullable enable
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's folders: the one rule for a pipeline's folder on both sides - names separated by '/', each tidy and short, at
    /// most four deep - and how the list reads the folders the pipelines are in: the ones inside the folder shown, counted, one
    /// whatever its case.
    /// </summary>
    public sealed class PipelineFoldersTests
    {
        [Theory]
        [InlineData(" Finance /  Invoices   2026 ", "Finance/Invoices 2026")]
        [InlineData("a//b/", "a/b")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("One/Two/Three/Four", "One/Two/Three/Four")]
        public void A_folder_is_made_tidy(string? typed, string expected)
        {
            Assert.Equal(expected, PipelineFolders.Normalize(typed, out var error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("One/Two/Three/Four/Five", "A folder is at most 4 deep.")]
        [InlineData("Finance/Q1|Q2", "'Q1|Q2' cannot be a folder's name: at most 40 letters, digits, spaces, '-', '_' or '.'.")]
        [InlineData("A folder name of well over forty characters", "'A folder name of well over forty characters' cannot be a folder's name: at most 40 letters, digits, spaces, '-', '_' or '.'.")]
        public void A_folder_that_cannot_be_one_is_refused_with_the_reason(string typed, string reason)
        {
            Assert.Equal("", PipelineFolders.Normalize(typed, out var error));
            Assert.Equal(reason, error);
        }

        [Fact]
        public void The_list_reads_the_folders_inside_the_one_shown_counted_and_one_whatever_its_case()
        {
            var folders = new[] { "Finance", "Finance/Invoices", "finance/invoices/Q1", "Ops", null, "" };

            Assert.Equal(new[] { ("Finance", "Finance", 3), ("Ops", "Ops", 1) }, PipelineFolders.Children(folders, ""));
            Assert.Equal(new[] { ("Finance/Invoices", "Invoices", 2) }, PipelineFolders.Children(folders, "FINANCE"));
            Assert.Equal(new[] { ("finance/invoices/Q1", "Q1", 1) }, PipelineFolders.Children(folders, "Finance/Invoices"));
            Assert.Empty(PipelineFolders.Children(folders, "Ops"));
            Assert.Equal(new[] { "Finance", "Finance/Invoices", "finance/invoices/Q1", "Ops" }, PipelineFolders.All(folders));
            Assert.Equal(new[] { "A", "A/B", "A/B/C" }, PipelineFolders.Path("A/B/C"));
            Assert.Empty(PipelineFolders.Path(""));
            Assert.Equal("C", PipelineFolders.Name("A/B/C"));
            Assert.True(PipelineFolders.IsIn("finance", "Finance"));
            Assert.False(PipelineFolders.IsIn("Finance/Invoices", "Finance"));
            Assert.True(PipelineFolders.IsWithin("Finance/Invoices", "Finance"));
            Assert.False(PipelineFolders.IsWithin("Financed", "Finance"));
            Assert.True(PipelineFolders.IsIn(null, ""));
        }

        // A folder typed in another case is the same folder, spelled as the list spells it - level by level (found driving: a move to
        // "walks/With calls" kept "walks" where the list said "Walks").
        [Fact]
        public void A_folder_is_spelled_as_the_list_already_spells_it_level_by_level()
        {
            var known = new[] { "Walks", "walks/With calls", null };

            Assert.Equal("Walks/With calls/New", PipelineFolders.Spelled("WALKS/with CALLS/New", known));
            Assert.Equal("Walks", PipelineFolders.Spelled("walks", known));
            Assert.Equal("Other/walks", PipelineFolders.Spelled("Other/walks", known)); // the same name elsewhere is another folder
            Assert.Equal("", PipelineFolders.Spelled("", known));
        }

        // A folder renamed, moved or removed: where each pipeline goes, and what is refused - into itself, onto a folder that is
        // there, deeper than allowed, or a folder that is none.
        [Fact]
        public void A_folder_moved_takes_what_is_below_it_and_is_refused_saying_why()
        {
            Assert.Equal("", PipelineFolders.Parent("Finance"));
            Assert.Equal("Finance", PipelineFolders.Parent("Finance/Invoices"));
            Assert.Equal("", PipelineFolders.Parent(""));

            Assert.Equal("Money", PipelineFolders.Moved("Finance", "finance", "Money"));
            Assert.Equal("Money/Invoices/2026", PipelineFolders.Moved("Finance/Invoices/2026", "Finance", "Money"));
            Assert.Equal("Invoices", PipelineFolders.Moved("Finance/Invoices", "Finance", "")); // removed at the top: up to it
            Assert.Equal("", PipelineFolders.Moved("Finance", "Finance", ""));
            Assert.Equal("Financed", PipelineFolders.Moved("Financed", "Finance", "Money")); // a name that only begins the same is another folder
            Assert.Equal("Ops", PipelineFolders.Moved("Ops", "Finance", "Money"));

            var folders = new[] { "Finance", "Finance/Invoices/2026", "Ops", null };
            Assert.Null(PipelineFolders.MoveProblem(folders, "Finance", "Money"));
            Assert.Null(PipelineFolders.MoveProblem(folders, "Finance", "FINANCE")); // its own name in another case
            Assert.Null(PipelineFolders.MoveProblem(folders, "Finance/Invoices", "Ops/Invoices"));
            Assert.Equal("There is no such folder.", PipelineFolders.MoveProblem(folders, "Nowhere", "Money"));
            Assert.Equal("A folder needs a name.", PipelineFolders.MoveProblem(folders, "Finance", ""));
            Assert.Equal("A folder cannot go inside itself.", PipelineFolders.MoveProblem(folders, "Finance", "finance/Again"));
            Assert.Equal("There is a folder 'Ops' already.", PipelineFolders.MoveProblem(folders, "Finance", "ops"));
            Assert.Equal("There is a folder 'Finance' already.", PipelineFolders.MoveProblem(folders, "Finance/Invoices", "Finance")); // its own parent
            Assert.Equal("A folder is at most 4 deep - a folder inside it would be deeper.", PipelineFolders.MoveProblem(folders, "Finance", "Ops/A/B"));
        }
    }
}
