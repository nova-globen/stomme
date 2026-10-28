using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NovaGloben.Module.KnowledgeBase.Models;

namespace NovaGloben.Module.KnowledgeBase.Services
{
    /// <summary>
    /// The contract the module's pages consume. A component depends on this, never on a repository, so the same page
    /// renders on Static SSR, Blazor Server and WebAssembly: the server binds it to the mediator-backed service, the
    /// browser to the HTTP one over <c>api/KnowledgeBase</c>.
    /// </summary>
    public interface IKnowledgeBaseService
    {
        Task<List<CategoryModel>> ListCategoriesAsync();

        Task<CategoryModel> CreateCategoryAsync(CreateCategoryRequest request);

        Task<List<ArticleModel>> ListArticlesAsync(Guid? categoryId = null);

        Task<ArticleModel?> GetArticleAsync(string slug);

        Task<ArticleModel> DraftArticleAsync(DraftArticleRequest request);

        Task<ArticleModel> EditArticleAsync(Guid articleId, EditArticleRequest request);

        Task<ArticleModel> PublishArticleAsync(Guid articleId);
    }
}
