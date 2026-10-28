using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NovaGloben.Module.KnowledgeBase.Application;
using NovaGloben.Module.KnowledgeBase.Models;

namespace NovaGloben.Module.KnowledgeBase.Controllers
{
    /// <summary>
    /// <c>api/KnowledgeBase</c>: each action carries <c>[RequiresPermissionKey]</c> with one of the module's keys, an
    /// authorization POLICY that runs before the action (a caller without the key never reaches it), and sends the
    /// matching request through the mediator, whose own <c>[Requires]</c> check applies again. A rule the domain refuses
    /// answers 400 with the domain's message; a duplicate slug answers 409; an unknown article - or a draft a visitor asks
    /// for - answers 404; a publish the site's quota refuses answers 429.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class KnowledgeBaseController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public KnowledgeBaseController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/KnowledgeBase/categories
        [HttpGet("categories")]
        [RequiresPermissionKey(KnowledgeBasePermissions.View)]
        public async Task<IActionResult> Categories(CancellationToken ct) => Ok(await _mediator.Send(new ListCategoriesQuery(), ct));

        // POST api/KnowledgeBase/categories
        [HttpPost("categories")]
        [RequiresPermissionKey(KnowledgeBasePermissions.Edit)]
        public Task<IActionResult> CreateCategory([FromBody] CreateCategoryRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new CreateCategoryCommand(request.Name), ct)));

        // GET api/KnowledgeBase/articles?categoryId=...
        [HttpGet("articles")]
        [RequiresPermissionKey(KnowledgeBasePermissions.View)]
        public async Task<IActionResult> Articles([FromQuery] Guid? categoryId, CancellationToken ct) => Ok(await _mediator.Send(new ListArticlesQuery(categoryId), ct));

        // GET api/KnowledgeBase/articles/{slug}
        [HttpGet("articles/{slug}")]
        [RequiresPermissionKey(KnowledgeBasePermissions.View)]
        public async Task<IActionResult> Article(string slug, CancellationToken ct) =>
            await _mediator.Send(new GetArticleQuery(slug), ct) is { } article ? Ok(article) : NotFound();

        // POST api/KnowledgeBase/articles
        [HttpPost("articles")]
        [RequiresPermissionKey(KnowledgeBasePermissions.Edit)]
        public Task<IActionResult> DraftArticle([FromBody] DraftArticleRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new DraftArticleCommand(request.Title, request.Slug, request.Body, request.CategoryId), ct)));

        // PUT api/KnowledgeBase/articles/{id}
        [HttpPut("articles/{id:guid}")]
        [RequiresPermissionKey(KnowledgeBasePermissions.Edit)]
        public Task<IActionResult> EditArticle(Guid id, [FromBody] EditArticleRequest request, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new EditArticleCommand(id, request.Title, request.Body, request.CategoryId), ct)));

        // POST api/KnowledgeBase/articles/{id}/publish
        [HttpPost("articles/{id:guid}/publish")]
        [RequiresPermissionKey(KnowledgeBasePermissions.Publish)]
        public Task<IActionResult> PublishArticle(Guid id, CancellationToken ct) =>
            Answer(async () => Ok(await _mediator.Send(new PublishArticleCommand(id), ct)));

        private async Task<IActionResult> Answer(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (KnowledgeBaseQuotaExceededException ex)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
            }
            catch (System.Collections.Generic.KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
            {
                return Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
