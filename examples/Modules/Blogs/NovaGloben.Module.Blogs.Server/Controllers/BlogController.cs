using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NovaGloben.Module.Blogs.Services;
using BlogPostModel = NovaGloben.Module.Blogs.Models.BlogPostModel;

namespace NovaGloben.Module.Blogs.Controllers
{
    /// <summary>
    /// The Web API boundary for the Blogs module: the client <c>IBlogService</c> calls these over HTTP, and
    /// every action delegates to the server <see cref="IBlogService"/>, which dispatches through the
    /// <c>IMediator</c>. This is the full Stomme module round trip — component → client IService (HTTP) →
    /// controller → server IService → mediator → handler → storage-blind repository.
    /// <para>
    /// A blog is public to read and (for this sample) to post, so reads and writes are
    /// <see cref="AllowAnonymousAttribute"/> — this keeps the sample easy to drive. A production module gating
    /// writes would keep <c>ModuleControllerBase</c>'s per-entity authorization instead.
    /// </para>
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    [AllowAnonymous]
    public class BlogController : ModuleControllerBase
    {
        private readonly IBlogService _blogService;

        public BlogController(IBlogService blogService, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _blogService = blogService;
        }

        // GET api/blog?moduleid=x  — the CQRS query
        [HttpGet]
        public async Task<IEnumerable<BlogPostModel>> Get(int moduleId) =>
            await _blogService.ListPostsAsync(moduleId);

        // POST api/blog — the CQRS command
        [HttpPost]
        public async Task<ActionResult<BlogPostModel>> Post([FromBody] BlogPostModel post)
        {
            var created = await _blogService.CreatePostAsync(post);
            return created;
        }
    }
}
