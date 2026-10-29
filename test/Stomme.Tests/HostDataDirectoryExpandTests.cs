#nullable enable
using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>HostDataDirectory.Expand</c> touches the file system only when there is a <c>|DataDirectory|</c> token to
    /// expand. It used to create the data directory for EVERY connection string, so a fixture with an explicit path raced
    /// another suite's deleted content root through the process-wide fallback (measured: a DirectoryNotFoundException in a
    /// full gate). Asked through a host environment instance - never the process-wide slot, which other suites share.
    /// </summary>
    public sealed class HostDataDirectoryExpandTests : IDisposable
    {
        private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"stomme-expand-{Guid.NewGuid():N}");

        [Fact]
        public void An_explicit_connection_string_is_returned_as_it_is_and_creates_nothing()
        {
            var services = Services();
            const string explicitPath = "Data Source=C:\\somewhere\\explicit.db";

            Assert.Equal(explicitPath, InvokeExpand(services, explicitPath));
            Assert.False(Directory.Exists(Path.Combine(_contentRoot, "Data")), "an explicit connection string created the data directory");
        }

        [Fact]
        public void A_tokenised_connection_string_expands_to_THIS_hosts_data_directory_and_creates_it()
        {
            var services = Services();
            var expanded = InvokeExpand(services, "Data Source=|DataDirectory|x.db");

            Assert.Equal($"Data Source={Path.Combine(_contentRoot, "Data")}{Path.DirectorySeparatorChar}x.db", expanded);
            Assert.True(Directory.Exists(Path.Combine(_contentRoot, "Data")));
        }

        private IServiceCollection Services()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IWebHostEnvironment>(new Environment(_contentRoot));
            return services;
        }

        private static string InvokeExpand(IServiceCollection services, string connectionString) =>
            HostDataDirectory.Expand(services, connectionString);

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
            public string WebRootPath { get; set; } = contentRoot;
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ApplicationName { get; set; } = "tests";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = contentRoot;
            public string EnvironmentName { get; set; } = "Development";
        }
    }
}
