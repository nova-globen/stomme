using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using File = Stomme.Models.File;

namespace Stomme.Tests
{
    // SynchronizationJob.ResolveFileIdAsync maps a primary site's file reference (logo, favicon, PWA icons)
    // onto the secondary site by (folder path, file name). Both lookups can legitimately return null — a
    // dangling FileId on the primary, or a file the secondary site simply does not have — and the job runs
    // in the background where an unhandled NullReferenceException aborts the whole site synchronization.
    // docs/backlog.md §BL-32.
    public class SynchronizationJobFileResolutionTests
    {
        // Only the two reads ResolveFileIdAsync performs are answered; everything else throws, so the job
        // reaching for any other file operation would fail loudly rather than be silently stubbed.
        private sealed class StubFileRepository : IFileRepository
        {
            public File FileById;
            public File Counterpart;

            public Task<File> GetFileAsync(int fileId) => Task.FromResult(FileById);
            public Task<File> GetFileAsync(int siteId, string folderPath, string fileName) => Task.FromResult(Counterpart);

            public Task<IEnumerable<File>> GetFilesAsync(int folderId) => throw new NotImplementedException();
            public Task<IEnumerable<File>> GetFilesAsync(int folderId, bool tracking) => throw new NotImplementedException();
            public Task<File> AddFileAsync(File file) => throw new NotImplementedException();
            public Task<File> UpdateFileAsync(File file) => throw new NotImplementedException();
            public Task<File> GetFileAsync(int fileId, bool tracking) => throw new NotImplementedException();
            public Task<File> GetFileAsync(int folderId, string fileName) => throw new NotImplementedException();
            public Task DeleteFileAsync(int fileId) => throw new NotImplementedException();
            public Task<string> GetFilePathAsync(int fileId) => throw new NotImplementedException();
            public Task<string> GetFilePathAsync(File file) => throw new NotImplementedException();
        }

        private static async Task<int?> ResolveAsync(StubFileRepository files, int? fileId, int siteId = 2)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IFileRepository>(files);
            await using var provider = services.BuildServiceProvider();

            var job = new SynchronizationJob(provider.GetRequiredService<IServiceScopeFactory>());
            return await job.ResolveFileIdAsync(provider, fileId, siteId);
        }

        [Fact]
        public async Task A_null_file_reference_stays_null_without_touching_the_repository()
        {
            var result = await ResolveAsync(new StubFileRepository(), fileId: null);

            Assert.Null(result);
        }

        // The primary site's FileId points at a row that no longer exists. The old code dereferenced
        // file.Folder.Path on the null and threw inside the background job.
        [Fact]
        public async Task A_dangling_file_reference_resolves_to_null_instead_of_throwing()
        {
            var files = new StubFileRepository { FileById = null, Counterpart = null };

            var result = await ResolveAsync(files, fileId: 17);

            Assert.Null(result);
        }

        // The file exists on the primary but the secondary site has no counterpart at that path+name. The
        // old code dereferenced .FileId on the null second lookup and threw.
        [Fact]
        public async Task A_file_with_no_counterpart_on_the_target_site_resolves_to_null()
        {
            var files = new StubFileRepository
            {
                FileById = new File { FileId = 17, Name = "logo.png", Folder = new Folder { Path = "Public/" } },
                Counterpart = null
            };

            var result = await ResolveAsync(files, fileId: 17);

            Assert.Null(result);
        }

        [Fact]
        public async Task A_resolvable_file_maps_to_the_counterpart_sites_file_id()
        {
            var files = new StubFileRepository
            {
                FileById = new File { FileId = 17, Name = "logo.png", Folder = new Folder { Path = "Public/" } },
                Counterpart = new File { FileId = 42 }
            };

            var result = await ResolveAsync(files, fileId: 17);

            Assert.Equal(42, result);
        }
    }
}
