using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using NovaGloben.Module.Blogs.Persistence;
using WireModel = NovaGloben.Module.Blogs.Models.BlogPostModel;

namespace NovaGloben.Module.Blogs.Application
{
    /// <summary>
    /// CQRS QUERY: list the posts for one blog (module instance), newest first. A query has no validator and
    /// raises no events — it reads through the module-specific repository method whose <c>ModuleId</c> filter
    /// runs in the store (provider-neutral <c>FindAsync</c>). Dispatched through <c>IMediator.Send</c>.
    /// </summary>
    public sealed record ListPostsQuery(int ModuleId) : IQuery<IReadOnlyList<WireModel>>;

    public sealed class ListPostsQueryHandler(IBlogPostRepository repository)
        : IQueryHandler<ListPostsQuery, IReadOnlyList<WireModel>>
    {
        public async Task<IReadOnlyList<WireModel>> Handle(ListPostsQuery request, CancellationToken ct)
        {
            var posts = await repository.GetByModuleAsync(request.ModuleId, ct);
            return posts.Select(BlogPostMapper.ToWire).ToList();
        }
    }
}
