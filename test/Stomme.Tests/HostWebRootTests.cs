#nullable enable
using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-186: an app built on the Stomme.Server package has no web root under <c>dotnet run</c> - the framework's static
    /// files come from the package - and ASP.NET leaves <c>WebRootPath</c> NULL when the folder does not exist, so the app's
    /// first install failed creating the site (measured 2026-09-30). <c>Program.EnsureWebRoot</c> creates and names one and
    /// keeps whatever the host already served. The render guard installs without a <c>wwwroot</c> as the end-to-end half,
    /// and the gate's app-template phase drives the app itself.
    /// </summary>
    public sealed class HostWebRootTests : IDisposable
    {
        private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"stomme-webroot-{Guid.NewGuid():N}");

        public HostWebRootTests() => Directory.CreateDirectory(_contentRoot);

        [Fact]
        public void A_host_without_a_web_root_gets_one_and_serves_both_what_is_written_there_and_what_it_served_before()
        {
            var packaged = new SingleFileProvider("framework.js", "from the package");
            var environment = new Environment(_contentRoot) { WebRootPath = null!, WebRootFileProvider = packaged };

            Stomme.Server.Program.EnsureWebRoot(environment);

            var webRoot = Path.Combine(_contentRoot, "wwwroot");
            Assert.Equal(webRoot, environment.WebRootPath);
            Assert.True(Directory.Exists(webRoot), "the web root was named but not created");

            File.WriteAllText(Path.Combine(webRoot, "uploaded.txt"), "written at run time");
            Assert.True(environment.WebRootFileProvider.GetFileInfo("uploaded.txt").Exists, "a file written into the web root is not served");
            Assert.True(environment.WebRootFileProvider.GetFileInfo("framework.js").Exists, "what the host served before is no longer served");
        }

        [Fact]
        public void A_host_with_a_web_root_is_left_as_it_is()
        {
            var existing = Path.Combine(_contentRoot, "public");
            var provider = new NullFileProvider();
            var environment = new Environment(_contentRoot) { WebRootPath = existing, WebRootFileProvider = provider };

            Stomme.Server.Program.EnsureWebRoot(environment);

            Assert.Equal(existing, environment.WebRootPath);
            Assert.Same(provider, environment.WebRootFileProvider);
            Assert.False(Directory.Exists(Path.Combine(_contentRoot, "wwwroot")), "a web root was created beside the host's own");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_contentRoot))
                {
                    Directory.Delete(_contentRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private sealed class Environment(string contentRoot) : IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = "";
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ApplicationName { get; set; } = "tests";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = contentRoot;
            public string EnvironmentName { get; set; } = "Development";
        }

        /// <summary>Stands for the static web assets a host serves from its packages: one file, in memory.</summary>
        private sealed class SingleFileProvider(string name, string content) : IFileProvider
        {
            public IFileInfo GetFileInfo(string subpath) =>
                subpath.TrimStart('/') == name ? new MemoryFile(name, content) : new NotFoundFileInfo(subpath);

            public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

            public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
        }

        private sealed class MemoryFile(string name, string content) : IFileInfo
        {
            public bool Exists => true;
            public long Length => Encoding.UTF8.GetByteCount(content);
            public string? PhysicalPath => null;
            public string Name => name;
            public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
            public bool IsDirectory => false;
            public Stream CreateReadStream() => new MemoryStream(Encoding.UTF8.GetBytes(content));
        }
    }
}
