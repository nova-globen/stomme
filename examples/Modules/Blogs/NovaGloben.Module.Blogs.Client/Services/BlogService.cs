using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Services;
using Stomme.Shared;
using NovaGloben.Module.Blogs.Models;

namespace NovaGloben.Module.Blogs.Services
{
    /// <summary>
    /// The WebAssembly/browser implementation of <see cref="IBlogService"/>: calls the Blogs Web API over HTTP.
    /// Registered explicitly by the module's <c>ClientStartup</c> (the marker-interface convention cannot
    /// register it: it looks the <c>I{Name}</c> contract up in the implementation's OWN assembly, and the split
    /// moved <see cref="IBlogService"/> to the Shared assembly). On the server the explicit
    /// <c>ServerBlogService</c> registration wins.
    /// </summary>
    public sealed class BlogService : ServiceBase, IBlogService
    {
        public BlogService(HttpClient http, SiteState siteState) : base(http, siteState) { }

        private string ApiUrl => CreateApiUrl("Blog");

        public async Task<List<BlogPostModel>> ListPostsAsync(int moduleId) =>
            await GetJsonAsync<List<BlogPostModel>>($"{ApiUrl}?moduleid={moduleId}") ?? new List<BlogPostModel>();

        public async Task<BlogPostModel> CreatePostAsync(BlogPostModel post) =>
            await PostJsonAsync($"{ApiUrl}", post);
    }
}
