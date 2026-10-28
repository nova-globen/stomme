using System;

namespace NovaGloben.Module.Blogs.Models
{
    /// <summary>
    /// The wire contract for a blog post — the shape that crosses the client/server boundary (component →
    /// <c>IBlogService</c> → controller → back). It is DELIBERATELY separate from the server-side
    /// <c>BlogPost</c> aggregate root (Server assembly, <c>Domain/</c>): the aggregate carries behavior,
    /// invariants, a value object and a version token, none of which belong on the DTO. A mapper translates
    /// aggregate → wire at the application boundary; there is no wire → aggregate mapping because a post is
    /// created through the aggregate's <c>Publish</c> factory, never rebuilt from a DTO.
    /// <para>
    /// This type lives in the SHARED assembly of the split module (it is consumed by the Blazor component and
    /// streams to the WASM client), so it carries no infrastructure dependencies.
    /// </para>
    /// </summary>
    public class BlogPostModel
    {
        /// <summary>Stable identity of the post (the aggregate id).</summary>
        public Guid Id { get; set; }

        /// <summary>The module instance this post belongs to; each placed Blogs module is its own blog.</summary>
        public int ModuleId { get; set; }

        /// <summary>The post title.</summary>
        public string Title { get; set; }

        /// <summary>The post body.</summary>
        public string Body { get; set; }

        /// <summary>The post author's display name.</summary>
        public string Author { get; set; }

        /// <summary>When the post was published (UTC).</summary>
        public DateTime PublishedOnUtc { get; set; }
    }
}
