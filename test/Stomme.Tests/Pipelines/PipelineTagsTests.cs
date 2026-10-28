#nullable enable
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Pipelines;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>BL-229: the one rule for a pipeline's tags, held by the list page and the server alike.</summary>
    public sealed class PipelineTagsTests
    {
        [Fact]
        public void Tags_are_tidied_kept_once_whatever_their_case_and_in_the_order_given()
        {
            Assert.Equal(new[] { "billing", "on call", "v1.2_beta-3" }, PipelineTags.Parse(" billing ,  on   call,, BILLING , v1.2_beta-3,", out var error));
            Assert.Null(error);
            Assert.Empty(PipelineTags.Normalize(null, out error));
            Assert.Null(error);
            Assert.Equal(new[] { "Kundén" }, PipelineTags.Normalize(new string?[] { null, " ", "Kundén" }, out error));
            Assert.Null(error);
        }

        [Fact]
        public void A_tag_that_cannot_be_one_is_refused_with_the_reason()
        {
            PipelineTags.Parse("ops, a/b", out var error);
            Assert.Equal("'a/b' cannot be a tag: at most 30 letters, digits, spaces, '-', '_' or '.'.", error);
            PipelineTags.Parse(new string('x', PipelineTags.MaxLength + 1), out error);
            Assert.StartsWith("'xxxx", error);
            Assert.Single(PipelineTags.Normalize(new[] { new string('x', PipelineTags.MaxLength) }, out error));
            Assert.Null(error);

            var many = PipelineTags.Normalize(Enumerable.Range(1, PipelineTags.MaxTags + 2).Select(i => "t" + i), out error);
            Assert.Equal("A pipeline takes at most 10 tags.", error);
            Assert.Equal(PipelineTags.MaxTags, many.Count);
        }
    }
}
