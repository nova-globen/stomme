#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Backup.Persistence;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Backup.Application.Archive;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Backup.Infrastructure;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Backup.Application
{
    /// <summary>What a pre-flight found. Empty problems: the snapshot can be restored here, now.</summary>
    public sealed record BackupPreflightReport(IReadOnlyList<string> Problems)
    {
        public bool Ok => Problems.Count == 0;
    }

    /// <summary>What the boot phase did with a staged restore.</summary>
    public sealed record BackupBootReport(Guid SnapshotId, bool Restored, bool SafetySnapshotRestored, bool Maintenance, string? Reason);

    /// <summary>The <c>Backup</c> configuration section (off by default - D106).</summary>
    public sealed class BackupOptions
    {
        public const string SectionName = "Backup";

        public bool Enabled { get; set; }

        public ScheduleOptions Schedule { get; set; } = new();

        public sealed class ScheduleOptions
        {
            /// <summary>The scheduled run's passphrase - operator configuration, never a row, a log or an API response.</summary>
            public string Passphrase { get; set; } = string.Empty;

            /// <summary>
            /// A file holding the passphrase, which WINS over <see cref="Passphrase"/> when set - how a container gets it, so the
            /// secret is never in its environment (infra's rule for deployed sites, 2026-09-29).
            /// </summary>
            public string PassphraseFile { get; set; } = string.Empty;

            /// <summary>The setting the passphrase comes from - what a refusal names; never the value.</summary>
            public string SettingName => string.IsNullOrWhiteSpace(PassphraseFile) ? SectionName + ":Schedule:Passphrase" : SectionName + ":Schedule:PassphraseFile";

            /// <summary>
            /// The scheduled run's passphrase: the named file's content when <see cref="PassphraseFile"/> is set (its trailing line
            /// breaks dropped - nothing else, a passphrase may begin or end with a space), else <see cref="Passphrase"/>. Empty when
            /// the file is named but absent, so the caller's check refuses and names the setting.
            /// </summary>
            public string ResolvePassphrase()
            {
                if (!string.IsNullOrWhiteSpace(PassphraseFile))
                {
                    return System.IO.File.Exists(PassphraseFile) ? System.IO.File.ReadAllText(PassphraseFile).TrimEnd('\r', '\n') : string.Empty;
                }

                return Passphrase ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// The backup engine (BL-81, D106 item 7): takes installation snapshots through every participant, checks a snapshot
    /// before anything is written (pre-flight), stages a restore with a SAFETY snapshot, and applies it in the boot phase -
    /// before hosted services start and before the first request, because a middleware gate cannot quiesce an open circuit
    /// or a running job (refutation 11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalogue</b> is read and written in SYSTEM scope and in a fresh DI scope each time: it is installation-wide,
    /// and the File store would otherwise file it under whatever tenant the caller happened to be in.
    /// </para>
    /// <para>
    /// <b>Key custody across the restart.</b> The operator's passphrase is never written down. Staging derives the target
    /// archive's KEY (unique to that archive - its salt) and the safety snapshot's, and writes them to
    /// <c>|DataDirectory|backups/restore.pending.json</c> - beside the databases, never web-served - for the window until the
    /// boot phase has applied the restore and deleted the file. Since 2026-09-24 the file is PROTECTED with the host's
    /// Data Protection key ring (purpose <see cref="RequestProtectorPurpose"/>): a copy of the file alone yields nothing, and a
    /// file written under another host's ring is unreadable, which the boot phase reports rather than guessing.
    /// </para>
    /// </remarks>
    public sealed class BackupEngine(IServiceScopeFactory scopes, BackupArchiveStores archives, IConfigManager config, IDataProtectionProvider dataProtection, ILogger<BackupEngine> logger)
    {
        public const int MinimumPassphraseLength = 12;
        public const string RequestProtectorPurpose = "Stomme.Backup.RestoreRequest";
        public const string SearchWatermarkSetting = "Search_LastIndexedOn";
        private const string RequestFileName = "restore.pending.json";

        private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

        public string RequestFile => Path.Combine(config.GetDataDirectory(), LocalFileBackupArchiveStore.FolderName, RequestFileName);

        public static string? PassphraseProblem(string? passphrase) =>
            string.IsNullOrEmpty(passphrase) || passphrase.Length < MinimumPassphraseLength
                ? $"A backup passphrase has at least {MinimumPassphraseLength} characters."
                : null;

        /// <summary>Takes a snapshot of the whole installation. The row is Available when this returns, Failed if it threw.</summary>
        public async Task<Guid> CreateSnapshotAsync(string passphrase, RetentionTag retentionTag, CancellationToken cancellationToken = default)
        {
            if (PassphraseProblem(passphrase) is { } problem)
            {
                throw new ArgumentException(problem, nameof(passphrase));
            }

            var id = await StartSnapshotAsync(retentionTag, cancellationToken);
            await TakeSnapshotAsync(id, passphrase, cancellationToken);
            return id;
        }

        /// <summary>The row of a snapshot about to be taken (Creating) - what a queued request returns at once.</summary>
        public async Task<Guid> StartSnapshotAsync(RetentionTag retentionTag, CancellationToken cancellationToken = default)
        {
            var snapshot = BackupSnapshotAggregate.Start(Guid.NewGuid(), retentionTag, Constants.Version, DateTime.UtcNow, archives.CurrentName);
            await CatalogueAsync(repository => repository.AddAsync(snapshot, cancellationToken));
            return snapshot.Id;
        }

        /// <summary>Takes the snapshot a <see cref="StartSnapshotAsync"/> row stands for.</summary>
        public async Task TakeSnapshotAsync(Guid id, string passphrase, CancellationToken cancellationToken = default)
        {
            if (PassphraseProblem(passphrase) is { } problem)
            {
                await UpdateAsync(id, row => row.Fail(nameof(ArgumentException), DateTime.UtcNow));
                throw new ArgumentException(problem, nameof(passphrase));
            }

            var version = Constants.Version;
            var snapshot = (await CatalogueAsync(repository => repository.GetByIdAsync(id, cancellationToken)))
                ?? throw new KeyNotFoundException("There is no such snapshot.");
            IBackupArchiveStore? store = null;
            try
            {
                store = archives.For(snapshot);
                var tenantIds = await TenantIdsAsync();
                var scope = new BackupScope(version, tenantIds);
                Checksum checksum;
                long size;
                await using (var file = store.Create(snapshot.StorageLocation))
                {
                    using var hashing = new HashingWriteStream(file);
                    await using (var writer = await BackupArchiveWriter.CreateAsync(hashing, passphrase, cancellationToken))
                    {
                        using var participantScope = scopes.CreateScope();
                        foreach (var participant in Participants(participantScope.ServiceProvider))
                        {
                            await using var part = writer.OpenPart(participant.Name, participant.SchemaVersion, version);
                            await participant.ExportAsync(new ArchivePartWriter(participant.Name, part), scope, cancellationToken);
                        }

                        await writer.CompleteAsync(version, tenantIds, DateTime.UtcNow, cancellationToken);
                    }

                    checksum = hashing.Checksum();
                    size = hashing.Length;
                }

                await store.PublishAsync(snapshot.StorageLocation, checksum, cancellationToken);
                await UpdateAsync(id, row => row.MarkCompleted(checksum, size, DateTime.UtcNow));
                logger.LogInformation("Backup snapshot {SnapshotId} taken: {Size} bytes, tag {Tag}, store {Store}.", id, size, snapshot.RetentionTag, snapshot.ArchiveStore);
            }
            catch (Exception ex)
            {
                store?.Abandon(snapshot.StorageLocation);
                await UpdateAsync(id, row => row.Fail(ex.GetType().Name, DateTime.UtcNow));
                logger.LogError(ex, "Backup snapshot {SnapshotId} failed.", id);
                throw;
            }
        }

        /// <summary>
        /// Checks a snapshot against THIS host, writing nothing: the archive's checksum, its passphrase and every chunk, the
        /// exact framework version, a participant with the same schema for every part and a part for every participant, and
        /// every participant's own validation.
        /// </summary>
        public Task<BackupPreflightReport> PreflightAsync(Guid snapshotId, string passphrase, CancellationToken cancellationToken = default) =>
            PreflightAsync(snapshotId, passphrase, null, cancellationToken);

        /// <summary>
        /// The pre-flight of a restore - of the whole installation, or (BL-171) of <paramref name="tenantIds"/> only. A
        /// partial selection must name tenants the snapshot holds and this installation has, and must not split a
        /// shared physical database (the rows of tenants sharing one database live in the same tables, so one of them
        /// cannot be put back without the others); on a RavenDB install the framework seam shares one database for
        /// every tenant (BL-176), so a partial restore is refused there.
        /// </summary>
        public async Task<BackupPreflightReport> PreflightAsync(Guid snapshotId, string passphrase, IReadOnlyList<int>? tenantIds, CancellationToken cancellationToken = default)
        {
            var problems = new List<string>();
            var snapshot = await CatalogueAsync(repository => repository.GetByIdAsync(snapshotId, cancellationToken));
            if (snapshot is null)
            {
                return new BackupPreflightReport(new[] { "There is no such snapshot." });
            }

            Stream? opened;
            try
            {
                opened = await archives.For(snapshot).OpenReadAsync(snapshot.StorageLocation, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return new BackupPreflightReport(new[] { ex.Message });
            }

            await using var file = opened;
            if (file is null)
            {
                return new BackupPreflightReport(new[] { "The snapshot's archive is missing from the archive store." });
            }

            try
            {
                snapshot.EnsureRestorable(await Checksum.ComputeAsync(file, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return new BackupPreflightReport(new[] { ex.Message });
            }

            file.Position = 0;
            BackupArchiveReader reader;
            try
            {
                reader = await BackupArchiveReader.OpenAsync(file, passphrase, cancellationToken);
            }
            catch (Exception ex) when (ex is BackupArchiveException or ArgumentException)
            {
                return new BackupPreflightReport(new[] { ex.Message });
            }

            using (reader)
            {
                try
                {
                    reader.RequireRestorableOn(Constants.Version, Stomme.Infrastructure.UpgradeManager.StepVersions);
                }
                catch (BackupArchiveException ex)
                {
                    return new BackupPreflightReport(new[] { ex.Message });
                }

                using var participantScope = scopes.CreateScope();
                var selection = await SelectionProblemsAsync(participantScope.ServiceProvider, reader.Manifest, tenantIds);
                if (selection.Problems.Count > 0)
                {
                    return new BackupPreflightReport(selection.Problems);
                }

                var scope = selection.Scope!;
                var participants = Participants(participantScope.ServiceProvider);
                problems.AddRange(PartProblems(reader.Manifest, participants));
                if (problems.Count > 0)
                {
                    return new BackupPreflightReport(problems);
                }

                try
                {
                    await reader.VerifyAsync(cancellationToken);
                }
                catch (BackupArchiveException ex)
                {
                    return new BackupPreflightReport(new[] { ex.Message });
                }

                foreach (var participant in participants)
                {
                    var part = reader.Manifest.Parts.Single(candidate => candidate.Name == participant.Name);
                    var verdict = await participant.ValidateAsync(new ArchivePartReader(reader, part), scope, cancellationToken);
                    problems.AddRange(verdict.Problems.Select(problem => $"{participant.Name}: {problem}"));
                }
            }

            return new BackupPreflightReport(problems);
        }

        /// <summary>
        /// Pre-flight, then a SAFETY snapshot (tag <c>pre-restore</c>), then the snapshot is Staged and the request written.
        /// Nothing is replaced here - the restore is applied by the boot phase after the host restarts.
        /// </summary>
        public Task<BackupPreflightReport> StageRestoreAsync(Guid snapshotId, string passphrase, CancellationToken cancellationToken = default) =>
            StageRestoreAsync(snapshotId, passphrase, null, cancellationToken);

        /// <summary>Pre-flight, safety snapshot, stage - of the whole installation, or (BL-171) of <paramref name="tenantIds"/> only. The safety snapshot is always of the whole installation.</summary>
        public async Task<BackupPreflightReport> StageRestoreAsync(Guid snapshotId, string passphrase, IReadOnlyList<int>? tenantIds, CancellationToken cancellationToken = default)
        {
            if (System.IO.File.Exists(RequestFile))
            {
                return new BackupPreflightReport(new[] { "A restore is already staged; it is applied when the host restarts." });
            }

            var report = await PreflightAsync(snapshotId, passphrase, tenantIds, cancellationToken);
            if (!report.Ok)
            {
                return report;
            }

            var target = (await CatalogueAsync(repository => repository.GetByIdAsync(snapshotId, cancellationToken)))!;
            var targetKey = await KeyOfAsync(target, passphrase, cancellationToken);
            var safetyId = await CreateSnapshotAsync(passphrase, RetentionTag.PreRestore, cancellationToken);
            var safety = (await CatalogueAsync(repository => repository.GetByIdAsync(safetyId, cancellationToken)))!;
            var safetyKey = await KeyOfAsync(safety, passphrase, cancellationToken);

            await UpdateAsync(snapshotId, row => row.Stage(row.GetChecksum()!, DateTime.UtcNow));
            Directory.CreateDirectory(Path.GetDirectoryName(RequestFile)!);
            var selected = tenantIds is { Count: > 0 } ? tenantIds.Distinct().OrderBy(id => id).ToList() : null;
            await WriteRequestAsync(new RestoreRequest
            {
                SnapshotId = snapshotId, Key = Convert.ToBase64String(targetKey),
                SafetySnapshotId = safetyId, SafetyKey = Convert.ToBase64String(safetyKey), StagedOnUtc = DateTime.UtcNow,
                TenantIds = selected,
            }, cancellationToken);
            CryptographicOperations.ZeroMemory(targetKey);
            CryptographicOperations.ZeroMemory(safetyKey);
            logger.LogWarning("Backup restore of {SnapshotId} staged (safety snapshot {SafetyId}; tenants {Tenants}); it is applied when the host restarts.", snapshotId, safetyId, selected is null ? "all" : string.Join(",", selected));
            return report;
        }

        /// <summary>
        /// BL-171: the scope a restore runs under, or the problems with the selection. Null/empty means the whole snapshot.
        /// A partial selection: every id in the snapshot and on this installation; no shared physical database split
        /// (a tenant sharing its database with others is restored with all of them or not at all); not on RavenDB.
        /// </summary>
        private async Task<(BackupScope? Scope, List<string> Problems)> SelectionProblemsAsync(IServiceProvider provider, BackupManifest manifest, IReadOnlyList<int>? tenantIds)
        {
            var problems = new List<string>();
            if (tenantIds is null || tenantIds.Count == 0)
            {
                return (new BackupScope(manifest.FrameworkVersion, manifest.TenantIds), problems);
            }

            var selected = tenantIds.Distinct().OrderBy(id => id).ToList();
            foreach (var id in selected.Where(id => !manifest.TenantIds.Contains(id)))
            {
                problems.Add($"Tenant {id} is not in the snapshot.");
            }

            var tenants = (await provider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
            foreach (var id in selected.Where(id => tenants.All(tenant => tenant.TenantId != id)))
            {
                problems.Add($"Tenant {id} does not exist on this installation.");
            }

            if (problems.Count > 0)
            {
                return (null, problems);
            }

            if (selected.Count == manifest.TenantIds.Count && manifest.TenantIds.All(selected.Contains))
            {
                return (new BackupScope(manifest.FrameworkVersion, manifest.TenantIds), problems);
            }

            if (Type.GetType(config.GetDatabaseType()) is { } databaseType && Activator.CreateInstance(databaseType) is not Stomme.Databases.Interfaces.IRelationalDatabase)
            {
                problems.Add("A tenant-scoped restore is not supported on a RavenDB installation yet: its framework part restores each RavenDB database whole, and tenants created before BL-176 still share the installation's database. Restore the whole installation.");
                return (null, problems);
            }

            // The physical database of each tenant, as the framework participant lays it out: the master tenant's
            // connection is the installation's; every other tenant names its own connection-string setting.
            string Database(Tenant tenant) => tenant.DBType + "|" + (config.GetConnectionString(tenant.DBConnectionString) ?? tenant.DBConnectionString);
            foreach (var group in tenants.GroupBy(Database))
            {
                var inGroup = group.Select(tenant => tenant.TenantId).OrderBy(id => id).ToList();
                var chosen = inGroup.Where(selected.Contains).ToList();
                if (chosen.Count > 0 && chosen.Count < inGroup.Count)
                {
                    problems.Add($"Tenants {string.Join(", ", inGroup)} share one database; a restore of one of them is a restore of all of them. Select all of them or none.");
                }
            }

            return problems.Count > 0 ? (null, problems) : (new BackupScope(manifest.FrameworkVersion, selected, manifest.TenantIds), problems);
        }

        /// <summary>
        /// THE BOOT PHASE: applies a staged restore, if there is one, before hosted services and traffic. On failure the
        /// target becomes RestoreFailed and the safety snapshot is applied the same way; if THAT fails the installation is left
        /// in maintenance (<see cref="BackupBootReport.Maintenance"/>) - the host must not serve.
        /// </summary>
        public async Task<BackupBootReport?> ApplyStagedRestoreAsync(CancellationToken cancellationToken = default)
        {
            if (!System.IO.File.Exists(RequestFile))
            {
                return null;
            }

            var request = await ReadRequestAsync(cancellationToken);
            if (request.Maintenance)
            {
                return new BackupBootReport(request.SnapshotId, false, false, true, "A restore and its safety snapshot both failed earlier; the installation is in maintenance.");
            }

            var reason = await ApplyAsync(request.SnapshotId, Convert.FromBase64String(request.Key), request.TenantIds, cancellationToken);
            if (reason is null)
            {
                System.IO.File.Delete(RequestFile);
                return new BackupBootReport(request.SnapshotId, true, false, false, null);
            }

            logger.LogError("Backup restore of {SnapshotId} failed ({Reason}); applying the safety snapshot {SafetyId}.", request.SnapshotId, reason, request.SafetySnapshotId);
            await UpdateAsync(request.SafetySnapshotId, row => row.Stage(row.GetChecksum()!, DateTime.UtcNow));
            var safetyReason = await ApplyAsync(request.SafetySnapshotId, Convert.FromBase64String(request.SafetyKey), cancellationToken);
            if (safetyReason is null)
            {
                System.IO.File.Delete(RequestFile);
                return new BackupBootReport(request.SnapshotId, false, true, false, reason);
            }

            request.Maintenance = true;
            await WriteRequestAsync(request, cancellationToken);
            logger.LogCritical("Backup restore of {SnapshotId} AND its safety snapshot {SafetyId} failed; the installation is in maintenance.", request.SnapshotId, request.SafetySnapshotId);
            return new BackupBootReport(request.SnapshotId, false, false, true, $"{reason}; safety snapshot: {safetyReason}");
        }

        private IDataProtector RequestProtector => dataProtection.CreateProtector(RequestProtectorPurpose);

        private Task WriteRequestAsync(RestoreRequest request, CancellationToken cancellationToken) =>
            System.IO.File.WriteAllTextAsync(RequestFile, RequestProtector.Protect(JsonSerializer.Serialize(request, RequestJson)), cancellationToken);

        /// <summary>The staged request, or an <see cref="InvalidDataException"/> when the file is not one this host protected.</summary>
        private async Task<RestoreRequest> ReadRequestAsync(CancellationToken cancellationToken)
        {
            string json;
            try
            {
                json = RequestProtector.Unprotect(await System.IO.File.ReadAllTextAsync(RequestFile, cancellationToken));
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException("The staged restore request was not written by this host (its Data Protection ring cannot open it).", ex);
            }

            return JsonSerializer.Deserialize<RestoreRequest>(json, RequestJson)
                ?? throw new InvalidDataException("The staged restore request is unreadable.");
        }

        /// <summary>Applies one staged snapshot. Null on success, the failure's type otherwise (never a raw message).</summary>
        private Task<string?> ApplyAsync(Guid snapshotId, byte[] key, CancellationToken cancellationToken) =>
            ApplyAsync(snapshotId, key, null, cancellationToken);

        private async Task<string?> ApplyAsync(Guid snapshotId, byte[] key, IReadOnlyList<int>? tenantIds, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = (await CatalogueAsync(repository => repository.GetByIdAsync(snapshotId, cancellationToken)))
                    ?? throw new InvalidOperationException("The staged snapshot is no longer in the catalogue.");
                await UpdateAsync(snapshotId, row => row.BeginRestore());
                await using var file = await archives.For(snapshot).OpenReadAsync(snapshot.StorageLocation, cancellationToken)
                    ?? throw new FileNotFoundException("The staged snapshot's archive is missing.");
                using var reader = await BackupArchiveReader.OpenWithKeyAsync(file, key, cancellationToken);
                reader.RequireRestorableOn(Constants.Version, Stomme.Infrastructure.UpgradeManager.StepVersions);
                BackupScope scope;
                using (var participantScope = scopes.CreateScope())
                {
                    // The selection is checked again at boot: the installation may have changed since it was staged.
                    var selection = await SelectionProblemsAsync(participantScope.ServiceProvider, reader.Manifest, tenantIds);
                    if (selection.Problems.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(" ", selection.Problems));
                    }

                    scope = selection.Scope!;
                    var participants = Participants(participantScope.ServiceProvider);
                    if (PartProblems(reader.Manifest, participants).FirstOrDefault() is { } problem)
                    {
                        throw new InvalidOperationException(problem);
                    }

                    foreach (var participant in participants.OrderBy(p => p.RestoreOrder).ThenBy(p => p.Name, StringComparer.Ordinal))
                    {
                        var part = reader.Manifest.Parts.Single(candidate => candidate.Name == participant.Name);
                        await participant.ReplaceAsync(new ArchivePartReader(reader, part), scope, cancellationToken);
                    }
                }

                await AfterRestoreAsync(scope.TenantIds);
                await UpdateAsync(snapshotId, row => row.CompleteRestore(DateTime.UtcNow));
                logger.LogWarning("Backup snapshot {SnapshotId} restored.", snapshotId);
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup restore of {SnapshotId} failed.", snapshotId);
                var reason = ex.GetType().Name;
                try
                {
                    await UpdateAsync(snapshotId, row =>
                    {
                        if (row.Status == BackupSnapshotStatus.Restoring)
                        {
                            row.FailRestore(reason);
                        }
                    });
                }
                catch (Exception catalogue)
                {
                    logger.LogError(catalogue, "The catalogue could not record the failed restore of {SnapshotId}.", snapshotId);
                }

                return reason;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>After a restore: every cache is stale, and the search index was cleared - its watermark goes too.</summary>
        private async Task AfterRestoreAsync(IReadOnlyList<int> tenantIds)
        {
            using (var scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ICacheManager>().ClearAllAsync();
            }

            foreach (var tenantId in tenantIds)
            {
                using var scope = scopes.CreateScope();
                var tenant = (await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).SingleOrDefault(candidate => candidate.TenantId == tenantId);
                if (tenant is null)
                {
                    continue;
                }

                var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();
                tenantManager.SetTenant(tenant);
                foreach (var site in await scope.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync())
                {
                    await tenantManager.SetAliasAsync(tenant.TenantId, site.SiteId);
                    var settings = scope.ServiceProvider.GetRequiredService<ISettingRepository>();
                    if (await settings.GetSettingAsync(EntityNames.Site, site.SiteId, SearchWatermarkSetting) is { } watermark)
                    {
                        await settings.DeleteSettingAsync(EntityNames.Site, watermark.SettingId);
                    }
                }
            }
        }

        /// <summary>A snapshot row Creating for less than <paramref name="lease"/> - one is being taken, by this node or another.</summary>
        public async Task<bool> IsSnapshotInProgressAsync(TimeSpan lease, DateTime nowUtc) =>
            (await CatalogueAsync(repository => repository.ListByStatusAsync(BackupSnapshotStatus.Creating))).Any(row => row.CreatedOnUtc > nowUtc - lease);

        /// <summary>Creating rows older than <paramref name="lease"/>: the process that took them died. Failed, and their partial archives dropped.</summary>
        public async Task<int> FailAbandonedAsync(TimeSpan lease, DateTime nowUtc)
        {
            var abandoned = (await CatalogueAsync(repository => repository.ListByStatusAsync(BackupSnapshotStatus.Creating))).Where(row => row.CreatedOnUtc <= nowUtc - lease).ToList();
            foreach (var row in abandoned)
            {
                try
                {
                    archives.For(row).Abandon(row.StorageLocation);
                }
                catch (InvalidOperationException ex)
                {
                    // The row's store is not configured on this host any more; the row is failed all the same.
                    logger.LogWarning("The partial archive of abandoned snapshot {SnapshotId} could not be removed: {Reason}", row.Id, ex.Message);
                }

                await UpdateAsync(row.Id, snapshot => snapshot.Fail("abandoned", nowUtc));
            }

            return abandoned.Count;
        }

        /// <summary>
        /// Retention (D106 item 8), installation-wide: SCHEDULED snapshots beyond the newest keep-last-N AND older than the
        /// retention days, <c>pre-restore</c> safety snapshots and <c>pre-upgrade</c> snapshots (BL-172) and failed attempts older than the retention days. Manual,
        /// imported and <c>indefinite</c> snapshots are the operator's and are never removed here. With several schedules the
        /// most generous keep-last-N and days apply. Returns how many went.
        /// </summary>
        public async Task<int> ApplyRetentionAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var schedules = await ListSchedulesAsync(cancellationToken);
            if (schedules.Count == 0)
            {
                return 0;
            }

            var keepLast = schedules.Max(schedule => schedule.KeepLast);
            var cutoff = nowUtc.AddDays(-schedules.Max(schedule => schedule.RetentionDays));
            var rows = await ListSnapshotsAsync(cancellationToken);
            var doomed = rows.Where(row => row.RetentionTag == RetentionTag.Scheduled && row.Status == BackupSnapshotStatus.Available)
                .OrderByDescending(row => row.CreatedOnUtc).Skip(keepLast).Where(row => row.CreatedOnUtc < cutoff)
                .Concat(rows.Where(row => (row.RetentionTag == RetentionTag.PreRestore || row.RetentionTag == RetentionTag.PreUpgrade) && row.CanBeDeleted() && row.CreatedOnUtc < cutoff))
                .Concat(rows.Where(row => row.Status == BackupSnapshotStatus.Failed && row.CreatedOnUtc < cutoff && row.RetentionTag != RetentionTag.Indefinite))
                .DistinctBy(row => row.Id)
                .ToList();
            foreach (var row in doomed)
            {
                await DeleteSnapshotAsync(row.Id, cancellationToken);
            }

            return doomed.Count;
        }

        public async Task<IReadOnlyList<BackupScheduleAggregate>> ListSchedulesAsync(CancellationToken cancellationToken = default)
        {
            using var scope = scopes.CreateScope();
            using var system = scope.ServiceProvider.GetService<ITenantScopeFactory>()?.BeginScope(null);
            return await scope.ServiceProvider.GetRequiredService<IBackupScheduleRepository>().ListAsync(cancellationToken);
        }

        /// <summary>Adds the schedule, or saves it when it exists.</summary>
        public async Task SaveScheduleAsync(BackupScheduleAggregate schedule, CancellationToken cancellationToken = default)
        {
            using var scope = scopes.CreateScope();
            using var system = scope.ServiceProvider.GetService<ITenantScopeFactory>()?.BeginScope(null);
            var repository = scope.ServiceProvider.GetRequiredService<IBackupScheduleRepository>();
            if (await repository.GetByIdAsync(schedule.Id, cancellationToken) is null)
            {
                await repository.AddAsync(schedule, cancellationToken);
            }
            else
            {
                await repository.UpdateAsync(schedule, cancellationToken);
            }
        }

        /// <summary>
        /// Brings in an archive taken elsewhere (D106 item 6, refutation 16): streamed into the archive store and hashed, then
        /// read END TO END under the passphrase - every chunk through GCM - before a catalogue row exists. A row is created
        /// only for an archive that verified; anything else is deleted and the reason thrown. The row keeps the instant the
        /// archive was taken; a restore from it still needs this host's version (the pre-flight says so).
        /// </summary>
        public async Task<Guid> ImportArchiveAsync(Stream upload, string passphrase, CancellationToken cancellationToken = default)
        {
            if (PassphraseProblem(passphrase) is { } problem)
            {
                throw new ArgumentException(problem, nameof(passphrase));
            }

            var id = Guid.NewGuid();
            var location = StorageLocation.ForSnapshot(id);
            var storeName = archives.CurrentName;
            var store = archives.Current;
            try
            {
                Checksum checksum;
                long size;
                await using (var file = store.Create(location))
                {
                    using var hashing = new HashingWriteStream(file);
                    await upload.CopyToAsync(hashing, cancellationToken);
                    checksum = hashing.Checksum();
                    size = hashing.Length;
                }

                await store.PublishAsync(location, checksum, cancellationToken);
                string version;
                DateTime takenOn;
                await using (var read = await store.OpenReadAsync(location, cancellationToken) ?? throw new FileNotFoundException("The imported archive is missing."))
                using (var reader = await BackupArchiveReader.OpenAsync(read, passphrase, cancellationToken))
                {
                    await reader.VerifyAsync(cancellationToken);
                    version = reader.Manifest.FrameworkVersion;
                    takenOn = reader.Manifest.CreatedOnUtc;
                }

                var row = BackupSnapshotAggregate.Imported(id, version, takenOn, checksum, size, DateTime.UtcNow, storeName);
                await CatalogueAsync(repository => repository.AddAsync(row, cancellationToken));
                logger.LogInformation("Backup archive imported as {SnapshotId}: {Size} bytes, taken on {Version}.", id, size, version);
                return id;
            }
            catch
            {
                store.Abandon(location);
                try
                {
                    await store.DeleteAsync(location, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    logger.LogWarning(cleanup, "The refused import {Location} could not be removed from the archive store.", location.Value);
                }

                throw;
            }
        }

        /// <summary>The published archive of a snapshot, for a download; null when there is none.</summary>
        public async Task<Stream?> OpenArchiveAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            var snapshot = await GetSnapshotAsync(snapshotId, cancellationToken);
            return snapshot is { Status: not (BackupSnapshotStatus.Creating or BackupSnapshotStatus.Failed) } ? await archives.For(snapshot).OpenReadAsync(snapshot.StorageLocation, cancellationToken) : null;
        }

        /// <summary>Fails a Creating row that will never be taken (its queued work was refused).</summary>
        public Task FailSnapshotAsync(Guid snapshotId, string reason) => UpdateAsync(snapshotId, row => row.Fail(reason, DateTime.UtcNow));

        /// <summary>The catalogue, newest first.</summary>
        public Task<IReadOnlyList<BackupSnapshotAggregate>> ListSnapshotsAsync(CancellationToken cancellationToken = default) =>
            CatalogueAsync(repository => repository.ListAsync(cancellationToken));

        public Task<BackupSnapshotAggregate?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) =>
            CatalogueAsync(repository => repository.GetByIdAsync(snapshotId, cancellationToken));

        public async Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            var snapshot = await CatalogueAsync(repository => repository.GetByIdAsync(snapshotId, cancellationToken))
                ?? throw new KeyNotFoundException("There is no such snapshot.");
            if (!snapshot.CanBeDeleted())
            {
                throw new InvalidOperationException($"A {snapshot.Status} snapshot cannot be deleted.");
            }

            await archives.For(snapshot).DeleteAsync(snapshot.StorageLocation, cancellationToken);
            await CatalogueAsync(repository => repository.DeleteAsync(snapshot, cancellationToken));
        }

        internal static IEnumerable<string> PartProblems(BackupManifest manifest, IReadOnlyList<IBackupParticipant> participants)
        {
            foreach (var part in manifest.Parts)
            {
                var participant = participants.FirstOrDefault(candidate => candidate.Name == part.Name);
                if (participant is null)
                {
                    yield return $"The snapshot's part '{part.Name}' has no participant on this host.";
                }
                else if (!string.Equals(participant.SchemaVersion, part.SchemaVersion, StringComparison.Ordinal))
                {
                    yield return $"'{part.Name}' is schema {part.SchemaVersion} in the snapshot and {participant.SchemaVersion} here.";
                }
            }

            foreach (var participant in participants.Where(candidate => manifest.Parts.All(part => part.Name != candidate.Name)))
            {
                yield return $"The snapshot has no part for '{participant.Name}' - it was added to this host after the snapshot was taken.";
            }
        }

        /// <summary>The registered participants, in restore order. Two with one name is a composition error, never a guess.</summary>
        private static IReadOnlyList<IBackupParticipant> Participants(IServiceProvider provider)
        {
            var participants = provider.GetServices<IBackupParticipant>().OrderBy(p => p.RestoreOrder).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
            var duplicate = participants.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"Two backup participants are named '{duplicate.Key}'.");
            }

            return participants;
        }

        private async Task<IReadOnlyList<int>> TenantIdsAsync()
        {
            using var scope = scopes.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Select(tenant => tenant.TenantId).OrderBy(id => id).ToList();
        }

        private async Task<byte[]> KeyOfAsync(BackupSnapshotAggregate snapshot, string passphrase, CancellationToken cancellationToken)
        {
            await using var file = await archives.For(snapshot).OpenReadAsync(snapshot.StorageLocation, cancellationToken) ?? throw new FileNotFoundException("The archive is missing.");
            return await BackupArchiveReader.DeriveKeyAsync(file, passphrase, cancellationToken);
        }

        private async Task<T> CatalogueAsync<T>(Func<IBackupSnapshotRepository, Task<T>> action)
        {
            using var scope = scopes.CreateScope();
            using var system = scope.ServiceProvider.GetService<ITenantScopeFactory>()?.BeginScope(null);
            return await action(scope.ServiceProvider.GetRequiredService<IBackupSnapshotRepository>());
        }

        private Task CatalogueAsync(Func<IBackupSnapshotRepository, Task> action) =>
            CatalogueAsync(async repository => { await action(repository); return 0; });

        private Task UpdateAsync(Guid snapshotId, Action<BackupSnapshotAggregate> change) =>
            CatalogueAsync(async repository =>
            {
                var row = await repository.GetByIdAsync(snapshotId) ?? throw new KeyNotFoundException("The snapshot left the catalogue.");
                change(row);
                await repository.UpdateAsync(row);
            });

        private sealed class RestoreRequest
        {
            public Guid SnapshotId { get; set; }

            public string Key { get; set; } = string.Empty;

            public Guid SafetySnapshotId { get; set; }

            public string SafetyKey { get; set; } = string.Empty;

            public DateTime StagedOnUtc { get; set; }

            public bool Maintenance { get; set; }

            /// <summary>BL-171: the tenants to put back; null for the whole snapshot.</summary>
            public List<int>? TenantIds { get; set; }
        }

        /// <summary>Passes writes through, hashing them - the archive's checksum without reading the file back.</summary>
        private sealed class HashingWriteStream(Stream inner) : Stream
        {
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private long _length;

            public override long Length => _length;

            public Checksum Checksum() => Domain.Checksum.Create(Convert.ToHexStringLower(_hash.GetCurrentHash()));

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _hash.AppendData(buffer);
                _length += buffer.Length;
                inner.Write(buffer);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                _hash.AppendData(buffer.Span);
                _length += buffer.Length;
                await inner.WriteAsync(buffer, cancellationToken);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Position { get => _length; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _hash.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
