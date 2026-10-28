using System;

namespace NovaGloben.Module.KnowledgeBase.Models
{
    /// <summary>An article's lifecycle: drafted, then published.</summary>
    public enum ArticleStatus
    {
        Draft = 1,
        Published = 2,
    }

    /// <summary>
    /// The wire shape of an article - what crosses the client/server boundary. It is deliberately NOT the server's
    /// aggregate: the aggregate carries behaviour, invariants and value objects that do not belong on a DTO.
    /// </summary>
    public sealed record ArticleModel(
        Guid Id,
        Guid? CategoryId,
        string Title,
        string Slug,
        string Body,
        ArticleStatus Status,
        int AuthorUserId,
        DateTime CreatedUtc,
        DateTime? PublishedUtc);

    /// <summary>The wire shape of a category.</summary>
    public sealed record CategoryModel(Guid Id, string Name, string Slug, DateTime CreatedUtc);

    /// <summary>The body of a request to draft an article; the slug is derived from the title when none is given.</summary>
    public sealed class DraftArticleRequest
    {
        public string Title { get; set; } = string.Empty;

        public string? Slug { get; set; }

        public string Body { get; set; } = string.Empty;

        public Guid? CategoryId { get; set; }
    }

    /// <summary>The body of a request to edit an article; its slug never changes.</summary>
    public sealed class EditArticleRequest
    {
        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public Guid? CategoryId { get; set; }
    }

    /// <summary>The body of a request to create a category.</summary>
    public sealed class CreateCategoryRequest
    {
        public string Name { get; set; } = string.Empty;
    }
}
