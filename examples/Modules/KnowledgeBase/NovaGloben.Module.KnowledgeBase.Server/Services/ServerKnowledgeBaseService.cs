using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using NovaGloben.Module.KnowledgeBase.Application;
using NovaGloben.Module.KnowledgeBase.Models;

namespace NovaGloben.Module.KnowledgeBase.Services
{
    /// <summary>
    /// The pages' service on the SERVER: the same commands and queries the API sends, through the mediator - so its
    /// authorization behaviour applies to a page exactly as to an API call. The browser gets the HTTP twin instead.
    /// </summary>
    public sealed class ServerKnowledgeBaseService(IMediator mediator) : IKnowledgeBaseService
    {
        public async Task<List<CategoryModel>> ListCategoriesAsync() => (await mediator.Send(new ListCategoriesQuery())).ToList();

        public Task<CategoryModel> CreateCategoryAsync(CreateCategoryRequest request) => mediator.Send(new CreateCategoryCommand(request.Name));

        public async Task<List<ArticleModel>> ListArticlesAsync(Guid? categoryId = null) => (await mediator.Send(new ListArticlesQuery(categoryId))).ToList();

        public Task<ArticleModel?> GetArticleAsync(string slug) => mediator.Send(new GetArticleQuery(slug));

        public Task<ArticleModel> DraftArticleAsync(DraftArticleRequest request) =>
            mediator.Send(new DraftArticleCommand(request.Title, request.Slug, request.Body, request.CategoryId));

        public Task<ArticleModel> EditArticleAsync(Guid articleId, EditArticleRequest request) =>
            mediator.Send(new EditArticleCommand(articleId, request.Title, request.Body, request.CategoryId));

        public Task<ArticleModel> PublishArticleAsync(Guid articleId) => mediator.Send(new PublishArticleCommand(articleId));
    }
}
