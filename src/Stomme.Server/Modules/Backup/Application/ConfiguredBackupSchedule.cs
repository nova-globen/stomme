#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Modules.Backup.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Backup.Application
{
    /// <summary>
    /// The backup schedule an unattended host is given by configuration (BL-258, infra ruling C30):
    /// <c>Backup:Schedule:IntervalHours</c>, <c>RetentionDays</c> and <c>KeepLast</c>, settable as environment variables alone
    /// (<c>Backup__Schedule__IntervalHours</c>, ...). Without it a schedule exists only as a row the Host creates at admin/backup,
    /// and the Backup Schedule Job is registered disabled - so a host nobody clicks through took no backup at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule: configuration seeds the FIRST schedule, and only that.</b> On a boot that finds NO schedule, one is created
    /// from the configuration and the job is enabled (its row added enabled, or a disabled row enabled). On a boot that finds
    /// any schedule - seeded or the Host's - nothing is written: a restart never duplicates one, and an edit the Host makes
    /// afterwards (a new interval, the schedule switched off, the job switched off) is kept. To apply a changed configuration,
    /// edit the schedule at admin/backup. Schedules are excluded from snapshots, so a restore does not bring one back.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> A configured schedule that is out of range, or whose passphrase is not set, refuses the boot by
    /// the setting's name (never a value), with nothing written. <c>Backup:Enabled</c> stays the master switch: with it off the
    /// schedule settings are ignored, and said so in the log.
    /// </para>
    /// </remarks>
    public static class ConfiguredBackupSchedule
    {
        public const int DefaultRetentionDays = 30;
        public const int DefaultKeepLast = 7;

        private const string Prefix = BackupOptions.SectionName + ":Schedule:";

        /// <summary>Whether any of the three schedule settings is given.</summary>
        public static bool IsConfigured(BackupOptions options) =>
            options.Schedule.IntervalHours is not null || options.Schedule.RetentionDays is not null || options.Schedule.KeepLast is not null;

        /// <summary>What is wrong with the configured schedule, each by its setting's name. Empty: nothing to refuse.</summary>
        public static IReadOnlyList<string> Problems(BackupOptions options)
        {
            var problems = new List<string>();
            if (!options.Enabled || !IsConfigured(options))
            {
                return problems;
            }

            var schedule = options.Schedule;
            if (schedule.IntervalHours is not { } interval)
            {
                problems.Add($"{Prefix}IntervalHours is required when {Prefix}RetentionDays or {Prefix}KeepLast is set.");
            }
            else if (interval < 1 || interval > BackupScheduleAggregate.MaximumIntervalHours)
            {
                problems.Add($"{Prefix}IntervalHours is 1-{BackupScheduleAggregate.MaximumIntervalHours}.");
            }

            if (schedule.RetentionDays is < 1)
            {
                problems.Add($"{Prefix}RetentionDays is at least 1.");
            }

            if (schedule.KeepLast is < 1)
            {
                problems.Add($"{Prefix}KeepLast is at least 1.");
            }

            if (BackupEngine.PassphraseProblem(schedule.ResolvePassphrase()) is not null)
            {
                problems.Add($"{schedule.SettingName} is not set (12+ characters); a scheduled backup cannot be taken without it.");
            }

            return problems;
        }

        /// <summary>
        /// Seeds the first schedule and enables the job, by the rule above. Returns a refusal (each problem by name) or null.
        /// </summary>
        public static async Task<string?> SeedAsync(IServiceProvider services, DateTime nowUtc)
        {
            var options = services.GetService<BackupOptions>();
            if (options is null || !IsConfigured(options))
            {
                return null;
            }

            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConfiguredBackupSchedule));
            if (!options.Enabled)
            {
                logger.LogWarning("{Prefix}* is set but {Section}:Enabled is not true, so no backup schedule was seeded.", Prefix, BackupOptions.SectionName);
                return null;
            }

            var problems = Problems(options);
            if (problems.Count > 0)
            {
                return "The configured backup schedule is not valid, so nothing was seeded: " + string.Join(" ", problems);
            }

            using var scope = services.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<BackupEngine>();
            if ((await engine.ListSchedulesAsync()).Count > 0)
            {
                logger.LogInformation("A backup schedule exists, so the configured one was not seeded; edit it at admin/backup.");
                return null;
            }

            // The job first: a failure after it leaves no schedule, so the next boot tries both again.
            await EnableJobAsync(scope.ServiceProvider);
            var seeded = BackupScheduleAggregate.Create(
                options.Schedule.IntervalHours!.Value,
                options.Schedule.RetentionDays ?? DefaultRetentionDays,
                options.Schedule.KeepLast ?? DefaultKeepLast,
                enabled: true,
                nowUtc);
            await engine.SaveScheduleAsync(seeded);
            logger.LogInformation("Seeded the backup schedule from configuration: every {Interval} hours, kept {Days} days, at least {Keep}.",
                seeded.IntervalHours, seeded.RetentionDays, seeded.KeepLast);
            return null;
        }

        private static async Task EnableJobAsync(IServiceProvider services)
        {
            var jobs = services.GetRequiredService<IJobRepository>();
            var jobType = Utilities.GetFullTypeName(typeof(BackupScheduleJob).AssemblyQualifiedName);
            var job = await jobs.GetJobAsync(jobType);
            if (job is null)
            {
                // The row HostedServiceBase would register, but enabled.
                var template = new BackupScheduleJob(services.GetRequiredService<IServiceScopeFactory>());
                await jobs.AddJobAsync(new Job
                {
                    JobType = jobType,
                    Name = template.Name,
                    Frequency = template.Frequency,
                    Interval = template.Interval,
                    StartDate = template.StartDate,
                    EndDate = template.EndDate,
                    MaximumDuration = template.MaximumDuration,
                    RetentionHistory = template.RetentionHistory,
                    IsEnabled = true,
                    IsStarted = true,
                    IsExecuting = false,
                    NextExecution = null
                });
            }
            else if (!job.IsEnabled)
            {
                job.IsEnabled = true;
                await jobs.UpdateJobAsync(job);
            }
        }
    }
}
