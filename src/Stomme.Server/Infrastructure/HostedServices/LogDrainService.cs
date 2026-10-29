using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Drains <see cref="LogQueue"/> and performs the async store writes that
    /// <see cref="ILogManager.Log(Log)"/> used to perform inline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enrichment that needs the REQUEST (alias, user id, url, remote ip) already happened on the calling
    /// thread inside <see cref="LogManager"/> — this service has no request scope and could not reproduce it.
    /// All that is left here is the part that only needs the store: the Log row and its notifications.
    /// </para>
    /// <para>
    /// Each entry gets its OWN DI scope, and the scope's <see cref="ITenantManager"/> is pointed at the
    /// entry's captured routing alias BEFORE any repository is resolved — the same thing the scheduled jobs
    /// do (<c>HostedServiceBase</c>). Without that, a repository in this scope would resolve no tenant and
    /// write to the wrong database or fail. <c>SetAlias(Alias)</c> is a pure SiteState setter and stays
    /// synchronous, which is why it is usable here.
    /// </para>
    /// </remarks>
    public sealed class LogDrainService : BackgroundService
    {
        private readonly LogQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfigManager _config;
        private readonly ILogger<LogDrainService> _filelogger;
        private readonly LogNotificationThrottle _throttle;

        public LogDrainService(LogQueue queue, IServiceScopeFactory scopeFactory, IConfigManager config, ILogger<LogDrainService> filelogger, LogNotificationThrottle throttle)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _config = config;
            _filelogger = filelogger;
            _throttle = throttle;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // NOT stoppingToken. Cancelling the read would abandon whatever is still queued; instead
            // StopAsync completes the writer, ReadAllAsync then drains what remains and ends the loop on its
            // own. That IS the graceful flush. The host's shutdown timeout still bounds it, via StopAsync.
            await foreach (var entry in _queue.Reader.ReadAllAsync(CancellationToken.None))
            {
                await WriteAsync(entry);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // No more entries; let the loop finish the backlog, then wait for it.
            _queue.Complete();
            await base.StopAsync(cancellationToken);
        }

        private async Task WriteAsync(LogEntry entry)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                // Route this scope's repositories at the tenant the log was RAISED in.
                if (entry.RoutingAlias != null)
                {
                    var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();
                    await tenantManager.SetAliasAsync(entry.RoutingAlias);
                }

                var logs = scope.ServiceProvider.GetRequiredService<ILogRepository>();
                await logs.AddLogAsync(entry.Log);

                await SendNotificationAsync(scope.ServiceProvider, entry);
            }
            catch
            {
                // an error occurred writing the log
                // (unchanged from LogManager.Log's original swallow — a logging failure must never take down
                // the request that logged, and there is nowhere left to report it to.)
            }
        }

        /// <remarks>
        /// Moved verbatim from <c>LogManager.SendNotification</c>. It runs AFTER the Log row is written
        /// because the notification body links to <c>log.LogId</c>, which the store assigns.
        /// </remarks>
        private async Task SendNotificationAsync(IServiceProvider provider, LogEntry entry)
        {
            var log = entry.Log;

            Shared.LogLevel notifylevel = Shared.LogLevel.Error;
            var section = _config.GetSection("Logging:LogLevel:Notify");
            if (section.Exists())
            {
                notifylevel = Enum.Parse<Shared.LogLevel>(section.Value);
            }
            // A repeat is held back (LogNotificationThrottle): the entry is in the site's log, the hosts are not mailed again.
            if (Enum.Parse<Shared.LogLevel>(log.Level) >= notifylevel
                && _throttle.ShouldTell(log.SiteId ?? -1, log.Level, string.IsNullOrEmpty(log.MessageTemplate) ? log.Message : log.MessageTemplate, out var heldBack))
            {
                var subject = $"Site {log.Level} Notification";
                var again = heldBack > 0 ? $"<br /><br />Logged {heldBack} more time{(heldBack == 1 ? "" : "s")} since the last notification of it - those were not sent." : "";
                string body = $"Log Message: {log.Message}{again}";

                var alias = entry.DisplayAlias;
                if (alias != null)
                {
                    subject = $"{alias.Name} Site {log.Level} Notification";
                    body = $"Log Message: {log.Message}<br /><br />Please visit {alias.Protocol}{alias.Name}/admin/log?id={log.LogId} for more information{again}";
                }

                var userRoles = provider.GetRequiredService<IUserRoleRepository>();
                var notifications = provider.GetRequiredService<INotificationRepository>();

                foreach (var userrole in await userRoles.GetUserRolesAsync(RoleNames.Host, log.SiteId.Value))
                {
                    var notification = new Notification(log.SiteId.Value, userrole.User, subject, body);
                    await notifications.AddNotificationAsync(notification);
                }
            }
        }
    }
}
