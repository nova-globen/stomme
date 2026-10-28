using System;
using System.Linq;
using Stomme.Tenancy.Domain;
using NovaGloben.Module.KnowledgeBase.Domain;
using NovaGloben.Module.KnowledgeBase.Models;
using Xunit;

namespace NovaGloben.Module.KnowledgeBase.Tests
{
    /// <summary>
    /// BL-86 increment 1 - the exemplar's domain rules, with no host: the slug is normalised the way every lookup
    /// normalises its argument (D49), the title is a string value object, a category and an article belong to a site of
    /// a tenant, an article is drafted with an author and published once, and each step raises its domain event.
    /// </summary>
    public sealed class KnowledgeBaseDomainTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("Getting Started", "getting-started")]
        [InlineData("  Café  au   lait! ", "cafe-au-lait")]
        [InlineData("C# & .NET 10", "c-net-10")]
        [InlineData("---Already-slugged---", "already-slugged")]
        public void A_slug_is_the_text_lower_cased_with_everything_else_one_hyphen(string text, string expected)
        {
            Assert.Equal(expected, Slug.From(text));
            Assert.True(Slug.IsNormalised(expected));
        }

        [Fact]
        public void A_slug_is_at_most_80_characters_and_never_ends_with_a_hyphen()
        {
            var slug = Slug.From(string.Join(" ", Enumerable.Repeat("word", 30)));

            Assert.True(slug.Length <= Slug.MaxLength);
            Assert.False(slug.EndsWith('-'));
        }

        [Fact]
        public void A_text_with_no_letter_or_digit_is_refused_as_a_slug()
        {
            Assert.Throws<ArgumentException>(() => Slug.Create("!!! ???"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void An_article_needs_a_title(string title)
        {
            Assert.Throws<ArgumentException>(() => ArticleTitle.Create(title));
        }

        [Fact]
        public void A_title_longer_than_150_characters_is_refused()
        {
            Assert.Throws<ArgumentException>(() => ArticleTitle.Create(new string('a', ArticleTitle.MaxLength + 1)));
        }

        [Fact]
        public void A_category_belongs_to_a_site_of_a_tenant_and_raises_its_event()
        {
            var category = Category.Create(Guid.NewGuid(), TenantId.Default, 3, " How-to guides ", Now);

            Assert.Equal((TenantId.Default, 3, "How-to guides", "how-to-guides"), (category.TenantId, category.SiteId, category.Name, category.Slug));
            Assert.Single(category.DomainEvents.OfType<CategoryCreatedEvent>());
            Assert.Throws<ArgumentOutOfRangeException>(() => Category.Create(Guid.NewGuid(), TenantId.Default, 0, "Guides", Now));
        }

        [Fact]
        public void An_article_is_drafted_by_an_author_with_a_slug_from_its_title()
        {
            var article = Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Reset your password", null, "Open your profile.", 7, Now);

            Assert.Equal((ArticleStatus.Draft, "reset-your-password", 7, (DateTime?)null), (article.Status, article.Slug, article.AuthorUserId, article.PublishedUtc));
            Assert.Equal("Reset your password", article.Title.Value);
            Assert.Single(article.DomainEvents.OfType<ArticleDraftedEvent>());
        }

        [Fact]
        public void An_explicit_slug_is_normalised()
        {
            var article = Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Reset your password", "Password Reset", "Open your profile.", 7, Now);

            Assert.Equal("password-reset", article.Slug);
        }

        [Fact]
        public void An_article_without_an_author_or_a_body_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Title", null, "Body", 0, Now));
            Assert.Throws<ArgumentException>(() => Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Title", null, "  ", 7, Now));
        }

        [Fact]
        public void An_edit_changes_the_title_body_and_category_but_never_the_address()
        {
            var article = Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Reset your password", null, "Open your profile.", 7, Now);
            var category = Guid.NewGuid();

            article.Edit("Resetting a forgotten password", "Choose Forgot password on the sign-in page.", category, Now.AddMinutes(5));

            Assert.Equal(("Resetting a forgotten password", "reset-your-password", (Guid?)category, Now.AddMinutes(5)), (article.Title.Value, article.Slug, article.CategoryId, article.UpdatedUtc));
            Assert.Single(article.DomainEvents.OfType<ArticleEditedEvent>());
            Assert.Throws<ArgumentException>(() => article.Edit(" ", "Body", null, Now));
        }

        [Fact]
        public void An_article_is_published_once_and_raises_its_event()
        {
            var article = Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Reset your password", null, "Open your profile.", 7, Now);

            article.Publish(Now.AddHours(1));

            Assert.Equal((ArticleStatus.Published, (DateTime?)Now.AddHours(1)), (article.Status, article.PublishedUtc));
            var published = Assert.Single(article.DomainEvents.OfType<ArticlePublishedEvent>());
            Assert.Equal(("reset-your-password", 7), (published.Slug, published.AuthorUserId));
            Assert.Throws<InvalidOperationException>(() => article.Publish(Now.AddHours(2)));
        }

        [Fact]
        public void An_erased_author_leaves_a_published_article_attributed_to_nobody_and_otherwise_unchanged()
        {
            var article = Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Reset your password", null, "Open your profile.", 7, Now);
            article.Publish(Now.AddHours(1));

            article.EraseAuthor();

            Assert.Equal((Article.NoAuthor, ArticleStatus.Published, "Reset your password", Now.AddHours(1)), (article.AuthorUserId, article.Status, article.Title.Value, article.UpdatedUtc));
        }

        [Fact]
        public void A_seeded_demo_article_is_published_by_nobody_and_raises_no_event()
        {
            var seeded = Article.Seed(Guid.NewGuid(), TenantId.Default, 3, null, "Welcome", "Welcome to the knowledge base", "Hello.", Now);

            Assert.Equal((ArticleStatus.Published, Article.NoAuthor, (DateTime?)Now, "welcome-to-the-knowledge-base"), (seeded.Status, seeded.AuthorUserId, seeded.PublishedUtc, seeded.Slug));
            Assert.Empty(seeded.DomainEvents);
        }

        [Fact]
        public void A_draft_is_not_anonymised_it_is_deleted_with_its_authors_data()
        {
            var draft = Article.Draft(Guid.NewGuid(), TenantId.Default, 3, null, "Reset your password", null, "Open your profile.", 7, Now);

            Assert.Throws<InvalidOperationException>(() => draft.EraseAuthor());
            Assert.Equal(7, draft.AuthorUserId);
        }
    }
}
