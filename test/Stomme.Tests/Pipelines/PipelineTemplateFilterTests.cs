#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - filters in an expression (n8n's expression functions, written as Liquid writes them): each turns a placeholder's
    /// value into another, in a chain, with literal arguments; one renderer for the engine and the editor, so the preview is what
    /// the step gets. A name that is not a filter, and a number filter given no number, are errors saying which - before the run
    /// in the problems panel, and when it renders.
    /// </summary>
    public sealed class PipelineTemplateFilterTests
    {
        private static readonly Dictionary<string, object?> Values = new()
        {
            ["name"] = "  ada lovelace ",
            ["amount"] = "120.456",
            ["when"] = "2026-10-06T07:30:00Z",
            ["tags"] = JsonDocument.Parse("[\"maths\",\"engines\",\"poetry\"]").RootElement.Clone(),
            ["person"] = JsonDocument.Parse("{\"town\":\"London\"}").RootElement.Clone(),
            ["empty"] = "",
        };

        private static (bool Found, object? Value) Lookup(string name) => Values.TryGetValue(name, out var value) ? (true, value) : (false, null);

        private static object? Render(string template) => PipelineTemplate.Render(template, Lookup);

        [Theory]
        [InlineData("{{ name | trim | upper }}", "ADA LOVELACE")]
        [InlineData("{{ name | trim | capitalize }}", "Ada lovelace")]
        [InlineData("{{ name | trim | lower | replace: \"ada\", \"augusta ada\" }}", "augusta ada lovelace")]
        [InlineData("{{ name | trim | truncate: 3 }}", "ada…")]
        [InlineData("Hi {{ person.nick | default: \"friend\" }}!", "Hi friend!")]
        [InlineData("{{ empty | default: 'a|b' }}", "a|b")]
        [InlineData("{{ tags | join: \" + \" }}", "maths + engines + poetry")]
        [InlineData("{{ tags | first | upper }} and {{ tags | last }}", "MATHS and poetry")]
        [InlineData("{{ person.town | split: \"o\" | join: \"0\" }}", "L0nd0n")]
        [InlineData("{{ amount | fixed: 1 }} / {{ amount | round }} / {{ amount | plus: 0.044 }}", "120.5 / 120 / 120.500")]
        [InlineData("{{ amount | times: 2 | divide: 4 | fixed: 2 }}", "60.23")]
        [InlineData("{{ when | date: \"dd MMM yyyy HH:mm\", \"Europe/Stockholm\" }}", "06 Oct 2026 09:30")]
        [InlineData("{{ when | date: \"yyyy-MM-dd\" }}", "2026-10-06")]
        [InlineData("[{{ empty | date: \"yyyy\" }}]", "[]")]
        [InlineData("{{ person | json }}", "{\"town\":\"London\"}")]
        [InlineData("q={{ person.town | urlencode }}&x={{ \"a b\" | urlencode }}", "q=London&x=a%20b")]
        public void A_filter_turns_the_value_into_another_and_filters_chain(string template, string expected)
        {
            Assert.Equal(expected, PipelineTemplate.Text(Render(template)));
        }

        [Fact]
        public void A_whole_placeholder_keeps_a_filters_value_and_names_ignore_the_filters()
        {
            Assert.Equal(120.46m, Render("{{ amount | round: 2 }}"));
            Assert.Equal(3, Render("{{ tags | length }}"));
            Assert.Equal(15, Render("{{ name | length }}"));
            Assert.Equal(new[] { "name", "tags", "person" }, PipelineTemplate.Names("{{ name | upper }} {{ tags | join: \"|\" }} {{ person.town | default: \"x\" }}"));
        }

        [Fact]
        public void A_filter_that_is_not_one_or_a_number_that_is_none_is_an_error_saying_which()
        {
            var unknown = Assert.Throws<PipelineTemplateException>(() => Render("{{ name | shout }}"));
            Assert.Contains("'shout' is not a filter - use upper, lower", unknown.Message);
            Assert.Contains("'ada lovelace' is not a number", Assert.Throws<PipelineTemplateException>(() => Render("{{ name | trim | round: 2 }}")).Message);
            Assert.Contains("cannot divide by 0", Assert.Throws<PipelineTemplateException>(() => Render("{{ amount | divide: 0 }}")).Message);
            Assert.Contains("'soon' is not a date", Assert.Throws<PipelineTemplateException>(() => Render("{{ \"soon\" | date: \"yyyy\" }}")).Message);

            // Before the run: the same, from the text alone.
            Assert.Equal(new[] { "'shout' is not a filter - use " + string.Join(", ", PipelineTemplateFilters.All.Select(filter => filter.Name)) + ".", "round: 'two' is not a number." },
                PipelineTemplateFilters.Problems("{{ name | shout }} {{ amount | round: two }} {{ amount | round: 2 }}"));
        }

        [Fact]
        public void After_a_bar_the_editor_suggests_the_filters_that_start_with_what_was_typed()
        {
            var variables = Values.Select(pair => (pair.Key, pair.Value));
            Assert.Equal(new[] { "{{ name | upper }}", "{{ name | urlencode }}" }, PipelineTemplate.Suggest("Hi {{ name | u", variables));
            Assert.Equal("Hi {{ name | trim | round: 2 }}", PipelineTemplate.Accept("Hi {{ name | trim | ro", Assert.Single(PipelineTemplate.Suggest("Hi {{ name | trim | ro", variables))));
            Assert.Equal(PipelineTemplateFilters.All.Count, PipelineTemplate.Suggest("{{ name |", variables, max: 50).Count);
        }
    }
}
