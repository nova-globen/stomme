using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;
using File = Stomme.Models.File;

namespace Stomme.Repository
{
    public interface IFileRepository
    {
        Task<IEnumerable<File>> GetFilesAsync(int folderId);
        Task<IEnumerable<File>> GetFilesAsync(int folderId, bool tracking);
        Task<File> AddFileAsync(File file);
        Task<File> UpdateFileAsync(File file);
        Task<File> GetFileAsync(int fileId);
        Task<File> GetFileAsync(int fileId, bool tracking);
        Task<File> GetFileAsync(int folderId, string fileName);
        Task<File> GetFileAsync(int siteId, string folderPath, string fileName);
        Task DeleteFileAsync(int fileId);
        Task<string> GetFilePathAsync(int fileId);
        Task<string> GetFilePathAsync(File file);
    }

    /// <summary>
    /// The File cluster on the persistence seam (D49) — the last one to cross, and the one D48 escalated.
    /// Writes through <see cref="IFileStore"/>, reads through <see cref="IFrameworkStoreSessionFactory"/>
    /// (D19). The <c>DbSet&lt;File&gt;</c> is gone from <see cref="TenantDBContext"/> — that deletion is the
    /// compiler's proof this cluster moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What D48 escalated, and what D49 decided.</b> The two reads below that find a file BY NAME used to
    /// fold case on the COLUMN — <c>item.Name.ToLower() == fileName</c>. That was not stylistic: the live
    /// database stores mixed-case names and paths (<c>Public/</c>, <c>Users/1/</c>) while
    /// <c>Pages/Files.cshtml.cs</c> lowercases the requested URL before calling, so the fold was the only
    /// reason a file resolved at all. RavenDB's LINQ provider cannot translate it (<c>NotSupportedException</c>,
    /// measured on a real server), and the portable spelling is not a substitute — plain <c>==</c> is
    /// case-SENSITIVE on relational and case-INSENSITIVE on RavenDB. One expression, two semantics.
    /// </para>
    /// <para>
    /// D49 normalizes the DATA instead of translating the QUERY. <see cref="FileAggregate.NameNormalized"/>
    /// and <see cref="FolderAggregate.PathNormalized"/> hold a lowercase copy beside the stored case, and the
    /// reads below filter them with plain <c>==</c>. Both providers then agree, because for two lowercase
    /// strings ordinal equality and case-insensitive equality are the same relation — asserted live on each by
    /// <c>A_normalized_column_filter_is_identical_on_every_provider</c>. This is Identity's
    /// <c>NormalizedUserName</c> split, already running in <see cref="TenantDBContext"/>.
    /// </para>
    /// <para>
    /// <b>BOTH SIDES ARE NORMALIZED HERE, INSIDE THE REPOSITORY, AND THAT IS THE POINT.</b> The column side is
    /// derived in the aggregate's setter; the ARGUMENT side is normalized by
    /// <see cref="NormalizedText.Normalize"/> in each read below. The harvested code required the CALLER to
    /// pre-lowercase (<c>item.Name.ToLower() == fileName</c> — argument untouched), which is exactly the
    /// invisible convention D49 rejected when it rejected teaching <c>RavenLinqRewriting</c> to fold
    /// <c>.ToLower()</c>: a rule the seam cannot see or enforce is a rule that gets broken. It already had
    /// been — see <see cref="GetFileAsync(int, string, string)"/> on <c>SynchronizationJob</c>. A caller can no
    /// longer get this wrong, and <c>A_file_resolves_by_name_whatever_case_the_caller_passes</c> pins that.
    /// </para>
    /// <para>
    /// <b>The join stays decomposed (D22 rule 9, the D21 pattern), unchanged from D48.</b>
    /// <see cref="GetFileAsync(int, string, string)"/> cannot filter across <c>File.Folder</c> — D48 severed
    /// that navigation to <c>[NotMapped]</c> because while it stayed mapped EF rediscovered the Folder
    /// contract through it and broke the model at startup. So the folder ids are resolved FIRST (their own
    /// database-side filter, now on the normalized column) and the files are fanned in on
    /// <c>FolderId IN (...)</c>. BOTH halves stay in the database; nothing moved into memory.
    /// </para>
    /// <para>
    /// <b>The folder resolution is deliberately NOT <c>IFolderRepository.GetFolderAsync(siteId, path)</c>.</b>
    /// That method matches the path CASE-SENSITIVELY (<c>item.Path == path</c>) because its callers pass the
    /// stored case; this read matches case-INSENSITIVELY because its caller passes a lowercased URL segment.
    /// Reusing it would compare <c>Public/</c> against <c>public/</c> and 404 every file on the site. It also
    /// returns a SET, because nothing stops <c>Docs/</c> and <c>docs/</c> coexisting — the unique index on
    /// (site_id, path) is case-sensitive and <b>D49 did not move it onto the normalized column</b>, precisely
    /// so that a pair which is legal today stays legal.
    /// </para>
    /// <para>
    /// <b>The store is CONSTRUCTOR-INJECTED, and D47's per-write store is deliberately NOT cargo-culted here.
    /// That was checked, not assumed — and the first draft of this class got it wrong.</b> The hazard is real:
    /// a store owns ONE <see cref="TenantDBContext"/> for its lifetime and a controller holds one transient
    /// repository per request, so two writes to the SAME key through one instance hit the identity map and
    /// throw (D44). <c>PROBE_two_writes_to_the_same_file_through_one_repository_instance</c> demonstrates it
    /// live rather than describing it.
    /// </para>
    /// <para>
    /// <b>No File path reaches that shape.</b> Every call site was walked: the zip loop
    /// (<c>FileController.cs:302-330</c>), the upload (<c>:519-535</c>), <c>ModuleController</c>'s import and
    /// <c>SynchronizationJob</c>'s file sync all Add-OR-Update in an if/else, over DISTINCT keys per iteration.
    /// The one path that writes twice is <c>FileController.Put</c>, whose <c>CreateFile</c> may
    /// <see cref="DeleteFileAsync"/> an over-capacity file (key N) before <c>:267</c> unconditionally calls
    /// <see cref="UpdateFileAsync"/> on key N — and that does NOT hit the identity map, because EF DETACHES a
    /// deleted entity once it is saved. It fails on the row being gone, identically with a per-write store, so
    /// a per-write store would fix nothing there. It is a pre-existing product-logic wrinkle, not a lifetime
    /// one, and it is out of scope here.
    /// </para>
    /// <para>
    /// So this matches <see cref="FolderRepository"/> (D48) and the eleven clusters before it; UrlMapping
    /// remains the sole exception, because it is the sole cluster whose READ writes.
    /// <c>TheFileWriteLifetimeTests</c> pins the shapes production actually uses.
    /// </para>
    /// </remarks>
    public class FileRepository : IFileRepository
    {
        private readonly IFileStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IPermissionRepository _permissions;
        private readonly IFolderRepository _folderRepository;
        private readonly ITenantManager _tenants;

