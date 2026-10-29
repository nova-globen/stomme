using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Modules.ContextualHelp.Services;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// The BROWSER side of <see cref="IHelpService"/>: what the launcher costs a WebAssembly site.
    /// </summary>
    /// <remarks>
    /// The launcher asks for every anchor on every page view, and in a WebAssembly runtime each ask is an HTTP request. A
    /// stock install has help OFF, so without a memory of that answer it would pay one 404 per anchor per page, forever.
    /// "Off" is the ROUTER's 404, distinct from "no topic" (204) - which must NOT switch the launcher off.
    /// </remarks>
    public class HelpClientServiceTests
    {
        [Fact]
        public async Task Once_the_server_says_help_is_OFF_the_launcher_stops_asking()
        {
            var handler = new CountingHandler(HttpStatusCode.NotFound);
            var service = Service(handler);

            Assert.Null(await service.GetTopicAsync("page:", "en"));
            Assert.Null(await service.GetTopicAsync("stomme.modules.admin.users.index", "en"));
            Assert.Null(await service.GetTopicAsync("page:about", "en"));

            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task NO_TOPIC_is_not_OFF_every_anchor_is_still_asked()
        {
            var handler = new CountingHandler(HttpStatusCode.NoContent);
            var service = Service(handler);

            Assert.Null(await service.GetTopicAsync("page:", "en"));
            Assert.Null(await service.GetTopicAsync("page:about", "sv-SE"));

            Assert.Equal(2, handler.Requests);
            Assert.Contains("anchor=page%3Aabout", handler.LastUri, StringComparison.Ordinal);
            Assert.Contains("locale=sv-SE", handler.LastUri, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_topic_is_read_from_the_body()
        {
            var handler = new CountingHandler(HttpStatusCode.OK,
                "{\"anchor\":\"page:\",\"locale\":\"en\",\"title\":\"Welcome\",\"safeHtml\":\"<p>Hi</p>\"}");
            var topic = await Service(handler).GetTopicAsync("page:", "en");

            Assert.NotNull(topic);
            Assert.Equal("Welcome", topic.Title);
            Assert.Equal("<p>Hi</p>", topic.SafeHtml);
        }

        private static HelpService Service(HttpMessageHandler handler) =>
            new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                new SiteState { Alias = new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost", Protocol = "http://" } });

        private sealed class CountingHandler(HttpStatusCode status, string? json = null) : HttpMessageHandler
        {
            public int Requests;
            public string LastUri = "";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                LastUri = request.RequestUri?.ToString() ?? "";
                var response = new HttpResponseMessage(status);
                if (json is not null)
                {
                    response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                }

                return Task.FromResult(response);
            }
        }
    }
}
