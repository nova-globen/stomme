#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// The predecessor platform's <c>CloudCostRecordTests</c> by name on the site-scoped aggregate. CHANGED: the row names
    /// its site (a site of zero is refused, the port's own case) and its date is the stored UTC midnight.
    /// </summary>
    public sealed class CloudCostRecordTests
    {
        private const int Site = 3;

        private static CloudCostEntry CreateEntry(
            CloudProvider provider = CloudProvider.Azure,
            CloudServiceCategory category = CloudServiceCategory.Compute,
            string service = "Virtual Machines",
            string resource = "vm-orders-01",
            decimal cost = 12.34m,
            string currency = "USD",
            string? department = null,
            string? project = null,
            string? environment = null,
            DateOnly? date = null) =>
            new(date ?? new DateOnly(2026, 7, 1), provider, category, service, resource, cost, currency, department, project, environment);

        [Fact]
        public void Record_StoresUnifiedCostSchemaFields()
        {
            var id = Guid.NewGuid();
            var record = CloudCostRecordAggregate.Record(id, TenantId.Default, Site, CreateEntry(department: " Finance ", project: " predecessor ", environment: " prod "));

            Assert.Equal(id, record.Id);
            Assert.Equal(TenantId.Default, record.TenantId);
            Assert.Equal(Site, record.SiteId);
            Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), record.DateUtc);
            Assert.Equal(DateTimeKind.Utc, record.DateUtc.Kind);
            Assert.Equal(CloudProvider.Azure, record.Provider);
            Assert.Equal(CloudServiceCategory.Compute, record.ServiceCategory);
            Assert.Equal("Virtual Machines", record.Service);
            Assert.Equal("vm-orders-01", record.Resource);
            Assert.Equal(12.34m, record.Cost);
            Assert.Equal("USD", record.Currency);
            Assert.Equal("Finance", record.Department);
            Assert.Equal("predecessor", record.Project);
            Assert.Equal("prod", record.Environment);
        }

        [Fact]
        public void Record_UppercasesAndTrimsCurrency()
        {
            Assert.Equal("USD", CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(currency: " usd ")).Currency);
        }

        [Fact]
        public void Record_TrimsServiceAndResource_AndStoresBlankOptionalTagsAsNull()
        {
            var record = CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(
                service: "  AmazonS3  ", resource: "  arn:aws:s3:::predecessor-exports  ", department: "   ", project: null, environment: ""));

            Assert.Equal("AmazonS3", record.Service);
            Assert.Equal("arn:aws:s3:::predecessor-exports", record.Resource);
            Assert.Null(record.Department);
            Assert.Null(record.Project);
            Assert.Null(record.Environment);
        }

        [Fact]
        public void Record_AcceptsNegativeCost_ForProviderCreditsAndRefunds()
        {
            Assert.Equal(-25.50m, CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(cost: -25.50m)).Cost);
        }

        [Theory]
        [InlineData((CloudProvider)0)]
        [InlineData((CloudProvider)999)]
        public void Record_RejectsUndefinedProvider(CloudProvider provider)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(provider: provider)));
        }

        [Fact]
        public void Record_RejectsUndefinedServiceCategory()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(category: (CloudServiceCategory)999)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("US")]
        [InlineData("USDX")]
        [InlineData("U5D")]
        [InlineData("U$D")]
        public void Record_RejectsMissingOrMalformedCurrency(string currency)
        {
            Assert.ThrowsAny<ArgumentException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(currency: currency)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Record_RejectsMissingService(string service)
        {
            Assert.ThrowsAny<ArgumentException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(service: service)));
        }

        [Fact]
        public void Record_RejectsDefaultDate()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, Site, CreateEntry(date: default(DateOnly))));
        }

        [Fact]
        public void Record_RejectsMissingTenant()
        {
            Assert.Throws<ArgumentNullException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), null!, Site, CreateEntry()));
        }

        [Fact]
        public void Record_RejectsMissingSite()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, 0, CreateEntry()));
        }

        [Fact]
        public void Record_RaisesCloudCostRecordedEvent_WithNormalizedPayload()
        {
            var id = Guid.NewGuid();
            var record = CloudCostRecordAggregate.Record(id, TenantId.Default, Site, CreateEntry(
                provider: CloudProvider.Gcp, category: CloudServiceCategory.Analytics, service: "BigQuery", cost: 42.10m, currency: "eur"));

            var recorded = Assert.IsType<CloudCostRecordedEvent>(Assert.Single(record.DomainEvents));
            Assert.Equal(id, recorded.RecordId);
            Assert.Equal(TenantId.Default, recorded.TenantId);
            Assert.Equal(Site, recorded.SiteId);
            Assert.Equal(CloudProvider.Gcp, recorded.Provider);
            Assert.Equal(CloudServiceCategory.Analytics, recorded.ServiceCategory);
            Assert.Equal(42.10m, recorded.Cost);
            Assert.Equal("EUR", recorded.Currency);
            Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), recorded.DateUtc);
        }
    }

    /// <summary>
    /// The predecessor platform's <c>CloudBudgetTests</c>, the aggregate half by name (the three command-handler cases land
    /// with the command in increment 4), plus the marker's once-only identity.
    /// </summary>
    public sealed class CloudBudgetTests
    {
        private const int Site = 3;

        [Theory]
        [InlineData(0)]
        [InlineData(-50)]
        public void Set_NonPositiveAmount_IsRejected(decimal amount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, amount, "USD"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("US")]
        [InlineData("DOLLARS")]
        [InlineData("U5D")]
        public void Set_InvalidCurrency_IsRejected(string currency)
        {
            Assert.ThrowsAny<ArgumentException>(() => CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, 100m, currency));
        }

        [Fact]
        public void Set_UndefinedProvider_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, 100m, "USD", (CloudProvider)42));
        }

        [Fact]
        public void Set_NormalizesCurrencyToUppercase_AndRaisesCloudBudgetSetEvent()
        {
            var budget = CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, 250m, "usd", CloudProvider.Azure);

            Assert.Equal("USD", budget.Currency);
            Assert.Equal(250m, budget.MonthlyAmount);
            Assert.Equal(CloudProvider.Azure, budget.Provider);
            Assert.Equal(Site, budget.SiteId);
            var raised = Assert.IsType<CloudBudgetSetEvent>(Assert.Single(budget.DomainEvents));
            Assert.Equal((budget.Id, TenantId.Default, Site, (CloudProvider?)CloudProvider.Azure, 250m, "USD"), (raised.BudgetId, raised.TenantId, raised.SiteId, raised.Provider, raised.MonthlyAmount, raised.Currency));
        }

        [Fact]
        public void Amend_ReplacesAmountAndCurrency_AndRaisesTheEventAgain()
        {
            var budget = CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, 100m, "USD");
            budget.ClearDomainEvents();

            budget.Amend(300m, "eur");

            Assert.Equal(300m, budget.MonthlyAmount);
            Assert.Equal("EUR", budget.Currency);
            Assert.Equal(300m, Assert.IsType<CloudBudgetSetEvent>(Assert.Single(budget.DomainEvents)).MonthlyAmount);
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Amend(0m, "EUR"));
        }

        [Fact]
        public void An_alert_markers_id_is_the_tenant_the_site_and_the_key_so_a_second_raise_is_the_same_row()
        {
            var first = CloudCostAlertMarkerAggregate.Raise(TenantId.Default, Site, " budget:x:80:2026-09 ", DateTime.UtcNow);
            var again = CloudCostAlertMarkerAggregate.Raise(TenantId.Default, Site, "budget:x:80:2026-09", DateTime.UtcNow);

            Assert.Equal(first.Id, again.Id);
            Assert.Equal("budget:x:80:2026-09", first.Key);
            Assert.NotEqual(first.Id, CloudCostAlertMarkerAggregate.BuildId(TenantId.Default, Site + 1, "budget:x:80:2026-09"));
            Assert.NotEqual(first.Id, CloudCostAlertMarkerAggregate.BuildId(TenantId.From(Guid.NewGuid()), Site, "budget:x:80:2026-09"));
        }
    }

    /// <summary>BL-97 increment 2 - the predecessor platform's <c>CloudCostNormalizerTests</c> by name over the lifted normaliser.</summary>
    public sealed class CloudCostNormalizerTests
    {
        private readonly CloudCostNormalizer _normalizer = new();

        private static RawCloudCost CreateRaw(
            CloudProvider provider = CloudProvider.Azure,
            string service = "Virtual Machines",
            string resource = "vm-orders-01",
            decimal cost = 10m,
            string currency = "USD",
            IReadOnlyDictionary<string, string>? tags = null) =>
            new(new DateOnly(2026, 7, 1), provider, service, resource, cost, currency, tags);

        [Theory]
        [InlineData(CloudProvider.Azure, "Virtual Machines", CloudServiceCategory.Compute)]
        [InlineData(CloudProvider.Azure, "Storage Accounts", CloudServiceCategory.Storage)]
        [InlineData(CloudProvider.Azure, "Azure Cosmos DB", CloudServiceCategory.Database)]
        [InlineData(CloudProvider.Azure, "Azure OpenAI Service", CloudServiceCategory.Ai)]
        [InlineData(CloudProvider.Aws, "AmazonS3", CloudServiceCategory.Storage)]
        [InlineData(CloudProvider.Aws, "Amazon Simple Storage Service", CloudServiceCategory.Storage)]
        [InlineData(CloudProvider.Aws, "Amazon Elastic Compute Cloud", CloudServiceCategory.Compute)]
        [InlineData(CloudProvider.Aws, "AmazonCloudWatch", CloudServiceCategory.Monitoring)]
        [InlineData(CloudProvider.Gcp, "BigQuery", CloudServiceCategory.Analytics)]
        [InlineData(CloudProvider.Gcp, "Compute Engine", CloudServiceCategory.Compute)]
        [InlineData(CloudProvider.Gcp, "Cloud Storage", CloudServiceCategory.Storage)]
        [InlineData(CloudProvider.Datadog, "Infrastructure Hosts", CloudServiceCategory.Monitoring)]
        [InlineData(CloudProvider.Snowflake, "Warehouse Credits", CloudServiceCategory.Analytics)]
        [InlineData(CloudProvider.Microsoft365, "Office 365 E5", CloudServiceCategory.Licensing)]
        [InlineData(CloudProvider.Azure, "Some Future Offering", CloudServiceCategory.Other)]
        [InlineData(CloudProvider.GenericRest, "Anything At All", CloudServiceCategory.Other)]
        public void MapServiceCategory_MapsRawServiceNamesPerProvider(CloudProvider provider, string service, CloudServiceCategory expected)
        {
            Assert.Equal(expected, _normalizer.MapServiceCategory(provider, service));
        }

        [Fact]
        public void MapServiceCategory_IsCaseInsensitive()
        {
            Assert.Equal(CloudServiceCategory.Compute, _normalizer.MapServiceCategory(CloudProvider.Aws, "AWS LAMBDA"));
        }

        [Fact]
        public void Normalize_ProducesConformantEntry_WithMappedCategoryAndTrimmedFields()
        {
            var entry = _normalizer.Normalize(CreateRaw(provider: CloudProvider.Gcp, service: "  BigQuery  ", resource: "  projects/stomme/datasets/costs  "));

            Assert.Equal(new DateOnly(2026, 7, 1), entry.Date);
            Assert.Equal(CloudProvider.Gcp, entry.Provider);
            Assert.Equal(CloudServiceCategory.Analytics, entry.ServiceCategory);
            Assert.Equal("BigQuery", entry.Service);
            Assert.Equal("projects/stomme/datasets/costs", entry.Resource);
            Assert.Equal(10m, entry.Cost);
            Assert.Equal("USD", entry.Currency);
        }

        [Fact]
        public void Normalize_TrimsAndUppercasesCurrency()
        {
            Assert.Equal("EUR", _normalizer.Normalize(CreateRaw(currency: " eur ")).Currency);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("US")]
        [InlineData("DOLLARS")]
        [InlineData("U5D")]
        public void Normalize_RejectsMalformedCurrency(string currency)
        {
            Assert.ThrowsAny<ArgumentException>(() => _normalizer.Normalize(CreateRaw(currency: currency)));
        }

        [Fact]
        public void Normalize_ExtractsTags_UsingAliasesCaseInsensitively()
        {
            var entry = _normalizer.Normalize(CreateRaw(tags: new Dictionary<string, string> { ["COSTCENTER"] = "cc-42", ["App"] = " orders ", ["Env"] = "prod" }));

            Assert.Equal("cc-42", entry.Department);
            Assert.Equal("orders", entry.Project);
            Assert.Equal("prod", entry.Environment);
        }

        [Fact]
        public void Normalize_PrefersCanonicalTagKey_OverLaterAliases()
        {
            var entry = _normalizer.Normalize(CreateRaw(tags: new Dictionary<string, string> { ["cost-center"] = "cc-legacy", ["Department"] = "Finance" }));

            Assert.Equal("Finance", entry.Department);
        }

        [Fact]
        public void Normalize_AbsentOrBlankTags_YieldNulls()
        {
            var withoutTags = _normalizer.Normalize(CreateRaw(tags: null));
            var withBlankTags = _normalizer.Normalize(CreateRaw(tags: new Dictionary<string, string> { ["department"] = "   ", ["owner"] = "someone-else" }));

            Assert.Null(withoutTags.Department);
            Assert.Null(withoutTags.Project);
            Assert.Null(withoutTags.Environment);
            Assert.Null(withBlankTags.Department);
            Assert.Null(withBlankTags.Project);
            Assert.Null(withBlankTags.Environment);
        }

        [Fact]
        public void Normalize_KeepsNegativeCost()
        {
            Assert.Equal(-3.14m, _normalizer.Normalize(CreateRaw(cost: -3.14m)).Cost);
        }
    }
}