        public FileRepository(IFileStore store, IFrameworkStoreSessionFactory sessions, IPermissionRepository permissions, IFolderRepository folderRepository, ITenantManager tenants)
        {
            _store = store;
            _sessions = sessions;
            _permissions = permissions;
            _folderRepository = folderRepository;
            _tenants = tenants;
        }

        /// <summary>
        /// Opens a read session, runs <paramref name="read"/> against it and disposes it. Reads NEVER go
        /// through the store (D19).
        /// </summary>
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<File>> GetFilesAsync(int folderId)
        {
            return await GetFilesAsync(folderId, true);
        }

        /// <summary>
        /// NOTE the <paramref name="tracking"/> parameter is now INERT, and deliberately kept on the
        /// interface — as on <see cref="IFolderRepository"/> (D48) and the leaf clusters (D44). A session read
        /// tracks nothing (D19), so both arms return the same detached models. Removing the overload would
        /// churn <c>FileController</c> for no behaviour change; it stays at parity.
        /// </summary>
        public async Task<IEnumerable<File>> GetFilesAsync(int folderId, bool tracking)
        {
            var folder = await _folderRepository.GetFolderAsync(folderId, false);
            var permissions = (await _permissions.GetPermissionsAsync(folder.SiteId, EntityNames.Folder, folderId)).ToList();

            // The .Include(item => item.Folder) is gone with the navigation (D48); every file in this call
            // shares ONE folder (it is the argument), so the stitch below is a single object, not a second
            // query per row. This read does NOT pay N+1.
            var files = await QueryAsync(session => session.Query<FileAggregate>()
                .Where(item => item.FolderId == folderId)
                .ToList()
                .ConvertAll(FileMapper.ToModel));

            var alias = await _tenants.GetAliasAsync();
            folder.PermissionList = permissions.ToList();
            foreach (var file in files)
            {
                file.Folder = folder;
                file.Url = GetFileUrl(file, alias);
            }
            return files;
        }

