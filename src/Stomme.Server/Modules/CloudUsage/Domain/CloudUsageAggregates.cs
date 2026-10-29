#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Stomme.CloudUsage;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.CloudUsage.Domain
{
    public sealed record CloudCostRecordedEvent(
        Guid RecordId,
        TenantId TenantId,
        int SiteId,
        CloudProvider Provider,
        CloudServiceCategory ServiceCategory,
        decimal Cost,
        string Currency,
        DateTime DateUtc) : DomainEvent;

    public sealed record CloudBudgetSetEvent(
        Guid BudgetId,
        TenantId TenantId,
        int SiteId,
        CloudProvider? Provider,
        decimal MonthlyAmount,
        string Currency) : DomainEvent;

    /// <summary>
    /// One normalised cost row: the Unified Cost Schema's
    /// fields, the provider and category a defined value, the currency a three-letter code upper-cased, the optional tags
    /// trimmed with a blank one stored as none, a negative cost accepted (credits and refunds).
    /// </summary>
    /// <remarks>
    /// The row belongs to a SITE of its tenant (the connection it was fetched through is a
    /// site's), and the date is <see cref="DateUtc"/> - a UTC-midnight <see cref="DateTime"/> - because no module store holds
    /// a <see cref="DateOnly"/>; the Shared schema keeps its <see cref="DateOnly"/>, and a window is a range on this value.
    /// </remarks>
    public sealed class CloudCostRecordAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private CloudCostRecordAggregate()
        {
            TenantId = null!;
            Service = string.Empty;
            Resource = string.Empty;
            Currency = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public DateTime DateUtc { get; private set; }

        public CloudProvider Provider { get; private set; }

        public CloudServiceCategory ServiceCategory { get; private set; }

        public string Service { get; private set; }

        public string Resource { get; private set; }

        public decimal Cost { get; private set; }

        public string Currency { get; private set; }

        public string? Department { get; private set; }

        public string? Project { get; private set; }

        public string? Environment { get; private set; }

        /// <summary>The calendar day as the UTC midnight the stores hold.</summary>
        public static DateTime ToStoredDate(DateOnly date) => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        public static CloudCostRecordAggregate Record(Guid id, TenantId tenantId, int siteId, CloudCostEntry entry)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(entry);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A cost row belongs to a site.");
            }

            if (!Enum.IsDefined(entry.Provider))
            {
                throw new ArgumentOutOfRangeException(nameof(entry), $"'{entry.Provider}' is not a known cloud provider.");
            }

            if (!Enum.IsDefined(entry.ServiceCategory))
            {
                throw new ArgumentOutOfRangeException(nameof(entry), $"'{entry.ServiceCategory}' is not a known service category.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Service);
            ArgumentNullException.ThrowIfNull(entry.Resource);
            if (entry.Date == default)
            {
                throw new ArgumentOutOfRangeException(nameof(entry), "Cost date is required.");
            }

            var record = new CloudCostRecordAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                DateUtc = ToStoredDate(entry.Date),
                Provider = entry.Provider,
                ServiceCategory = entry.ServiceCategory,
                Service = entry.Service.Trim(),
                Resource = entry.Resource.Trim(),
                Cost = entry.Cost,
                Currency = CloudCurrency.Normalize(entry.Currency, nameof(entry)),
                Department = TrimToNull(entry.Department),
                Project = TrimToNull(entry.Project),
                Environment = TrimToNull(entry.Environment),
            };
            record.RaiseDomainEvent(new CloudCostRecordedEvent(record.Id, record.TenantId, record.SiteId, record.Provider, record.ServiceCategory, record.Cost, record.Currency, record.DateUtc));
            return record;
        }

        private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// A monthly budget: a positive amount in one ISO 4217 currency, for all providers or one;
    /// one budget per scope, amended rather than duplicated (the command's upsert). Per site, as the rows it
    /// measures are.
    /// </summary>
    public sealed class CloudBudgetAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private CloudBudgetAggregate()
        {
            TenantId = null!;
            Currency = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public CloudProvider? Provider { get; private set; }

        public decimal MonthlyAmount { get; private set; }

        public string Currency { get; private set; }

        public static CloudBudgetAggregate Set(Guid id, TenantId tenantId, int siteId, decimal monthlyAmount, string currency, CloudProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A budget belongs to a site.");
            }

            if (provider is { } scoped && !Enum.IsDefined(scoped))
            {
                throw new ArgumentOutOfRangeException(nameof(provider), $"'{provider}' is not a known cloud provider.");
            }

            var budget = new CloudBudgetAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Provider = provider,
                MonthlyAmount = ValidateAmount(monthlyAmount),
                Currency = CloudCurrency.Normalize(currency, nameof(currency)),
            };
            budget.RaiseDomainEvent(new CloudBudgetSetEvent(budget.Id, budget.TenantId, budget.SiteId, budget.Provider, budget.MonthlyAmount, budget.Currency));
            return budget;
        }

        public void Amend(decimal monthlyAmount, string currency)
        {
            MonthlyAmount = ValidateAmount(monthlyAmount);
            Currency = CloudCurrency.Normalize(currency, nameof(currency));
            RaiseDomainEvent(new CloudBudgetSetEvent(Id, TenantId, SiteId, Provider, MonthlyAmount, Currency));
        }

        private static decimal ValidateAmount(decimal monthlyAmount) =>
            monthlyAmount > 0 ? monthlyAmount : throw new ArgumentOutOfRangeException(nameof(monthlyAmount), "A cloud budget must be a positive monthly amount.");
    }

    /// <summary>
    /// The once-only record of an alert already raised: its id is a hash of the
    /// tenant, the site and the alert key, so a second pass for the same threshold, month or day finds it and sends nothing.
    /// </summary>
    public sealed class CloudCostAlertMarkerAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private CloudCostAlertMarkerAggregate()
        {
            TenantId = null!;
            Key = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Key { get; private set; }

        public DateTime RaisedUtc { get; private set; }

        public static Guid BuildId(TenantId tenantId, int siteId, string key)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"cloud-cost-alert|{tenantId.Value}|{siteId}|{key.Trim()}"));
            return new Guid(hash[..16]);
        }

        public static CloudCostAlertMarkerAggregate Raise(TenantId tenantId, int siteId, string key, DateTime raisedUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An alert belongs to a site.");
            }

            return new CloudCostAlertMarkerAggregate
            {
                Id = BuildId(tenantId, siteId, key),
                TenantId = tenantId,
                SiteId = siteId,
                Key = key.Trim(),
                RaisedUtc = DateTime.SpecifyKind(raisedUtc, DateTimeKind.Utc),
            };
        }
    }

    /// <summary>The ISO 4217 rule every CloudUsage amount obeys: three ASCII letters, upper-cased.</summary>
    public static class CloudCurrency
    {
        public static string Normalize(string? currency, string parameterName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(currency, parameterName);
            var trimmed = currency.Trim();
            if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetter))
            {
                throw new ArgumentException($"Currency '{currency}' is not a three-letter ISO 4217 code.", parameterName);
            }

            return trimmed.ToUpperInvariant();
        }
    }
}
