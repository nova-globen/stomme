using System;
using Stomme.SharedKernel.Domain;

namespace NovaGloben.Module.Blogs.Domain
{
    /// <summary>
    /// The AGGREGATE ROOT (<see cref="AggregateRoot{TId}"/>) for a single blog post — the transactional
    /// consistency boundary the storage-blind repository persists. It owns its invariants: a post always has a
    /// validated <see cref="PostTitle"/> value object, a non-empty body and author, and is created only through
    /// the <see cref="Publish"/> factory, which raises a <see cref="BlogPostPublishedDomainEvent"/>.
    /// <para>
    /// It is persisted through the clean-arch stack (<c>IRepository&lt;BlogPost, Guid&gt;</c> +
    /// <c>IUnitOfWork</c>) with NO module-authored provider code: the host maps it into the shared
    /// <c>ModuleAggregatesDbContext</c> on a relational install, or round-trips it through the generic
    /// RavenDB/file repository on a document/file install. The parameterless constructor exists ONLY for EF
    /// Core materialisation; application code must use <see cref="Publish"/> so the invariants and the domain
    /// event cannot be skipped.
    /// </para>
    /// </summary>
    public sealed class BlogPost : AggregateRoot<Guid>
    {
        public const int MaxBodyLength = 20000;
        public const int MaxAuthorLength = 100;

        // EF Core materialisation constructor. Not for application use.
        private BlogPost()
        {
        }

        private BlogPost(Guid id, int moduleId, PostTitle title, string body, string author, DateTime publishedOnUtc)
        {
            Id = id;
            ModuleId = moduleId;
            Title = title;
            Body = body;
            Author = author;
            PublishedOnUtc = publishedOnUtc;
        }

        /// <summary>The module instance (blog) this post belongs to.</summary>
        public int ModuleId { get; private set; }

        /// <summary>The post title, as a validated value object.</summary>
        public PostTitle Title { get; private set; }

        /// <summary>The post body.</summary>
        public string Body { get; private set; }

        /// <summary>The post author's display name.</summary>
        public string Author { get; private set; }

        /// <summary>When the post was published (UTC).</summary>
        public DateTime PublishedOnUtc { get; private set; }

        /// <summary>
        /// The only way to create a post. Validates the body/author invariants, builds the aggregate through
        /// the <see cref="PostTitle"/> value object (which enforces the title invariant on its own), and raises
        /// the <see cref="BlogPostPublishedDomainEvent"/> the unit of work dispatches after the write.
        /// </summary>
        public static BlogPost Publish(int moduleId, string title, string body, string author)
        {
            if (moduleId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(moduleId), "A blog post must belong to a module.");
            }

            var trimmedBody = (body ?? string.Empty).Trim();
            if (trimmedBody.Length == 0)
            {
                throw new ArgumentException("A blog post body is required.", nameof(body));
            }

            if (trimmedBody.Length > MaxBodyLength)
            {
                throw new ArgumentException($"A blog post body must be at most {MaxBodyLength} characters.", nameof(body));
            }

            var trimmedAuthor = (author ?? string.Empty).Trim();
            if (trimmedAuthor.Length == 0)
            {
                throw new ArgumentException("A blog post author is required.", nameof(author));
            }

            if (trimmedAuthor.Length > MaxAuthorLength)
            {
                throw new ArgumentException($"A blog post author must be at most {MaxAuthorLength} characters.", nameof(author));
            }

            var post = new BlogPost(
                Guid.NewGuid(), moduleId, PostTitle.Create(title), trimmedBody, trimmedAuthor, DateTime.UtcNow);

            post.RaiseDomainEvent(new BlogPostPublishedDomainEvent(post.Id, post.ModuleId, post.Title.Value, post.Author));
            return post;
        }
    }
}