        public async Task<File> AddFileAsync(File file)
        {
            var aggregate = FileMapper.ToAggregate(file, assignId: false);
            await _store.AddAsync(aggregate);
            FileMapper.CopyPersistedValues(aggregate, file);
            file.Folder = await _folderRepository.GetFolderAsync(file.FolderId);
            file.Url = GetFileUrl(file, await _tenants.GetAliasAsync());
            return file;
        }

        public async Task<File> UpdateFileAsync(File file)
        {
            var aggregate = FileMapper.ToAggregate(file, assignId: true);
            await _store.UpdateAsync(aggregate);
            FileMapper.CopyPersistedValues(aggregate, file);
            file.Folder = await _folderRepository.GetFolderAsync(file.FolderId, false);
            file.Url = GetFileUrl(file, await _tenants.GetAliasAsync());
            return file;
        }

        public async Task<File> GetFileAsync(int fileId)
        {
            File file = await GetFileAsync(fileId, true);
            return file;
        }

        /// <summary>See <see cref="GetFilesAsync(int, bool)"/> — <paramref name="tracking"/> is inert.</summary>
        public async Task<File> GetFileAsync(int fileId, bool tracking)
        {
            var file = await QueryAsync(session => FileMapper.ToModel(session.Query<FileAggregate>()
                .FirstOrDefault(item => item.Id == fileId)));

            if (file != null)
            {
                await PopulateFolderAsync(file);
            }
            return file;
        }

        /// <summary>
        /// The name lookup within a known folder. <c>FileController.Get(folderId, name)</c>,
        /// <c>FileController.CreateFile</c> and <c>ModuleController</c>'s import all call it.
        /// </summary>
        /// <remarks>
        /// The harvested read folded case on BOTH sides (<c>item.Name.ToLower() == fileName.ToLower()</c>), so
        /// its three callers were already free to pass any case and all three did. That freedom is preserved
        /// exactly — the column side is now <see cref="FileAggregate.NameNormalized"/> and the argument side is
        /// normalized here. No caller changes.
        /// </remarks>
        public async Task<File> GetFileAsync(int folderId, string fileName)
        {
            var name = NormalizedText.Normalize(fileName);

            var file = await QueryAsync(session => FileMapper.ToModel(session.Query<FileAggregate>()
                .FirstOrDefault(item => item.FolderId == folderId && item.NameNormalized == name)));

            if (file != null)
            {
                await PopulateFolderAsync(file);
            }

            return file;
        }

        /// <summary>
        /// The RENDER PATH's read: <c>Pages/Files.cshtml.cs</c> resolves every requested static file through
        /// it, having lowercased both the folder path and the file name from the URL.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The decomposed join (D22 rule 9)</b> — see the remarks on this class for why the folder ids are
        /// resolved separately, why they are a LIST, and why
        /// <c>IFolderRepository.GetFolderAsync(siteId, path)</c> is the wrong tool.
        /// </para>
        /// <para>
        /// <b>D49 NORMALIZES THE ARGUMENTS HERE, WHICH IS A DELIBERATE BEHAVIOUR CHANGE, AND IT FIXES A LIVE
        /// NRE.</b> The harvested read did NOT normalize its arguments — it was <c>item.Name.ToLower() ==
        /// fileName</c>, i.e. the caller was expected to have lowercased. <c>Files.cshtml.cs</c> does.
        /// <c>SynchronizationJob.ResolveFileIdAsync</c> does NOT: it passes <c>file.Folder.Path</c> and
        /// <c>file.Name</c> straight out of the database, in stored case, so a mixed-case folder never matched,
        /// the method returned null, and the job dereferenced <c>.FileId</c> on it — an unhandled
        /// <see cref="NullReferenceException"/> that aborted site synchronization. D48 found it and left it at
        /// parity. Normalizing inside the repository removes THAT cause; see the note on
        /// <c>SynchronizationJob.ResolveFileIdAsync</c> for the second, independent cause on the same line
        /// which this does NOT fix and which is out of scope here.
        /// </para>
        /// </remarks>
        public async Task<File> GetFileAsync(int siteId, string folderPath, string fileName)
        {
            var folderIds = await GetFolderIdsByPathAsync(siteId, folderPath);
            if (folderIds.Count == 0)
            {
                return null;
            }

            var name = NormalizedText.Normalize(fileName);

            var file = await QueryAsync(session => FileMapper.ToModel(session.Query<FileAggregate>()
                .FirstOrDefault(item => folderIds.Contains(item.FolderId) && item.NameNormalized == name)));

            if (file != null)
            {
                await PopulateFolderAsync(file);
            }

            return file;
        }

