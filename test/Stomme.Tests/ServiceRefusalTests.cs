using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Modules.Dashboards.Services;
using Stomme.Services;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-222 (found on UAT, a WebAssembly site, 2026-10-04): adding a chart widget without a spec showed "The widget was not
    /// added: Arg_NullReferenceException" - the server had answered 400 "A chart widget requires a chart spec.", the browser's
    /// service returned null and the page carried on with it. A service that opts in to <c>ThrowOnRefusal</c> now throws the
    /// server's reason, as its server-side twin does on a Server site; a service that has not opted in is unchanged.
    /// </summary>
    public sealed class ServiceRefusalTests
    {
        private sealed class Answer : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            private readonly string _mediaType;

            public Answer(HttpStatusCode status, string body, string mediaType = "application/json")
            {
                _status = status;
                _body = body;
                _mediaType = mediaType;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(_status) { RequestMessage = request, Content = new StringContent(_body, Encoding.UTF8, _mediaType) });
        }

        private sealed class InheritedService : ServiceBase
        {
            public InheritedService(HttpClient http) : base(http, new SiteState()) { }

            public Task<DashboardView> PostAsync() => PostJsonAsync<object, DashboardView>("/api/Anything", new { });
        }

        private static HttpClient Client(HttpStatusCode status, string body, string mediaType = "application/json") =>
            new(new Answer(status, body, mediaType)) { BaseAddress = new Uri("http://localhost/") };

        private static DashboardsService Dashboards(HttpStatusCode status, string body, string mediaType = "application/json") =>
            new(Client(status, body, mediaType), new SiteState());

        [Fact]
        public async Task A_refused_write_throws_the_servers_reason()
        {
            var refusal = await Assert.ThrowsAsync<ServiceRefusalException>(() =>
                Dashboards(HttpStatusCode.BadRequest, "{\"error\":\"A chart widget requires a chart spec.\"}").AddWidgetAsync(Guid.NewGuid(), new AddWidgetRequest()));

            Assert.Equal("A chart widget requires a chart spec.", refusal.Message);
            Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        }

        [Fact]
        public async Task A_validation_problem_throws_its_first_error()
        {
            const string problem = "{\"title\":\"One or more validation errors occurred.\",\"status\":400,\"errors\":{\"Name\":[\"The Name field is required.\"]}}";

            var refusal = await Assert.ThrowsAsync<ServiceRefusalException>(() =>
                Dashboards(HttpStatusCode.BadRequest, problem, "application/problem+json").CreateAsync(new CreateDashboardRequest()));

            Assert.Equal("The Name field is required.", refusal.Message);
        }

        [Fact]
        public async Task A_body_that_is_not_a_reason_is_never_shown()
        {
            var refusal = await Assert.ThrowsAsync<ServiceRefusalException>(() =>
                Dashboards(HttpStatusCode.Forbidden, "<html><body>secret page</body></html>", "text/html").CreateAsync(new CreateDashboardRequest()));

            Assert.Equal("The request was refused (403 Forbidden).", refusal.Message);
        }

        [Fact]
        public async Task A_get_answered_not_found_is_still_null_and_any_other_refused_get_throws()
        {
            Assert.Null(await Dashboards(HttpStatusCode.NotFound, "").GetAsync(Guid.NewGuid()));

            await Assert.ThrowsAsync<ServiceRefusalException>(() => Dashboards(HttpStatusCode.Forbidden, "").GetAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task A_service_that_has_not_opted_in_still_returns_the_default()
        {
            Assert.Null(await new InheritedService(Client(HttpStatusCode.BadRequest, "{\"error\":\"refused\"}")).PostAsync());
        }

        /// <summary>The gate: a module page that shows an exception's message has a client service that throws the server's reason.</summary>
        [Fact]
        public void Every_module_page_that_shows_a_failures_reason_has_services_that_throw_it()
        {
            var modules = Path.Combine(RepositoryRoot(), "src", "Stomme.Client", "Modules");
            var showsReason = new Regex(@"(\+\s*ex\.Message|\{ex\.Message\}|=\s*ex\.Message)", RegexOptions.CultureInvariant);
            var missing = Directory.GetDirectories(modules)
                .Where(module => !string.Equals(Path.GetFileName(module), "Admin", StringComparison.Ordinal))
                .Where(module => Directory.GetFiles(module, "*.razor", SearchOption.TopDirectoryOnly).Any(page => showsReason.IsMatch(File.ReadAllText(page))))
                .SelectMany(module => Directory.Exists(Path.Combine(module, "Services"))
                    ? Directory.GetFiles(Path.Combine(module, "Services"), "*.cs")
                    : Array.Empty<string>())
                .Where(service => Regex.IsMatch(File.ReadAllText(service), @":\s*ServiceBase\b"))
                .Where(service => !File.ReadAllText(service).Contains("protected override bool ThrowOnRefusal => true;", StringComparison.Ordinal))
                .Select(service => Path.GetRelativePath(modules, service))
                .ToList();

            Assert.True(missing.Count == 0, "These services return null on a refusal while their page shows the reason: " + string.Join(", ", missing));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }
    }
}
