using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    public class SynchronizationJob : HostedServiceBase
    {
        // JobType = "Stomme.Infrastructure.SynchronizationJob, Stomme.Server"

        // synchronization only supports sites in the same tenant (database)
        // module title is used as a key to identify module instances on a page
        // modules must implement ISynchronizable interface for content synchronization
        // change detection does not support deleted items as key values will usually be different due to localization

        // define settings that should not be synchronized (should be extensible in the future)
        List<Setting> excludedSettings = new List<Setting>() {
            new Setting { EntityName = EntityNames.Site, SettingName = "Search_LastIndexedOn" }
        };

        public SynchronizationJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Synchronization Job";
            Frequency = "m"; // minute
            Interval = 1;
            IsEnabled = false;
        }

        // job is executed for each tenant in installation
        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            string log = "";

            var siteGroupRepository = provider.GetRequiredService<ISiteGroupRepository>();
            var siteGroupMemberRepository = provider.GetRequiredService<ISiteGroupMemberRepository>();
            var siteRepository = provider.GetRequiredService<ISiteRepository>();
            var aliasRepository = provider.GetRequiredService<IAliasRepository>();
            var tenantManager = provider.GetRequiredService<ITenantManager>();
            var settingRepository = provider.GetRequiredService<ISettingRepository>();

            List<SiteGroupMember> siteGroupMembers = null;
            List<Site> sites = null;
            List<Alias> aliases = null;

            // get site groups
            var siteGroups = await siteGroupRepository.GetSiteGroupsAsync();

            // iterate through site groups which need to be synchronized
            foreach (var siteGroup in siteGroups.Where(item => item.Synchronize && (item.Type == SiteGroupTypes.Synchronization || item.Type == SiteGroupTypes.ChangeDetection)))
            {
                // get data
                if (siteGroupMembers == null)
                {
                    siteGroupMembers = (await siteGroupMemberRepository.GetSiteGroupMembersAsync()).ToList();
                    sites = (await siteRepository.GetSitesAsync()).ToList();
                    aliases = (await aliasRepository.GetAliasesAsync()).ToList();
                }

                // the tenant cannot change while the job runs (the job is executed per tenant, and SetAlias
                // only ever selects an alias within the same tenant) so it is resolved once per site group
                var tenantId = (await tenantManager.GetTenantAsync()).TenantId;

                var primaryAliasName = "https://" + aliases.First(item => item.TenantId == tenantId && item.SiteId == siteGroup.PrimarySiteId && item.IsDefault).Name;
                log += (siteGroup.Type == SiteGroupTypes.Synchronization) ? "Synchronizing " : "Comparing ";
                log += $"Primary Site: {sites.First(item => item.SiteId == siteGroup.PrimarySiteId).Name} - {CreateLink(primaryAliasName)}<br />";

                // get primary site
                var primarySite = sites.FirstOrDefault(item => item.SiteId == siteGroup.PrimarySiteId);
                if (primarySite != null && !primarySite.IsDeleted)
                {
                    // update flag to prevent job from processing group again
                    siteGroup.Synchronize = false;
                    await siteGroupRepository.UpdateSiteGroupAsync(siteGroup);

                    // iterate through sites in site group
                    foreach (var siteGroupMember in siteGroupMembers.Where(item => item.SiteGroupId == siteGroup.SiteGroupId && item.SiteId != siteGroup.PrimarySiteId))
                    {
                        // get secondary site
                        var secondarySite = sites.FirstOrDefault(item => item.SiteId == siteGroupMember.SiteId);
                        if (secondarySite != null)
                        {
                            // get default alias for site
                            var secondaryAliasName = "https://" + aliases.First(item => item.TenantId == tenantId && item.SiteId == siteGroupMember.SiteId && item.IsDefault).Name;
                            siteGroupMember.AliasName = (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization) ? secondaryAliasName : primaryAliasName;

                            // initialize SynchronizedOn
                            if (siteGroupMember.SynchronizedOn == null)
                            {
                                siteGroupMember.SynchronizedOn = DateTime.MinValue;
                            }

                            // synchronize site
                            var siteLog = await SynchronizeSiteAsync(provider, tenantManager, settingRepository, siteGroupMember, primarySite, secondarySite);
                            if (string.IsNullOrEmpty(siteLog))
                            {
                                siteLog = (siteGroupMember.SynchronizedOn != DateTime.MinValue) ? "No Changes Identified<br />" : "Initialization Complete<br />";
                            }

                            // set synchronized date/time
                            siteGroupMember.SynchronizedOn = DateTime.UtcNow;
                            await siteGroupMemberRepository.UpdateSiteGroupMemberAsync(siteGroupMember);

                            log += $"With Secondary Site: {secondarySite.Name} - {CreateLink(secondaryAliasName)}<br />" + siteLog;
                        }
                        else
                        {
                            log += $"Site Group {siteGroup.Name} Has A SiteId {siteGroupMember.SiteId} Which Does Not Exist<br />";
                        }
                    }
                }
                else
                {
                    log += $"Site Group {siteGroup.Name} Has A PrimarySiteId {siteGroup.PrimarySiteId} Which Does Not Exist Or Is Deleted<br />";
                }
            }

            if (string.IsNullOrEmpty(log))
            {
                log = "No Site Groups Require Synchronization<br />";
            }

            return log;
        }

        private async Task<string> SynchronizeSiteAsync(IServiceProvider provider, ITenantManager tenantManager, ISettingRepository settingRepository, SiteGroupMember siteGroupMember, Site primarySite, Site secondarySite)
        {
            var log = "";

            // synchronize roles
            log += await SynchronizeRolesAsync(provider, settingRepository, siteGroupMember, primarySite.SiteId, secondarySite.SiteId);

            // synchronize folders/files
            log += await SynchronizeFoldersAsync(provider, settingRepository, siteGroupMember, primarySite.SiteId, secondarySite.SiteId);

            // synchronize pages/modules
            log += await SynchronizePagesAsync(provider, settingRepository, tenantManager, siteGroupMember, primarySite.SiteId, secondarySite.SiteId);

            // synchronize site
            if (primarySite.ModifiedOn > siteGroupMember.SynchronizedOn)
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    secondarySite.TimeZoneId = primarySite.TimeZoneId;
                    secondarySite.CultureCode = primarySite.CultureCode;
                    if (secondarySite.LogoFileId != primarySite.LogoFileId)
                    {
                        secondarySite.LogoFileId = await ResolveFileIdAsync(provider, primarySite.LogoFileId, secondarySite.SiteId);
                    }
                    if (secondarySite.FaviconFileId != primarySite.FaviconFileId)
                    {
                        secondarySite.FaviconFileId = await ResolveFileIdAsync(provider, primarySite.FaviconFileId, secondarySite.SiteId); ;
                    }
                    secondarySite.DefaultThemeType = primarySite.DefaultThemeType;
                    secondarySite.DefaultContainerType = primarySite.DefaultContainerType;
                    secondarySite.AdminContainerType = primarySite.AdminContainerType;
                    secondarySite.AdminThemeType = primarySite.AdminThemeType;
                    secondarySite.PwaIsEnabled = primarySite.PwaIsEnabled;
                    if (secondarySite.PwaAppIconFileId != primarySite.PwaAppIconFileId)
                    {
                        secondarySite.PwaAppIconFileId = await ResolveFileIdAsync(provider, primarySite.PwaAppIconFileId, secondarySite.SiteId); ;
                    }
                    if (secondarySite.PwaSplashIconFileId != primarySite.PwaSplashIconFileId)
                    {
                        secondarySite.PwaSplashIconFileId = await ResolveFileIdAsync(provider, primarySite.PwaSplashIconFileId, secondarySite.SiteId); ;
                    }
                    secondarySite.AllowRegistration = primarySite.AllowRegistration;
                    secondarySite.VisitorTracking = primarySite.VisitorTracking;
                    secondarySite.CaptureBrokenUrls = primarySite.CaptureBrokenUrls;
                    secondarySite.SiteGuid = primarySite.SiteGuid;
                    secondarySite.RenderMode = primarySite.RenderMode;
                    secondarySite.Runtime = primarySite.Runtime;
                    secondarySite.Prerender = primarySite.Prerender;
                    secondarySite.Hybrid = primarySite.Hybrid;
                    secondarySite.EnhancedNavigation = primarySite.EnhancedNavigation;
                    secondarySite.Version = primarySite.Version;
                    secondarySite.HeadContent = primarySite.HeadContent;
                    secondarySite.BodyContent = primarySite.BodyContent;
                    secondarySite.CreatedBy = primarySite.CreatedBy;
                    secondarySite.CreatedOn = primarySite.CreatedOn;
                    secondarySite.ModifiedBy = primarySite.ModifiedBy;
                    secondarySite.ModifiedOn = primarySite.ModifiedOn;
                    secondarySite.IsDeleted = primarySite.IsDeleted;
                    secondarySite.DeletedBy = primarySite.DeletedBy;
                    secondarySite.DeletedOn = primarySite.DeletedOn;

                    var siteRepository = provider.GetRequiredService<ISiteRepository>();
                    await siteRepository.UpdateSiteAsync(secondarySite);
                    log += Log(siteGroupMember, $"Site Updated: {secondarySite.Name} - {CreateLink(siteGroupMember.AliasName)}");
                }
                else // change detection
                {
                    log += Log(siteGroupMember, $"Site Updated: {primarySite.Name} - {CreateLink(siteGroupMember.AliasName)}");
                }
            }

            // site settings
            log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Site, primarySite.SiteId, secondarySite.SiteId);

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization && (siteGroupMember.SynchronizedOn == DateTime.MinValue || !string.IsNullOrEmpty(log)))
            {
                // clear cache for secondary site if any content was Synchronized
                var syncManager = provider.GetRequiredService<ISyncManager>();
                var alias = new Alias { TenantId = (await tenantManager.GetTenantAsync()).TenantId, SiteId = secondarySite.SiteId };
                await syncManager.AddSyncEventAsync(alias, EntityNames.Site, secondarySite.SiteId, SyncEventActions.Refresh);
            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.ChangeDetection && !string.IsNullOrEmpty(log))
            {
                // send change log to administrators
                log += await SendNotificationsAsync(provider, siteGroupMember, secondarySite.SiteId, secondarySite.Name, log);
            }

            return log;
        }

        internal async Task<int?> ResolveFileIdAsync(IServiceProvider provider, int? fileId, int siteId)
        {
            if (fileId != null)
            {
                var fileRepository = provider.GetRequiredService<IFileRepository>();

                // Both lookups can return null — a dangling FileId on the primary site, or a file the
                // secondary site does not have at that path+name. Either degrades to "no file" on the
                // secondary rather than throwing inside the background job and aborting the whole sync.
                var file = await fileRepository.GetFileAsync(fileId.Value);
                if (file == null)
                {
                    return null;
                }
                fileId = (await fileRepository.GetFileAsync(siteId, file.Folder.Path, file.Name))?.FileId;
            }
            return fileId;
        }

        private async Task<string> SynchronizeRolesAsync(IServiceProvider provider, ISettingRepository settingRepository, SiteGroupMember siteGroupMember, int primarySiteId, int secondarySiteId)
        {
            // get roles
            var roleRepository = provider.GetRequiredService<IRoleRepository>();
            var primaryRoles = await roleRepository.GetRolesAsync(primarySiteId);
            var secondaryRoles = (await roleRepository.GetRolesAsync(secondarySiteId)).ToList();
            var log = "";

            foreach (var primaryRole in primaryRoles)
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    var role = secondaryRoles.FirstOrDefault(item => item.Name == primaryRole.Name);

                    var secondaryRole = role;
                    if (secondaryRole == null)
                    {
                        secondaryRole = new Role();
                        secondaryRole.SiteId = secondarySiteId;
                    }

                    if (role == null || primaryRole.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        // set all properties
                        secondaryRole.Name = primaryRole.Name;
                        secondaryRole.Description = primaryRole.Description;
                        secondaryRole.IsAutoAssigned = primaryRole.IsAutoAssigned;
                        secondaryRole.IsSystem = primaryRole.IsSystem;

                        if (role == null)
                        {
                            await roleRepository.AddRoleAsync(secondaryRole);
                            log += Log(siteGroupMember, $"Role Added: {secondaryRole.Name}");
                        }
                        else
                        {
                            await roleRepository.UpdateRoleAsync(secondaryRole);
                            log += Log(siteGroupMember, $"Role Updated: {secondaryRole.Name}");
                        }
                    }

                    if (role != null)
                    {
                        secondaryRoles.Remove(role);
                    }
                }
                else // change detection
                {
                    if (primaryRole.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        log += Log(siteGroupMember, $"Role Updated: {primaryRole.Name}");
                    }
                }

            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
            {
                // remove roles in the secondary site which do not exist in the primary site
                foreach (var secondaryRole in secondaryRoles)
                {
                    await roleRepository.DeleteRoleAsync(secondaryRole.RoleId);
                    log += Log(siteGroupMember, $"Role Deleted: {secondaryRole.Name}");
                }
            }

            // settings
            log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Role, primarySiteId, secondarySiteId);

            return log;
        }

        private async Task<string> SynchronizeFoldersAsync(IServiceProvider provider, ISettingRepository settingRepository, SiteGroupMember siteGroupMember, int primarySiteId, int secondarySiteId)
        {
            var folderRepository = provider.GetRequiredService<IFolderRepository>();
            var fileRepository = provider.GetRequiredService<IFileRepository>();
            var log = "";

            // get folders (ignore personalized)
            var primaryFolders = (await folderRepository.GetFoldersAsync(primarySiteId)).Where(item => !item.Path.StartsWith("Users/"));
            var secondaryFolders = (await folderRepository.GetFoldersAsync(secondarySiteId)).Where(item => !item.Path.StartsWith("Users/")).ToList();

            // iterate through folders 
            foreach (var primaryFolder in primaryFolders)
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    var folder = secondaryFolders.FirstOrDefault(item => item.Path == primaryFolder.Path);

                    var secondaryFolder = folder;
                    if (secondaryFolder == null)
                    {
                        secondaryFolder = new Folder();
                        secondaryFolder.SiteId = secondarySiteId;
                    }

                    if (folder == null || primaryFolder.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        // set all properties
                        secondaryFolder.Path = primaryFolder.Path;
                        secondaryFolder.ParentId = null;
                        if (primaryFolder.ParentId != null)
                        {
                            var parentFolder = await folderRepository.GetFolderAsync(secondarySiteId, primaryFolders.First(item => item.FolderId == primaryFolder.ParentId).Path);
                            if (parentFolder != null)
                            {
                                secondaryFolder.ParentId = parentFolder.FolderId;
                            }
                        }
                        secondaryFolder.Type = primaryFolder.Type;
                        secondaryFolder.Name = primaryFolder.Name;
                        secondaryFolder.Order = primaryFolder.Order;
                        secondaryFolder.ImageSizes = primaryFolder.ImageSizes;
                        secondaryFolder.Capacity = primaryFolder.Capacity;
                        secondaryFolder.ImageSizes = primaryFolder.ImageSizes;
                        secondaryFolder.IsSystem = primaryFolder.IsSystem;
                        // OwnRows: only the folder's own rows travel (BL-102); see the module copy below.
                        secondaryFolder.PermissionList = SynchronizePermissions(PermissionRowScope.OwnRows(primaryFolder.PermissionList, EntityNames.Folder), secondarySiteId);

                        if (folder == null)
                        {
                            await folderRepository.AddFolderAsync(secondaryFolder);
                            log += Log(siteGroupMember, $"Folder Added: {secondaryFolder.Path}");
                        }
                        else
                        {
                            await folderRepository.UpdateFolderAsync(secondaryFolder);
                            log += Log(siteGroupMember, $"Folder Updated: {secondaryFolder.Path}");
                        }
                    }

                    if (folder != null)
                    {
                        secondaryFolders.Remove(folder);
                    }

                    // folder settings
                    log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Folder, primaryFolder.FolderId, secondaryFolder.FolderId);

                    // files
                    log += await SynchronizeFilesAsync(provider, folderRepository, fileRepository, siteGroupMember, primaryFolder, secondaryFolder);
                }
                else // change detection
                {
                    if (primaryFolder.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        log += Log(siteGroupMember, $"Folder Updated: {primaryFolder.Path}");

                        // folder settings
                        log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Folder, primaryFolder.FolderId, -1);

                        // files
                        log += await SynchronizeFilesAsync(provider, folderRepository, fileRepository, siteGroupMember, primaryFolder, null);
                    }
                }
            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
            {
                // remove folders in the secondary site which do not exist in the primary site
                foreach (var secondaryFolder in secondaryFolders)
                {
                    await folderRepository.DeleteFolderAsync(secondaryFolder.FolderId);
                    log += Log(siteGroupMember, $"Folder Deleted: {secondaryFolder.Path}");
                }
            }

            return log;
        }

        private async Task<string> SynchronizeFilesAsync(IServiceProvider provider, IFolderRepository folderRepository, IFileRepository fileRepository, SiteGroupMember siteGroupMember, Folder primaryFolder, Folder secondaryFolder)
        {
            var log = "";

            // get files for folder
            var primaryFiles = await fileRepository.GetFilesAsync(primaryFolder.FolderId);
            var secondaryFiles = new List<Models.File>();
            if (secondaryFolder != null)
            {
                secondaryFiles = (await fileRepository.GetFilesAsync(secondaryFolder.FolderId)).ToList();
            }

            foreach (var primaryFile in primaryFiles)
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    var file = secondaryFiles.FirstOrDefault(item => item.Name == primaryFile.Name);

                    var secondaryFile = file;
                    if (secondaryFile == null)
                    {
                        secondaryFile = new Models.File();
                        secondaryFile.FolderId = secondaryFolder.FolderId;
                        secondaryFile.Name = primaryFile.Name;
                    }

                    if (file == null || primaryFile.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        // set all properties
                        secondaryFile.Extension = primaryFile.Extension;
                        secondaryFile.Size = primaryFile.Size;
                        secondaryFile.ImageHeight = primaryFile.ImageHeight;
                        secondaryFile.ImageWidth = primaryFile.ImageWidth;
                        secondaryFile.Description = primaryFile.Description;

                        if (file == null)
                        {
                            await fileRepository.AddFileAsync(secondaryFile);
                            await SynchronizeFileAsync(folderRepository, primaryFolder, primaryFile, secondaryFolder, secondaryFile);
                            log += Log(siteGroupMember, $"File Added: {CreateLink(siteGroupMember.AliasName + "/" + secondaryFolder.Path + secondaryFile.Name)}");
                        }
                        else
                        {
                            await fileRepository.UpdateFileAsync(secondaryFile);
                            await SynchronizeFileAsync(folderRepository, primaryFolder, primaryFile, secondaryFolder, secondaryFile);
                            log += Log(siteGroupMember, $"File Updated: {CreateLink(siteGroupMember.AliasName + "/" + secondaryFolder.Path + secondaryFile.Name)}");
                        }
                    }

                    if (file != null)
                    {
                        secondaryFiles.Remove(file);
                    }
                }
                else // change detection
                {
                    if (primaryFile.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        log += Log(siteGroupMember, $"File Updated: {CreateLink(siteGroupMember.AliasName + "/" + primaryFolder.Path + primaryFile.Name)}");
                    }
                }
            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
            {
                // remove files in the secondary site which do not exist in the primary site
                foreach (var secondaryFile in secondaryFiles)
                {
                    await fileRepository.DeleteFileAsync(secondaryFile.FileId);
                    var secondaryPath = Path.Combine(await folderRepository.GetFolderPathAsync(secondaryFolder), secondaryFile.Name);
                    System.IO.File.Delete(secondaryPath);
                    log += Log(siteGroupMember, $"File Deleted: {CreateLink(siteGroupMember.AliasName + "/" + secondaryFolder.Path + secondaryFile.Name)}");
                }
            }

            return log;
        }

        private async Task SynchronizeFileAsync(IFolderRepository folderRepository, Folder primaryFolder, Models.File primaryFile, Folder secondaryFolder, Models.File secondaryFile)
        {
            var primaryPath = Path.Combine(await folderRepository.GetFolderPathAsync(primaryFolder), primaryFile.Name);
            if (System.IO.File.Exists(primaryPath))
            {
                var secondaryPath = Path.Combine(await folderRepository.GetFolderPathAsync(secondaryFolder), secondaryFile.Name);
                if (!Directory.Exists(Path.GetDirectoryName(secondaryPath)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(secondaryPath));
                }
                System.IO.File.Copy(primaryPath, secondaryPath, true);
            }
        }

        private async Task<string> SynchronizePagesAsync(IServiceProvider provider, ISettingRepository settingRepository, ITenantManager tenantManager, SiteGroupMember siteGroupMember, int primarySiteId, int secondarySiteId)
        {
            var pageRepository = provider.GetRequiredService<IPageRepository>();
            var pageModuleRepository = provider.GetRequiredService<IPageModuleRepository>();
            var moduleRepository = provider.GetRequiredService<IModuleRepository>();
            var log = "";

            int tenantId = (await tenantManager.GetTenantAsync()).TenantId;
            await tenantManager.SetAliasAsync(tenantId, primarySiteId); // required by ModuleDefinitionRepository.LoadModuleDefinitions()
            var primaryPageModules = (await pageModuleRepository.GetPageModulesAsync(primarySiteId)).ToList();
            await tenantManager.SetAliasAsync(tenantId, secondarySiteId); // required by ModuleDefinitionRepository.LoadModuleDefinitions()
            var secondaryPageModules = (await pageModuleRepository.GetPageModulesAsync(secondarySiteId)).ToList();

            // get pages (ignore personalized)
            var primaryPages = (await pageRepository.GetPagesAsync(primarySiteId)).Where(item => item.UserId == null);
            var secondaryPages = (await pageRepository.GetPagesAsync(secondarySiteId)).Where(item => item.UserId == null).ToList();

            // iterate through primary pages 
            foreach (var primaryPage in primaryPages)
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    var page = secondaryPages.FirstOrDefault(item => item.Path == primaryPage.Path);

                    var secondaryPage = page;
                    if (secondaryPage == null)
                    {
                        secondaryPage = new Page();
                        secondaryPage.SiteId = secondarySiteId;
                    }

                    if (page == null || primaryPage.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        // set all properties
                        secondaryPage.Path = primaryPage.Path;
                        secondaryPage.Name = primaryPage.Name;
                        secondaryPage.ParentId = null;
                        if (primaryPage.ParentId != null)
                        {
                            var parentPage = await pageRepository.GetPageAsync(primaryPages.First(item => item.PageId == primaryPage.ParentId).Path, secondarySiteId);
                            if (parentPage != null)
                            {
                                secondaryPage.ParentId = parentPage.PageId;
                            }
                        }
                        secondaryPage.Title = primaryPage.Title;
                        secondaryPage.Order = primaryPage.Order;
                        secondaryPage.Url = primaryPage.Url;
                        secondaryPage.ThemeType = primaryPage.ThemeType;
                        secondaryPage.DefaultContainerType = primaryPage.DefaultContainerType;
                        secondaryPage.HeadContent = primaryPage.HeadContent;
                        secondaryPage.BodyContent = primaryPage.BodyContent;
                        secondaryPage.Icon = primaryPage.Icon;
                        secondaryPage.IsNavigation = primaryPage.IsNavigation;
                        secondaryPage.IsClickable = primaryPage.IsClickable;
                        secondaryPage.UserId = null;
                        secondaryPage.IsPersonalizable = primaryPage.IsPersonalizable;
                        secondaryPage.EffectiveDate = primaryPage.EffectiveDate;
                        secondaryPage.ExpiryDate = primaryPage.ExpiryDate;
                        secondaryPage.CreatedBy = primaryPage.CreatedBy;
                        secondaryPage.CreatedOn = primaryPage.CreatedOn;
                        secondaryPage.ModifiedBy = primaryPage.ModifiedBy;
                        secondaryPage.ModifiedOn = primaryPage.ModifiedOn;
                        secondaryPage.DeletedBy = primaryPage.DeletedBy;
                        secondaryPage.DeletedOn = primaryPage.DeletedOn;
                        secondaryPage.IsDeleted = primaryPage.IsDeleted;
                        // OwnRows: only the page's own rows travel (BL-102); see the module copy below.
                        secondaryPage.PermissionList = SynchronizePermissions(PermissionRowScope.OwnRows(primaryPage.PermissionList, EntityNames.Page), secondarySiteId);

                        if (page == null)
                        {
                            secondaryPage = await pageRepository.AddPageAsync(secondaryPage);
                            log += Log(siteGroupMember, $"Page Added: {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                        }
                        else
                        {
                            secondaryPage = await pageRepository.UpdatePageAsync(secondaryPage);
                            log += Log(siteGroupMember, $"Page Updated: {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                        }
                    }

                    if (page != null)
                    {
                        secondaryPages.Remove(page);
                    }

                    // page settings
                    log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Page, primaryPage.PageId, secondaryPage.PageId);

                    // modules
                    log += await SynchronizeModulesAsync(provider, settingRepository, pageModuleRepository, moduleRepository, siteGroupMember, primaryPageModules, secondaryPageModules, primaryPage, secondaryPage, secondarySiteId);
                }
                else // change detection
                {
                    if (primaryPage.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        log += Log(siteGroupMember, $"Page Updated: {CreateLink(siteGroupMember.AliasName + "/" + primaryPage.Path)}");
                    }

                    // page settings
                    log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Page, primaryPage.PageId, -1);

                    // modules
                    log += await SynchronizeModulesAsync(provider, settingRepository, pageModuleRepository, moduleRepository, siteGroupMember, primaryPageModules, secondaryPageModules, primaryPage, null, secondarySiteId);
                }

            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
            {
                // remove pages in the secondary site which do not exist in the primary site
                foreach (var secondaryPage in secondaryPages)
                {
                    await pageRepository.DeletePageAsync(secondaryPage.PageId);
                    log += Log(siteGroupMember, $"Page Deleted: {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                }
            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization && (siteGroupMember.SynchronizedOn == DateTime.MinValue || !string.IsNullOrEmpty(log)))
            {
                // clear cache for secondary site if any content was Synchronized
                var syncManager = provider.GetRequiredService<ISyncManager>();
                var alias = new Alias { TenantId = (await tenantManager.GetTenantAsync()).TenantId, SiteId = secondarySiteId };
                await syncManager.AddSyncEventAsync(alias, EntityNames.Site, secondarySiteId, SyncEventActions.Refresh);
            }

            return log;
        }

        private async Task<string> SynchronizeModulesAsync(IServiceProvider provider, ISettingRepository settingRepository, IPageModuleRepository pageModuleRepository, IModuleRepository moduleRepository, SiteGroupMember siteGroupMember, List<PageModule> primaryPageModules, List<PageModule> secondaryPageModules, Page primaryPage, Page secondaryPage, int secondarySiteId)
        {
            var log = "";
            var removePageModules = new List<PageModule>();
            if (secondaryPage != null)
            {
                removePageModules = secondaryPageModules.Where(item => item.PageId == secondaryPage.PageId).ToList();
            }

            // iterate through primary modules on primary page
            foreach (var primaryPageModule in primaryPageModules.Where(item => item.PageId == primaryPage.PageId))
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    var pageModule = secondaryPageModules.FirstOrDefault(item => item.PageId == secondaryPage.PageId && item.Module.ModuleDefinitionName == primaryPageModule.Module.ModuleDefinitionName && item.Title.ToLower() == primaryPageModule.Title.ToLower());

                    var secondaryPageModule = pageModule;
                    if (secondaryPageModule == null)
                    {
                        secondaryPageModule = new PageModule();
                        secondaryPageModule.PageId = secondaryPage.PageId;
                        secondaryPageModule.Module = new Module();
                        secondaryPageModule.Module.SiteId = secondarySiteId;
                        secondaryPageModule.Module.ModuleDefinitionName = primaryPageModule.Module.ModuleDefinitionName;
                    }

                    if (pageModule == null || primaryPageModule.ModifiedOn > siteGroupMember.SynchronizedOn || primaryPageModule.Module.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        // set all properties
                        secondaryPageModule.Title = primaryPageModule.Title;
                        secondaryPageModule.Pane = primaryPageModule.Pane;
                        secondaryPageModule.Order = primaryPageModule.Order;
                        secondaryPageModule.ContainerType = primaryPageModule.ContainerType;
                        secondaryPageModule.Header = primaryPageModule.Header;
                        secondaryPageModule.Footer = primaryPageModule.Footer;
                        secondaryPageModule.IsDeleted = primaryPageModule.IsDeleted;
                        // OwnRows (BL-102): the primary's Module.PermissionList is the MERGED list -
                        // PageModuleRepository adds the site-wide (EntityId -1) rows of every 3-segment entry the
                        // definition declares. SynchronizePermissions drops EntityName, so without this filter those
                        // key rows were re-labelled as Module rows on the secondary (a key operation such as
                        // 'manage' granted on one module instance). Declared keys reach a secondary site through that
                        // site's own per-site key reconciliation, not through module sync.
                        secondaryPageModule.Module.PermissionList = SynchronizePermissions(PermissionRowScope.OwnRows(primaryPageModule.Module.PermissionList, EntityNames.Module), secondarySiteId);
                        secondaryPageModule.Module.AllPages = false;
                        secondaryPageModule.Module.IsDeleted = false;

                        if (pageModule == null)
                        {
                            // check if module exists in site (ie. a shared instance)
                            var module = secondaryPageModules.FirstOrDefault(item => item.Module.ModuleDefinitionName == primaryPageModule.Module.ModuleDefinitionName && item.Title.ToLower() == primaryPageModule.Title.ToLower())?.Module;
                            if (module == null)
                            {
                                module = await moduleRepository.AddModuleAsync(secondaryPageModule.Module);
                                log += Log(siteGroupMember, $"Module Added: {secondaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                            }
                            if (module != null)
                            {
                                secondaryPageModule.ModuleId = module.ModuleId;
                                secondaryPageModule.Module = null; // remove tracking
                                secondaryPageModule = await pageModuleRepository.AddPageModuleAsync(secondaryPageModule);
                                log += Log(siteGroupMember, $"Module Instance Added: {secondaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                                secondaryPageModule.Module = module;
                            }
                        }
                        else
                        {
                            // update existing module
                            if (primaryPageModule.Module.ModifiedOn > siteGroupMember.SynchronizedOn)
                            {
                                await moduleRepository.UpdateModuleAsync(secondaryPageModule.Module);
                                log += Log(siteGroupMember, $"Module Updated: {secondaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                            }
                            if (primaryPageModule.ModifiedOn > siteGroupMember.SynchronizedOn)
                            {
                                secondaryPageModule = await pageModuleRepository.UpdatePageModuleAsync(secondaryPageModule);
                                log += Log(siteGroupMember, $"Module Instance Updated: {secondaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                            }
                        }
                    }

                    if (pageModule != null)
                    {
                        removePageModules.Remove(pageModule);
                    }

                    // module settings
                    log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Module, primaryPageModule.ModuleId, secondaryPageModule.ModuleId);

                    // module content
                    log += await SynchronizeModuleContentAsync(provider, siteGroupMember, primaryPageModule, secondaryPageModule, primaryPage, secondaryPage);
                }
                else // change detection
                {
                    if (primaryPageModule.Module.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        log += Log(siteGroupMember, $"Module Updated: {primaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + primaryPage.Path)}");
                    }
                    if (primaryPageModule.ModifiedOn > siteGroupMember.SynchronizedOn)
                    {
                        log += Log(siteGroupMember, $"Module Instance Updated: {primaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + primaryPage.Path)}");
                    }

                    // module settings
                    log += await SynchronizeSettingsAsync(settingRepository, siteGroupMember, EntityNames.Module, primaryPageModule.ModuleId, -1);

                    // module content
                    log += await SynchronizeModuleContentAsync(provider, siteGroupMember, primaryPageModule, null, primaryPage, secondaryPage);
                }
            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
            {
                // remove modules on the secondary page which do not exist on the primary page
                foreach (var secondaryPageModule in removePageModules)
                {
                    await pageModuleRepository.DeletePageModuleAsync(secondaryPageModule.PageModuleId);
                    log += Log(siteGroupMember, $"Module Instance Deleted: {secondaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + secondaryPageModule.Page.Path)}");
                }
            }

            return log;
        }

        private async Task<string> SynchronizeModuleContentAsync(IServiceProvider provider, SiteGroupMember siteGroupMember, PageModule primaryPageModule, PageModule secondaryPageModule, Page primaryPage, Page secondaryPage)
        {
            var log = "";

            if (primaryPageModule.Module.ModuleDefinition.ServerManagerType != "")
            {
                Type moduleType = Type.GetType(primaryPageModule.Module.ModuleDefinition.ServerManagerType);
                if (moduleType != null && moduleType.GetInterface(nameof(ISynchronizable)) != null)
                {
                    try
                    {
                        var moduleObject = ActivatorUtilities.CreateInstance(provider, moduleType);
                        var moduleContent = await ((ISynchronizable)moduleObject).ExtractModuleAsync(primaryPageModule.Module, siteGroupMember.SynchronizedOn.Value);
                        if (!string.IsNullOrEmpty(moduleContent))
                        {
                            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                            {
                                await ((ISynchronizable)moduleObject).LoadModuleAsync(secondaryPageModule.Module, moduleContent);
                                log += Log(siteGroupMember, $"Module Content Updated: {secondaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + secondaryPage.Path)}");
                            }
                            else // change detection
                            {
                                log += Log(siteGroupMember, $"Module Content Updated: {primaryPageModule.Title} - {CreateLink(siteGroupMember.AliasName + "/" + primaryPage.Path)}");
                            }
                        }
                    }
                    catch
                    {
                        // error exporting/importing
                    }
                }
            }

            return log;
        }

        private List<Permission> SynchronizePermissions(List<Permission> permissionList, int siteId)
        {
            return permissionList.Select(item => new Permission
            {
                SiteId = siteId,
                PermissionName = item.PermissionName,
                RoleName = item.RoleName,
                UserId = item.UserId,
                IsAuthorized = item.IsAuthorized,
                CreatedBy = item.CreatedBy,
                CreatedOn = item.CreatedOn,
                ModifiedBy = item.ModifiedBy,
                ModifiedOn = item.ModifiedOn
            }).ToList();
        }

        private async Task<string> SynchronizeSettingsAsync(ISettingRepository settingRepository, SiteGroupMember siteGroupMember, string entityName, int primaryEntityId, int secondaryEntityId)
        {
            var log = "";
            var updated = false;

            var secondarySettings = (await settingRepository.GetSettingsAsync(entityName, secondaryEntityId)).ToList();
            foreach (var primarySetting in await settingRepository.GetSettingsAsync(entityName, primaryEntityId))
            {
                if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
                {
                    var secondarySetting = secondarySettings.FirstOrDefault(item => item.SettingName == primarySetting.SettingName);
                    if (secondarySetting == null)
                    {
                        secondarySetting = new Setting();
                        secondarySetting.EntityName = primarySetting.EntityName;
                        secondarySetting.EntityId = secondaryEntityId;
                        secondarySetting.SettingName = primarySetting.SettingName;
                        secondarySetting.SettingValue = primarySetting.SettingValue;
                        secondarySetting.IsPrivate = primarySetting.IsPrivate;
                        if (!excludedSettings.Any(item => item.EntityName == secondarySetting.EntityName && item.SettingName == secondarySetting.SettingName))
                        {
                            await settingRepository.AddSettingAsync(secondarySetting);
                            updated = true;
                        }
                    }
                    else
                    {
                        if (secondarySetting.SettingValue != primarySetting.SettingValue || secondarySetting.IsPrivate != primarySetting.IsPrivate)
                        {
                            secondarySetting.SettingValue = primarySetting.SettingValue;
                            secondarySetting.IsPrivate = primarySetting.IsPrivate;
                            if (!excludedSettings.Any(item => item.EntityName == secondarySetting.EntityName && item.SettingName == secondarySetting.SettingName))
                            {
                                await settingRepository.UpdateSettingAsync(secondarySetting);
                                updated = true;
                            }
                        }
                        secondarySettings.Remove(secondarySetting);
                    }
                }
                else // change detection
                {
                    if (primarySetting.ModifiedOn > siteGroupMember.SynchronizedOn && !excludedSettings.Any(item => item.EntityName == primarySetting.EntityName && item.SettingName == primarySetting.SettingName))
                    {
                        updated = true;
                    }
                }
            }

            if (siteGroupMember.SiteGroup.Type == SiteGroupTypes.Synchronization)
            {
                // any remaining secondary settings need to be deleted
                foreach (var secondarySetting in secondarySettings)
                {
                    if (!excludedSettings.Any(item => item.EntityName == secondarySetting.EntityName && item.SettingName == secondarySetting.SettingName))
                    {
                        await settingRepository.DeleteSettingAsync(secondarySetting.EntityName, secondarySetting.SettingId);
                        updated = true;
                    }
                }
            }

            if (updated)
            {
                log += Log(siteGroupMember, $"{entityName} Settings Updated");
            }

            return log;
        }

        private async Task<string> SendNotificationsAsync(IServiceProvider provider, SiteGroupMember siteGroupMember, int siteId, string siteName, string changeLog)
        {
            var userRoleRepository = provider.GetRequiredService<IUserRoleRepository>();
            var notificationRepository = provider.GetRequiredService<INotificationRepository>();
            var log = "";

            // get administrators for site
            var userRoles = await userRoleRepository.GetUserRolesAsync(RoleNames.Admin, siteId);
            if (userRoles != null && userRoles.Any())
            {
                foreach (var userRole in userRoles)
                {
                    var notification = new Notification(siteId, userRole.User, $"{siteName} Change Log", changeLog);
                    await notificationRepository.AddNotificationAsync(notification);
                }
                log += Log(siteGroupMember, $"Change Log Sent To Administrators For Secondary Site: {siteName}");
            }
            else
            {
                log += Log(siteGroupMember, $"Error Sending Change Log - Secondary Site {siteName} Does Not Have Any Administrators Defined");
            }

            return log;
        }

        private string Log(SiteGroupMember siteGroupMember, string content)
        {
            // not necessary to log initial synchronization
            if (siteGroupMember.SynchronizedOn != DateTime.MinValue)
            {
                return content + "<br />";
            }
            else
            {
                return "";
            }
        }

        private string CreateLink(string url)
        {
            return "<a href=\"" + url + "\" target=\"_new\">" + url + "</a>";
        }
    }
}
