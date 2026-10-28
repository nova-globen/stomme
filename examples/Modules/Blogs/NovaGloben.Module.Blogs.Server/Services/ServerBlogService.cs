using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Modules;
using NovaGloben.Module.Blogs.Application;
using NovaGloben.Module.Blogs.Models;

namespace NovaGloben.Module.Blogs.Services
{
    /// <summary>
    /// The SERVER side of <see cref="IBlogService"/>. Every call is a CQRS dispatch through the
    /// <see cref="IMediator"/> — a query for the read, a command for the write — so the business logic lives in
    /// mediator handlers behind the host's behavior pipeline and this service is a thin transport shim.
    /// <para>
    /// It is an <see cref="ITransientService"/>, registered explicitly in the module's <c>ServerStartup</c> so
    /// it wins over the client HTTP implementation on the server (the same pattern the framework modules use).
    /// </para>
    /// </summary>
    public sealed class ServerBlogService(IMediator mediator) : IBlogService, ITransientService
    {
        public async Task<List<BlogPostModel>> ListPostsAsync(int moduleId)
        {
            var posts = await mediator.Send(new ListPostsQuery(moduleId));
            return posts.ToList();
        }

        public Task<BlogPostModel> CreatePostAsync(BlogPostModel post) =>
            mediator.Send(new CreatePostCommand(post.ModuleId, post.Title, post.Body, post.Author));
    }
}
