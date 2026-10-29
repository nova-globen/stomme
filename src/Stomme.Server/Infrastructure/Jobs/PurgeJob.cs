using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    public class PurgeJob : HostedServiceBase
    {
        // JobType = "Stomme.Infrastructure.PurgeJob, Stomme.Server"

        public PurgeJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Purge Job";
            Frequency = "d"; // daily
            Interval = 1;
            StartDate = DateTime.ParseExact("03:00", "H:mm", null, System.Globalization.DateTimeStyles.AssumeLocal).ToUniversalTime(); // 3 AM
            IsEnabled = true;
        }

        // job is executed for each tenant in installation
        // (async because resolving a feature flag reads the flag store)
        /// <summary>The fallback when a retention setting cannot be read as whole days.</summary>
        private const int DefaultRetentionDays = 30;

        /// <summary>The retention days for <paramref name="key"/>, in whole days, never throwing.</summary>
        /// <remarks>
        /// These four settings were read with <c>int.Parse</c> OUTSIDE the try blocks meant to contain a failing
        /// purge, and the per-site loop has no catch of its own - so one unparseable value (the admin pages use a
        /// number input, but the Host settings import, the Setting API and the database do not) threw out of
        /// ExecuteJobAsync, skipped every LATER site and every later tenant, and replaced the job notes with the
        /// exception message alone. A value that is not a whole number, or is negative (which would purge rows
        /// dated in the FUTURE - everything), falls back to the 30-day default and says so. Zero is kept: the admin
        /// pages allow it, and it means "purge everything older than now". The note names the SETTING, never its
        /// value: these rows are private.
        /// </remarks>
        private static int Retention(ISettingRepository settingRepository, IEnumerable<Setting> settings, string key, ref string log, int fallback = DefaultRetentionDays)
        {
            var raw = settingRepository.GetSettingValue(settings, key, fallback.ToString());
            if (int.TryParse(raw, out var days) && days >= 0)
            {
                return days;
            }

            log += $"The {key} setting is not a whole number of days - using the {fallback}-day default<br />";
            return fallback;
        }
        public async override Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            string log = "";

            // get services
            var siteRepository = provider.GetRequiredService<ISiteRepository>();
            var settingRepository = provider.GetRequiredService<ISettingRepository>();
            var logRepository = provider.GetRequiredService<ILogRepository>();
            var visitorRepository = provider.GetRequiredService<IVisitorRepository>();
            var notificationRepository = provider.GetRequiredService<INotificationRepository>();
            var urlMappingRepository = provider.GetRequiredService<IUrlMappingRepository>();
            var siteTaskRepository = provider.GetRequiredService<ISiteTaskRepository>();
            var features = provider.GetRequiredService<IFeatureEvaluator>();
            var environment = provider.GetRequiredService<IWebHostEnvironment>();
            var tenantManager = provider.GetRequiredService<ITenantManager>();

            // Contributed retention passes. Resolved ONCE per tenant rather than per site: the set cannot
            // change inside a run, and resolving per site would build a module's purger - and whatever it injects -
            // once for every site on the install.
            var purgers = provider.GetServices<IRetentionPurger>().ToList();

            // Visitor retention follows the visitor-tracking capability: read once per run, because a
            // flag is installation-wide and the evaluator reads the flag store on every call.
            var visitorTracking = await features.IsEnabledAsync(FeatureCatalog.VisitorTracking);

            // iterate through sites for current tenant
            List<Site> sites = (await siteRepository.GetSitesAsync()).ToList();
            foreach (Site site in sites.Where(item => !item.IsDeleted))
            {
                log += "<br />Processing Site: " + site.Name + "<br />";
                int count;

                // get site settings
                var settings = await settingRepository.GetSettingsAsync(EntityNames.Site, site.SiteId, EntityNames.Host, -1);

                // purge event log
                var retention = Retention(settingRepository, settings, "LogRetention", ref log);
                try
                {
                    count = await logRepository.DeleteLogsAsync(site.SiteId, retention);
                    log += count.ToString() + " Events Purged<br />";
                }
                catch (Exception ex)
                {
                    log += $"Error Purging Events - {ex.Message}<br />";
                }

                // purge visitors
                // The per-site VisitorTracking column still narrows the capability to a site; the flag is the
                // installation-wide switch. The column is retained for backward compatibility.
                if (site.VisitorTracking && visitorTracking)
                {
                    retention = Retention(settingRepository, settings, "VisitorRetention", ref log);
                    try
                    {
                        count = await visitorRepository.DeleteVisitorsAsync(site.SiteId, retention);
                        log += count.ToString() + " Visitors Purged<br />";
                    }
                    catch (Exception ex)
                    {
                        log += $"Error Purging Visitors - {ex.Message}<br />";
                    }
                }

                // purge notifications
                retention = Retention(settingRepository, settings, "NotificationRetention", ref log);
                try
                {
                    count = await notificationRepository.DeleteNotificationsAsync(site.SiteId, retention);
                    log += count.ToString() + " Notifications Purged<br />";
                }
                catch (Exception ex)
                {
                    log += $"Error Purging Notifications - {ex.Message}<br />";
                }

                // purge broken urls 
                retention = Retention(settingRepository, settings, "UrlMappingRetention", ref log);
                try
                {
                    count = await urlMappingRepository.DeleteUrlMappingsAsync(site.SiteId, retention);
                    log += count.ToString() + " Broken Urls Purged<br />";
                }
                catch (Exception ex)
                {
                    log += $"Error Purging Broken Urls - {ex.Message}<br />";
                }

                // purge completed site tasks
                retention = 30; // 30 day default
                try
                {
                    count = await siteTaskRepository.DeleteSiteTasksAsync(site.SiteId, retention);
                    log += count.ToString() + " Completed Tasks Purged<br />";
                }
                catch (Exception ex)
                {
                    log += $"Error Purging Completed Site Tasks - {ex.Message}<br />";
                }

                // purge orphaned upload parts and dead staging directories (BL-100 F1, absorbing BL-114): the site's
                // staging tree, and the chunk parts the pre-guard upload left in its content folders
                try
                {
                    var window = FileIngestionOptions.FromConfiguration(provider.GetService<IConfigurationRoot>()).ChunkOrphanWindow;
                    var tenantId = (await tenantManager.GetTenantAsync()).TenantId;
                    count = await ChunkOrphanSweeper.SweepSiteAsync(environment.ContentRootPath, environment.WebRootPath, tenantId, site.SiteId, window, DateTime.UtcNow);
                    log += count.ToString() + " Orphaned Upload Parts Purged<br />";
                }
                catch (Exception ex)
                {
                    log += $"Error Purging Orphaned Upload Parts - {ex.Message}<br />";
                }

                // purge whatever the modules contributed: same site, same notes shape, same containment as
                // the five framework passes above. A module with rows to age out gets this instead of a second Job
                // row an operator can disable without knowing what they switched off. GetServices, not
                // GetRequiredService: an install with no contributing module resolves an EMPTY sequence and this
                // whole block is a no-op - which is why a stock install's notes are unchanged.
                foreach (var purger in purgers)
                {
                    // Read, parse and default the setting HERE, so that one module cannot answer "what does an
                    // unparseable retention value mean" differently from the framework. Any floor or
                    // ceiling belongs to the purger and is applied inside PurgeAsync.
                    var purgerRetention = Retention(settingRepository, settings, purger.SettingKey, ref log, purger.DefaultDays);
                    try
                    {
                        // No token: ExecuteJobAsync is handed none (BL-107 gave the tenant loop the job's token,
                        // not the body).
                        count = await purger.PurgeAsync(site.SiteId, purgerRetention, default);
                        log += count.ToString() + " " + purger.DisplayName + " Purged<br />";
                    }
                    catch (Exception ex)
                    {
                        // Contained per PURGER, not per site: one module's failure must not cost the other modules
                        // their pass, nor the site its remaining ones.
                        log += $"Error Purging {purger.DisplayName} - {ex.Message}<br />";
                    }
                }
            }

            return log;
        }
    }
}
