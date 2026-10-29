#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.CloudUsage.Application
{
    /// <summary>
    /// The alert switches (<c>CloudUsage:Alerts</c>, read from the HOST's configuration - the override file included):
    /// OFF by default (a stock install gains no job work), a 7-day anomaly baseline and a 20 % spike.
    /// </summary>
    public sealed class CloudUsageAlertOptions
    {
        public const string SectionName = "CloudUsage:Alerts";

        public bool Enabled { get; set; }

        public int AnomalyLookbackDays { get; set; } = 7;

        public decimal AnomalyThresholdPercent { get; set; } = 20m;

        public static CloudUsageAlertOptions From(IConfiguration? configuration)
        {
            var options = new CloudUsageAlertOptions();
            configuration?.GetSection(SectionName).Bind(options);
            return options;
        }
    }

    /// <summary>
    /// A site's cloud-cost alerts: each budget's month-to-date
    /// spend against 80 % (Warning) and 100 % (Critical) - one alert per pass, the highest newly crossed threshold, every
    /// crossed threshold marked so a jump past 100 % never back-fills an 80 % warning - and the latest full day against the
    /// rolling average per currency. Every alert is raised once: a marker keyed by site and key, written only after the
    /// dispatch succeeded, so a failed dispatch is retried by the next pass.
    /// </summary>
    /// <remarks>
    /// <b>No address list.</b> Dispatching to e-mail addresses, user ids and webhook endpoints
    /// taken from configuration would be an open relay by another name, which the notification dispatcher refuses. The recipients are
    /// the SITE's operators by user id (<see cref="SiteOperators"/>: its administrators and the Host Users - a fresh install's
    /// site has no administrator at all), as the metering and health alerts address them; with none, nothing is sent
    /// and the pass says so in the log.
    /// </remarks>
    public sealed class CloudCostAlertService(
        ICloudCostRecordRepository records,
        ICloudBudgetRepository budgets,
        ICloudCostAlertMarkerRepository markers,
        IUnitOfWork unitOfWork,
        IUserRoleRepository userRoles,
        ILogger<CloudCostAlertService> logger,
        TimeProvider clock,
        INotificationDispatcher? dispatcher = null)
    {
        public const string BudgetTemplateKey = "cloud-cost.budget-threshold";
        public const string AnomalyTemplateKey = "cloud-cost.anomaly";
        public const string Topic = "cloud-cost-alerts";

        public static readonly IReadOnlyList<int> BudgetThresholds = new[] { 100, 80 };

        public async Task<int> EvaluateAsync(TenantId tenantId, int siteId, CloudUsageAlertOptions options, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(options);
            if (!options.Enabled)
            {
                return 0;
            }

            if (dispatcher is null)
            {
                logger.LogInformation("Cloud-cost alerts on site {SiteId}: no notification dispatcher is registered, so nobody is told.", siteId);
                return 0;
            }

            var recipients = (await userRoles.GetSiteOperatorIdsAsync(siteId)).ToList();
            if (recipients.Count == 0)
            {
                logger.LogWarning("Cloud-cost alerts on site {SiteId}: the site has no operator to tell; nothing was evaluated.", siteId);
                return 0; // nothing configured to receive - evaluating would dispatch to no one
            }

            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var lookbackDays = options.AnomalyLookbackDays > 0 ? options.AnomalyLookbackDays : 7;
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var windowStart = new[] { monthStart, today.AddDays(-1 - lookbackDays) }.Min();
            var rows = await records.ListForSiteAsync(siteId, CloudCostRecordAggregate.ToStoredDate(windowStart), CloudCostRecordAggregate.ToStoredDate(today.AddDays(1)), ct);

            var dispatched = 0;
            dispatched += await EvaluateBudgetsAsync(tenantId, siteId, rows, today, recipients, ct);
            dispatched += await EvaluateAnomaliesAsync(tenantId, siteId, rows, today, lookbackDays, options.AnomalyThresholdPercent, recipients, ct);
            return dispatched;
        }

        private async Task<int> EvaluateBudgetsAsync(TenantId tenantId, int siteId, IReadOnlyList<CloudCostRecordAggregate> rows, DateOnly today, IReadOnlyList<int> recipients, CancellationToken ct)
        {
            var monthStart = CloudCostRecordAggregate.ToStoredDate(new DateOnly(today.Year, today.Month, 1));
            var nextMonth = CloudCostRecordAggregate.ToStoredDate(new DateOnly(today.Year, today.Month, 1).AddMonths(1));
            var monthKey = new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var dispatched = 0;
            foreach (var budget in await budgets.ListForSiteAsync(siteId, ct))
            {
                // Budget vs actual stays currency- and scope-honest, exactly like the budget-status query.
                var actual = rows.Where(r => r.DateUtc >= monthStart && r.DateUtc < nextMonth && r.Currency == budget.Currency && (budget.Provider is null || r.Provider == budget.Provider)).Sum(r => r.Cost);
                var percentUsed = actual / budget.MonthlyAmount * 100m;
                var crossed = new List<int>();
                foreach (var threshold in BudgetThresholds)
                {
                    if (percentUsed >= threshold && !await markers.WasRaisedAsync(tenantId, siteId, BudgetKey(budget.Id, threshold, monthKey), ct))
                    {
                        crossed.Add(threshold);
                    }
                }

                if (crossed.Count == 0)
                {
                    continue;
                }

                var top = crossed.Max();
                var sent = await TryDispatchAsync(new NotificationDispatchRequest(
                    BudgetTemplateKey,
                    Recipients(recipients),
                    new[] { NotificationChannelKind.InApp },
                    new[]
                    {
                        new DispatchParameter("month", monthKey),
                        new DispatchParameter("provider", budget.Provider?.ToString() ?? "all providers"),
                        new DispatchParameter("currency", budget.Currency),
                        new DispatchParameter("budgetAmount", Invariant(budget.MonthlyAmount)),
                        new DispatchParameter("actualSpend", Invariant(actual)),
                        new DispatchParameter("percentUsed", Invariant(Math.Round(percentUsed, 1))),
                        new DispatchParameter("threshold", top.ToString(CultureInfo.InvariantCulture)),
                    },
                    top >= 100 ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                    Topic), ct);
                if (!sent)
                {
                    continue; // dispatch failed - left unmarked so the next pass retries
                }

                dispatched++;
                var raisedUtc = clock.GetUtcNow().UtcDateTime;
                foreach (var threshold in crossed)
                {
                    await markers.AddAsync(CloudCostAlertMarkerAggregate.Raise(tenantId, siteId, BudgetKey(budget.Id, threshold, monthKey), raisedUtc), ct);
                }

                await unitOfWork.SaveChangesAsync(ct);
            }

            return dispatched;
        }

        private async Task<int> EvaluateAnomaliesAsync(TenantId tenantId, int siteId, IReadOnlyList<CloudCostRecordAggregate> rows, DateOnly today, int lookbackDays, decimal thresholdPercent,
            IReadOnlyList<int> recipients, CancellationToken ct)
        {
            var day = today.AddDays(-1); // the latest full day
            var dayUtc = CloudCostRecordAggregate.ToStoredDate(day);
            var baselineFrom = CloudCostRecordAggregate.ToStoredDate(day.AddDays(-lookbackDays));
            var dayKey = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dispatched = 0;

            // Per ISO 4217 currency - spend in different currencies is never summed together.
            foreach (var group in rows.GroupBy(r => r.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var baseline = group.Where(r => r.DateUtc >= baselineFrom && r.DateUtc < dayUtc).Sum(r => r.Cost);
                var average = baseline / lookbackDays;
                if (average <= 0)
                {
                    continue; // no meaningful baseline to compare against
                }

                var daySpend = group.Where(r => r.DateUtc == dayUtc).Sum(r => r.Cost);
                var deltaPercent = (daySpend - average) / average * 100m;
                var key = $"anomaly:{group.Key}:{dayKey}";
                if (deltaPercent <= thresholdPercent || await markers.WasRaisedAsync(tenantId, siteId, key, ct))
                {
                    continue;
                }

                var sent = await TryDispatchAsync(new NotificationDispatchRequest(
                    AnomalyTemplateKey,
                    Recipients(recipients),
                    new[] { NotificationChannelKind.InApp },
                    new[]
                    {
                        new DispatchParameter("date", dayKey),
                        new DispatchParameter("currency", group.Key),
                        new DispatchParameter("spend", Invariant(daySpend)),
                        new DispatchParameter("average", Invariant(Math.Round(average, 2))),
                        new DispatchParameter("deltaPercent", Invariant(Math.Round(deltaPercent, 1))),
                        new DispatchParameter("thresholdPercent", Invariant(thresholdPercent)),
                        new DispatchParameter("lookbackDays", lookbackDays.ToString(CultureInfo.InvariantCulture)),
                    },
                    NotificationSeverity.Warning,
                    Topic), ct);
                if (!sent)
                {
                    continue;
                }

                dispatched++;
                await markers.AddAsync(CloudCostAlertMarkerAggregate.Raise(tenantId, siteId, key, clock.GetUtcNow().UtcDateTime), ct);
                await unitOfWork.SaveChangesAsync(ct);
            }

            return dispatched;
        }

        public static string BudgetKey(Guid budgetId, int threshold, string monthKey) => $"budget:{budgetId:N}:{threshold}:{monthKey}";

        // Best-effort like every notification producer: a failure is logged, never thrown, and the alert stays unmarked.
        private async Task<bool> TryDispatchAsync(NotificationDispatchRequest request, CancellationToken ct)
        {
            try
            {
                await dispatcher!.DispatchAsync(request, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to dispatch the {TemplateKey} cloud-cost alert.", request.TemplateKey);
                return false;
            }
        }

        private static IReadOnlyList<DispatchRecipient> Recipients(IReadOnlyList<int> userIds) =>
            userIds.Select(id => new DispatchRecipient(NotificationRecipientKind.User, id.ToString(CultureInfo.InvariantCulture))).ToList();

        private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The daily alert pass: each site of the tenant in its own DI
    /// scope with its alias set, gated at run time by <c>CloudUsage:Alerts:Enabled</c> read from the HOST's configuration.
    /// A HostedServiceBase found by the framework's assembly scan, as every job is.
    /// </summary>
    public class CloudCostAlertJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.CloudUsage.Application.CloudCostAlertJob, Stomme.Server"
        public CloudCostAlertJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Cloud Cost Alert Job";
            Frequency = "d"; // day
            Interval = 1;
            IsEnabled = true; // gated at run time by CloudUsage:Alerts:Enabled
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var options = CloudUsageAlertOptions.From(HostConfiguration.Resolve(provider));
            if (!options.Enabled)
            {
                return "Cloud Cost Alerts Are Disabled (CloudUsage:Alerts:Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Cloud Cost Alerts: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<CloudCostAlertJob>>();
            using var tenantScope = provider.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);
            List<Site> sites;
            using (var listing = scopes.CreateScope())
            {
                listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync()).Where(site => !site.IsDeleted).ToList();
            }

            var log = new StringBuilder();
            foreach (var site in sites)
            {
                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);
                    await tenantManager.SetAliasAsync(tenant!.TenantId, site.SiteId);
                    var sent = await siteScope.ServiceProvider.GetRequiredService<CloudCostAlertService>().EvaluateAsync(tenantId, site.SiteId, options);
                    if (sent > 0)
                    {
                        log.Append("Cloud Cost Alerts For Site: ").Append(site.Name).Append(" - ").Append(sent).Append(" Sent<br />");
                    }
                }
                catch (Exception ex)
                {
                    // Contained per site: one site's failure must not cost the others their alerts.
                    logger.LogError(ex, "The cloud-cost alert pass failed for site {SiteId} of tenant {TenantId}.", site.SiteId, tenant!.TenantId);
                    log.Append("Cloud Cost Alerts Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Cloud Cost Alerts: Nothing To Send<br />" : log.ToString();
        }
    }

    /// <summary>The two templates this module contributes to the per-site template reconciliation.</summary>
    public static class CloudUsageTemplateCatalogue
    {
        public static IReadOnlyList<NotificationTemplateAggregate> All(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            return new[]
            {
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, CloudCostAlertService.BudgetTemplateKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Cloud budget {{ threshold }}%: {{ provider }}",
                            "Spend for {{ month }} on {{ provider }} is {{ actualSpend }} {{ currency }}, {{ percentUsed }}% of the {{ budgetAmount }} {{ currency }} budget.")),
                    },
                    placeholders: new PlaceholderSchema(new[] { "month", "provider", "currency", "budgetAmount", "actualSpend", "percentUsed", "threshold" })),
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, CloudCostAlertService.AnomalyTemplateKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Cloud spend spike on {{ date }} ({{ currency }})",
                            "Spend on {{ date }} was {{ spend }} {{ currency }}, {{ deltaPercent }}% above the {{ lookbackDays }}-day average of {{ average }} (threshold {{ thresholdPercent }}%).")),
                    },
                    placeholders: new PlaceholderSchema(new[] { "date", "currency", "spend", "average", "deltaPercent", "thresholdPercent", "lookbackDays" })),
            };
        }
    }

    /// <summary>Contributes <see cref="CloudUsageTemplateCatalogue"/> to the per-site template reconciliation.</summary>
    public sealed class CloudUsageTemplateSource : INotificationTemplateSource
    {
        public string Name => "Cloud usage alert templates";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) => CloudUsageTemplateCatalogue.All(tenantId, siteId);
    }
}
