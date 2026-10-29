using System.Linq;
using Stomme.Modules.Pipelines.Canvas;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's command bar: the editor's actions found by name in the step search - every word of the query begins a word
    /// of the action's name or of the words it answers to, in any case; nothing for a query under two letters.
    /// </summary>
    public sealed class PipelineEditorCommandsTests
    {
        private static string Found(string query) => string.Join(",", EditorCommands.Match(query).Select(command => command.Id));

        [Theory]
        [InlineData("pub", "publish,run")] // Run the published version
        [InlineData("and pub", "publish")]
        [InlineData("SAVE", "save,publish")]
        [InlineData("zoom fit", "fit")]
        [InlineData("history", "versions")]
        [InlineData("tidy", "tidy")]
        [InlineData("run", "test,run")]
        [InlineData("s", "")] // one letter is a step being looked for
        [InlineData("  ", "")]
        [InlineData("csv", "")]
        public void An_action_is_found_by_the_words_of_its_name_or_those_it_answers_to(string query, string expected) =>
            Assert.Equal(expected, Found(query));

        [Fact]
        public void Every_action_says_its_name_and_no_more_are_shown_than_the_bound()
        {
            Assert.All(EditorCommands.All, command => Assert.False(string.IsNullOrWhiteSpace(command.Label)));
            Assert.Equal(EditorCommands.All.Count, EditorCommands.All.Select(command => command.Id).Distinct().Count());
            Assert.True(EditorCommands.Match("show").Count <= EditorCommands.MaxShown);
        }
    }
}
