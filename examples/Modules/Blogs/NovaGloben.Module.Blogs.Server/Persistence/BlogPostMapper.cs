using NovaGloben.Module.Blogs.Domain;
using NovaGloben.Module.Blogs.Models;

namespace NovaGloben.Module.Blogs.Persistence
{
    /// <summary>
    /// Translates the <see cref="BlogPost"/> aggregate (server domain) to the <see cref="BlogPostModel"/> wire
    /// contract at the application boundary — the aggregate-beside-wire-contract split. Queries map aggregate →
    /// wire for the response; there is no wire → aggregate mapping because a new post is created through the
    /// aggregate's <c>Publish</c> factory, never rebuilt from a DTO.
    /// </summary>
    internal static class BlogPostMapper
    {
        public static BlogPostModel ToWire(BlogPost aggregate) => new BlogPostModel
        {
            Id = aggregate.Id,
            ModuleId = aggregate.ModuleId,
            Title = aggregate.Title.Value,
            Body = aggregate.Body,
            Author = aggregate.Author,
            PublishedOnUtc = aggregate.PublishedOnUtc,
        };
    }
}
