using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Services;
using Stomme.Shared;
using NovaGloben.Module.KnowledgeBase.Models;

namespace NovaGloben.Module.KnowledgeBase.Services
{
    /// <summary>
    /// The BROWSER's <see cref="IKnowledgeBaseService"/>: HTTP to <c>api/KnowledgeBase</c> through the framework's
    /// <see cref="ServiceBase"/>, which carries the alias path and the antiforgery header. The server decides every
    /// permission again.
    /// </summary>
    public class KnowledgeBaseService : ServiceBase, IKnowledgeBaseService
    {
        public KnowledgeBaseService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        private string ApiUrl => CreateApiUrl("KnowledgeBase");

        public async Task<List<CategoryModel>> ListCategoriesAsync() =>
            await GetJsonAsync<List<CategoryModel>>($"{ApiUrl}/categories") ?? new List<CategoryModel>();

        public async Task<CategoryModel> CreateCategoryAsync(CreateCategoryRequest request) =>
            await PostJsonAsync<CreateCategoryRequest, CategoryModel>($"{ApiUrl}/categories", request);

        public async Task<List<ArticleModel>> ListArticlesAsync(Guid? categoryId = null) =>
            await GetJsonAsync<List<ArticleModel>>(categoryId is { } category ? $"{ApiUrl}/articles?categoryId={category}" : $"{ApiUrl}/articles") ?? new List<ArticleModel>();

        public async Task<ArticleModel?> GetArticleAsync(string slug) =>
            await GetJsonAsync<ArticleModel?>($"{ApiUrl}/articles/{Uri.EscapeDataString(slug)}");

        public async Task<ArticleModel> DraftArticleAsync(DraftArticleRequest request) =>
            await PostJsonAsync<DraftArticleRequest, ArticleModel>($"{ApiUrl}/articles", request);

        public async Task<ArticleModel> EditArticleAsync(Guid articleId, EditArticleRequest request) =>
            await PutJsonAsync<EditArticleRequest, ArticleModel>($"{ApiUrl}/articles/{articleId}", request);

        public async Task<ArticleModel> PublishArticleAsync(Guid articleId) =>
            await PostJsonAsync<object, ArticleModel>($"{ApiUrl}/articles/{articleId}/publish", new object());
    }
}
