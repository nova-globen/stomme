#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// The site's pipeline folder as the Files page sees it: the very folder the file steps read and write
    /// (<see cref="PipelineFileSandbox"/>, one folder per site, a path that leaves it refused), listed a folder at a time, a file
    /// taken in, a folder made, a file or a folder deleted, a file given back. For the pipelines' authors only - what a run wrote
    /// is run data, which only they are shown.
    /// </summary>
    public static class PipelineSiteFiles
    {
        /// <summary>The site's folder for the request's site.</summary>
        public static string Root(PipelineStepOptions options, ITenantAccessor tenants) => PipelineFileSandbox.SiteRoot(options, tenants.TenantId);

        /// <summary>A path's full path in the site's folder - the folder itself for an empty one - or the refusal, thrown.</summary>
        public static string Resolve(string root, string? path)
        {
            var relative = PipelineFilePaths.Normalize(path ?? "");
            if (relative.Length == 0)
            {
                return root;
            }

            return PipelineFileSandbox.Resolve(root, relative, out var refusal) ?? throw new InvalidOperationException(refusal);
        }

        /// <summary>A folder or a file as the page lists it.</summary>
        public static PipelineFileEntryView Entry(FileSystemInfo item, string folder) => new()
        {
            Name = item.Name,
            Path = PipelineFilePaths.Combine(folder, item.Name),
            IsFolder = item is DirectoryInfo,
            Length = item is FileInfo file ? file.Length : 0,
            ModifiedOnUtc = item.LastWriteTimeUtc,
        };

        /// <summary>Where a file being taken in is written until it is whole - beside every site's folder, in none of them.</summary>
        public static string Incoming(PipelineStepOptions options) => Path.Combine(Path.GetFullPath(options.Files.Root!), ".incoming");
    }

    /// <summary>One folder of the site's pipeline folder - its folders, then its files; null when there is no such folder.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record ListPipelineFilesQuery(string? Folder) : IQuery<PipelineFolderView?>;

    public sealed class ListPipelineFilesQueryHandler(IOptions<PipelineStepOptions> options, ITenantAccessor tenants)
        : IQueryHandler<ListPipelineFilesQuery, PipelineFolderView?>
    {
        public Task<PipelineFolderView?> Handle(ListPipelineFilesQuery request, CancellationToken ct)
        {
            var folder = PipelineFilePaths.Normalize(request.Folder ?? "");
            var full = PipelineSiteFiles.Resolve(PipelineSiteFiles.Root(options.Value, tenants), folder);
            var view = new PipelineFolderView { Folder = folder, MaxFileBytes = options.Value.Files.MaxFileBytes };
            if (!Directory.Exists(full))
            {
                // The site's folder is made by the first file a step writes; until then it is there, and empty.
                return Task.FromResult(folder.Length == 0 ? view : null);
            }

            var directory = new DirectoryInfo(full);
            view.Entries.AddRange(directory.EnumerateDirectories().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item => PipelineSiteFiles.Entry(item, folder)));
            view.Entries.AddRange(directory.EnumerateFiles().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item => PipelineSiteFiles.Entry(item, folder)));
            return Task.FromResult<PipelineFolderView?>(view);
        }
    }

    /// <summary>
    /// A file taken into a folder of the site's pipeline folder, under its own name - refused, saying why, when the name cannot be
    /// a file's, the folder is not there, a file or folder of that name is, or it is larger than a pipeline file may be. Written
    /// beside the site's folder until whole, then moved in, so a refused or broken upload leaves nothing behind.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record UploadPipelineFileCommand(string? Folder, string? Name, Stream Content) : ICommand<PipelineFileEntryView>;

    public sealed class UploadPipelineFileCommandHandler(IOptions<PipelineStepOptions> options, ITenantAccessor tenants)
        : ICommandHandler<UploadPipelineFileCommand, PipelineFileEntryView>
    {
        public async Task<PipelineFileEntryView> Handle(UploadPipelineFileCommand request, CancellationToken ct)
        {
            var name = Path.GetFileName((request.Name ?? "").Replace('\\', '/').Split('/').Last()); // a browser may send a full path
            if (PipelineFilePaths.NameProblem(name) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            var folder = PipelineFilePaths.Normalize(request.Folder ?? "");
            var root = PipelineSiteFiles.Root(options.Value, tenants);
            var directory = PipelineSiteFiles.Resolve(root, folder);
            if (folder.Length == 0)
            {
                Directory.CreateDirectory(directory);
            }
            else if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"There is no folder '{folder}' - make it first.");
            }

            var target = PipelineSiteFiles.Resolve(root, PipelineFilePaths.Combine(folder, name));
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidOperationException(AlreadyThere(name));
            }

            var limit = options.Value.Files.MaxFileBytes;
            var incoming = PipelineSiteFiles.Incoming(options.Value);
            Directory.CreateDirectory(incoming);
            var part = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await request.Content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        written += read;
                        if (written > limit)
                        {
                            throw new InvalidOperationException($"'{name}' is larger than the {PipelineFilePaths.Size(limit)} a pipeline file may be.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }

                try
                {
                    File.Move(part, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                {
                    throw new InvalidOperationException(AlreadyThere(name)); // taken in the meantime
                }
            }
            finally
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }

            return PipelineSiteFiles.Entry(new FileInfo(target), folder);
        }

        private static string AlreadyThere(string name) => $"'{name}' is already in this folder - delete it first, or upload the file under another name.";
    }

    /// <summary>A folder made in the site's pipeline folder; refused, saying why, when its name cannot be one, the folder it is in is not there, or the name is taken.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record CreatePipelineFolderCommand(string? Path) : ICommand<PipelineFileEntryView>;

    public sealed class CreatePipelineFolderCommandHandler(IOptions<PipelineStepOptions> options, ITenantAccessor tenants)
        : ICommandHandler<CreatePipelineFolderCommand, PipelineFileEntryView>
    {
        public Task<PipelineFileEntryView> Handle(CreatePipelineFolderCommand request, CancellationToken ct)
        {
            var path = PipelineFilePaths.Normalize(request.Path ?? "");
            var name = PipelineFilePaths.Name(path);
            if (PipelineFilePaths.NameProblem(name) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            var parent = PipelineFilePaths.Parent(path);
            var root = PipelineSiteFiles.Root(options.Value, tenants);
            var parentDirectory = PipelineSiteFiles.Resolve(root, parent);
            if (parent.Length == 0)
            {
                Directory.CreateDirectory(parentDirectory);
            }
            else if (!Directory.Exists(parentDirectory))
            {
                throw new InvalidOperationException($"There is no folder '{parent}' to make '{name}' in.");
            }

            var target = PipelineSiteFiles.Resolve(root, path);
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidOperationException($"'{name}' is already in this folder.");
            }

            return Task.FromResult(PipelineSiteFiles.Entry(Directory.CreateDirectory(target), parent));
        }
    }

    /// <summary>A file, or a folder with everything in it, deleted from the site's pipeline folder; false when there is none. The site's folder itself is refused.</summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DeletePipelineFileCommand(string? Path) : ICommand<bool>;

    public sealed class DeletePipelineFileCommandHandler(IOptions<PipelineStepOptions> options, ITenantAccessor tenants)
        : ICommandHandler<DeletePipelineFileCommand, bool>
    {
        public Task<bool> Handle(DeletePipelineFileCommand request, CancellationToken ct)
        {
            var path = PipelineFilePaths.Normalize(request.Path ?? "");
            if (path.Length == 0)
            {
                throw new InvalidOperationException("The site's folder itself is not deleted - delete what is in it.");
            }

            var target = PipelineSiteFiles.Resolve(PipelineSiteFiles.Root(options.Value, tenants), path);
            if (File.Exists(target))
            {
                File.Delete(target);
                return Task.FromResult(true);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    /// <summary>A file of the site's pipeline folder, to give back: where it is and its name; null when there is no such file.</summary>
    public sealed record PipelineFileContent(string FullPath, string Name, long Length);

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record GetPipelineFileQuery(string? Path) : IQuery<PipelineFileContent?>;

    public sealed class GetPipelineFileQueryHandler(IOptions<PipelineStepOptions> options, ITenantAccessor tenants)
        : IQueryHandler<GetPipelineFileQuery, PipelineFileContent?>
    {
        public Task<PipelineFileContent?> Handle(GetPipelineFileQuery request, CancellationToken ct)
        {
            var path = PipelineFilePaths.Normalize(request.Path ?? "");
            if (path.Length == 0)
            {
                return Task.FromResult<PipelineFileContent?>(null);
            }

            var target = PipelineSiteFiles.Resolve(PipelineSiteFiles.Root(options.Value, tenants), path);
            return Task.FromResult(File.Exists(target) ? new PipelineFileContent(target, Path.GetFileName(target), new FileInfo(target).Length) : null);
        }
    }
}