        /// <summary>
        /// The folder half of the decomposed join: every folder in the site whose path normalizes to
        /// <paramref name="folderPath"/>. Stays in the DATABASE, on both providers.
        /// </summary>
        /// <remarks>
        /// A LIST, not a single id, because the harvested join matched a set — the (site_id, path) unique
        /// index is case-sensitive, so <c>Docs/</c> and <c>docs/</c> may coexist and both normalize to
        /// <c>docs/</c>. D49 did not move that index onto the normalized column, so that pair is still legal
        /// and this still has to return both. Returning the first would silently change which file a URL
        /// resolves to.
        /// <para>
        /// THIS is the read that used to keep the File cluster off the seam: it was
        /// <c>item.Path.ToLower() == folderPath</c>, which throws <c>NotSupportedException</c> on RavenDB
        /// (D48, measured — and <c>TheToLowerTranslationGapTests</c> still pins that measurement, because the
        /// gap is real and D49 routed around it rather than closing it). Filtering
        /// <see cref="FolderAggregate.PathNormalized"/> with plain <c>==</c> says the same thing on every
        /// provider.
        /// </para>
        /// </remarks>
        private async Task<List<int>> GetFolderIdsByPathAsync(int siteId, string folderPath)
        {
            var path = NormalizedText.Normalize(folderPath);

            return await QueryAsync(session => session.Query<FolderAggregate>()
                .Where(item => item.SiteId == siteId && item.PathNormalized == path)
                .ToList()
                .ConvertAll(item => item.Id));
        }

        /// <summary>
        /// Stitches the contract's <see cref="File.Folder"/> view state on from the Folder aggregate, and the
        /// permissions onto it — what the severed navigation's <c>.Include</c> used to do. Roughly thirty
        /// production reads dereference the result; see the remarks on <see cref="File.Folder"/>.
        /// </summary>
        private async Task PopulateFolderAsync(File file)
        {
            file.Folder = await _folderRepository.GetFolderAsync(file.FolderId, false);
            file.Url = GetFileUrl(file, await _tenants.GetAliasAsync());
        }

        /// <summary>
        /// Removes the file row. The read and the delete share ONE store deliberately — they are the same
        /// unit of work on the same tracked identity, which is what <see cref="IRepository{T, TId}"/> expects
        /// (and what <c>FolderRepository.DeleteFolderAsync</c> does). What must not be shared is a store
        /// across two SEPARATE writes; see the remarks on this class.
        /// </summary>
        public async Task DeleteFileAsync(int fileId)
        {
            var file = await _store.GetByIdAsync(fileId);
            await _store.DeleteAsync(file);
        }

        public async Task<string> GetFilePathAsync(int fileId)
        {
            var file = await QueryAsync(session => FileMapper.ToModel(session.Query<FileAggregate>()
                .FirstOrDefault(item => item.Id == fileId)));

            return await GetFilePathAsync(file);
        }

        /// <summary>
        /// The physical path of a file on disk.
        /// </summary>
        /// <remarks>
        /// The <c>file.Folder ?? ...</c> fallback the harvested code carried is kept: an unstitched file (the
        /// one <see cref="GetFilePathAsync(int)"/> hands over) resolves its folder through
        /// <see cref="IFolderRepository"/>, while callers that already have a stitched file must not pay for a
        /// second read.
        /// </remarks>
        public async Task<string> GetFilePathAsync(File file)
        {
            if (file == null) return null;
            var folder = file.Folder ?? await _folderRepository.GetFolderAsync(file.FolderId, false);
            var filepath = Path.Combine(await _folderRepository.GetFolderPathAsync(folder), file.Name);
            return filepath;
        }

        private string GetFileUrl(File file, Alias alias)
        {
            return Utilities.FileUrl(alias, file.Folder.Path, file.Name);
        }
    }
}
