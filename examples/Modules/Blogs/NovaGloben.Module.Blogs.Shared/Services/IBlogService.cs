using System.Collections.Generic;
using System.Threading.Tasks;
using NovaGloben.Module.Blogs.Models;

namespace NovaGloben.Module.Blogs.Services
{
    /// <summary>
    /// The client/server contract the Blogs UI consumes. The component depends on this interface, never on a
    /// repository — data access from a Blazor component goes through an <c>IService</c> over HTTP, so the same
    /// component renders on Static SSR, Server and WebAssembly. The server binds it to
    /// <c>ServerBlogService</c> (mediator dispatch); the client binds it to <c>BlogService</c> (HTTP to the
    /// controller).
    /// </summary>
    public interface IBlogService
    {
        Task<List<BlogPostModel>> ListPostsAsync(int moduleId);

        Task<BlogPostModel> CreatePostAsync(BlogPostModel post);
    }
}
