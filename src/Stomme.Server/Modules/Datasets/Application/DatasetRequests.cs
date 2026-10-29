#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Datasets.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>
    /// The producer-facing registrar: a registration for an origin the site already has
    /// REFRESHES that dataset (the schema, the summary, the sample; the classification escalated by the classifier, never
    /// lowered; the name moved when the registration names it differently and nothing else holds the new name); a
    /// registration for a NEW origin under a name another dataset holds is REJECTED, not overwritten; an over-cap sample
    /// is refused before inference; the classifier escalates the producer's classification (a PII sample registered as
    /// Public comes back Restricted); the site is the request's and the tenant the scope's - no default-tenant fallback,
    /// a missing one throws.
    /// </summary>
    public sealed class DatasetRegistrar(IDatasetRepository datasets, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, IDataClassifier classifier, TimeProvider clock) : IDatasetRegistrar
    {
        public async Task<Guid> RegisterOrRefreshAsync(DatasetRegistration registration, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);
            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A dataset belongs to the request's site; none is resolved.");
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A dataset belongs to a tenant; none is resolved in this scope.");
            var now = clock.GetUtcNow().UtcDateTime;
            var name = registration.Name.Trim();
            var reference = string.IsNullOrWhiteSpace(registration.OriginReference) ? null : registration.OriginReference.Trim();

            var inference = SchemaInference.Infer(registration.SampleJson); // an over-cap sample is refused HERE, before anything is read
            var summary = registration.RowCountEstimate is { } rows ? DatasetSummary.From(inference.Summary.Columns, rows) : inference.Summary;
            var derived = classifier.Classify(inference.Schema.ColumnNames.ToList(), registration.SampleJson);

            var existing = await datasets.FindByOriginAsync(siteId, registration.OriginKind, reference ?? name, ct);
            var byName = await datasets.FindByNameAsync(siteId, name, ct);
            if (existing is not null)
            {
                if (byName is not null && byName.Id != existing.Id)
                {
                    throw new InvalidOperationException($"A dataset named '{name}' already exists for another origin.");
                }

                existing.Refresh(inference.Schema, summary, inference.SampleRowsJson, Max(registration.Classification, derived), now);
                if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
                {
                    existing.Rename(name);
                }

                await datasets.UpdateAsync(existing, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return existing.Id;
            }

            if (byName is not null)
            {
                throw new InvalidOperationException($"A dataset named '{name}' already exists for another origin; a registration never overwrites it.");
            }

            var dataset = DatasetAggregate.Register(tenantId, siteId, name, registration.OriginKind, reference, Max(registration.Classification, derived), inference.Schema, summary, inference.SampleRowsJson, now);
            await datasets.AddAsync(dataset, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return dataset.Id;
        }

        private static DataClassification Max(DataClassification asserted, DataClassification derived) => derived > asserted ? derived : asserted;
    }

    /// <summary>Registers a dataset from sample rows (or refreshes the one its origin already has) - the registrar's rules, over the mediator.</summary>
    [Requires(DatasetPermissions.Manage)]
    public sealed record RegisterDatasetCommand(string Name, OriginKind OriginKind, string? OriginReference, string SampleJson, DataClassification Classification = DataClassification.Internal) : ICommand<Guid>;

    public sealed class RegisterDatasetCommandValidator : AbstractValidator<RegisterDatasetCommand>
    {
        public RegisterDatasetCommandValidator()
        {
            RuleFor(command => command.Name).NotEmpty().MaximumLength(DatasetLimits.MaxNameLength);
            RuleFor(command => command.SampleJson).NotEmpty().MaximumLength(DatasetLimits.MaxSampleBytes);
        }
    }

    public sealed class RegisterDatasetCommandHandler(IDatasetRegistrar registrar) : ICommandHandler<RegisterDatasetCommand, Guid>
    {
        public Task<Guid> Handle(RegisterDatasetCommand request, CancellationToken ct) =>
            registrar.RegisterOrRefreshAsync(new DatasetRegistration(request.Name, request.OriginKind, request.OriginReference, request.SampleJson, request.Classification), ct);
    }

    /// <summary>Recomputes a dataset's schema, summary and sample from fresh rows; the classifier may escalate the level, never lower it.</summary>
    [Requires(DatasetPermissions.Manage)]
    public sealed record RefreshDatasetCommand(Guid Id, string SampleJson) : ICommand<Unit>;

    public sealed class RefreshDatasetCommandValidator : AbstractValidator<RefreshDatasetCommand>
    {
        public RefreshDatasetCommandValidator()
        {
            RuleFor(command => command.Id).NotEmpty();
            RuleFor(command => command.SampleJson).NotEmpty().MaximumLength(DatasetLimits.MaxSampleBytes);
        }
    }

    public sealed class RefreshDatasetCommandHandler(IDatasetRepository datasets, IUnitOfWork unitOfWork, SiteState siteState, IDataClassifier classifier, TimeProvider clock)
        : ICommandHandler<RefreshDatasetCommand, Unit>
    {
        public async Task<Unit> Handle(RefreshDatasetCommand request, CancellationToken ct)
        {
            var dataset = await datasets.GetByIdAsync(request.Id, ct);
            if (dataset is null || dataset.SiteId != siteState.Alias?.SiteId)
            {
                throw new InvalidOperationException($"Dataset '{request.Id}' does not exist.");
            }

            var inference = SchemaInference.Infer(request.SampleJson);
            dataset.Refresh(inference.Schema, inference.Summary, inference.SampleRowsJson, classifier.Classify(inference.Schema.ColumnNames.ToList(), request.SampleJson), clock.GetUtcNow().UtcDateTime);
            await datasets.UpdateAsync(dataset, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Deletes a dataset; refused while a consumer still references it; idempotent for an unknown id.</summary>
    [Requires(DatasetPermissions.Manage)]
    public sealed record DeleteDatasetCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteDatasetCommandHandler(IDatasetRepository datasets, IUnitOfWork unitOfWork, SiteState siteState, IEnumerable<IDatasetUsageProvider> usageProviders)
        : ICommandHandler<DeleteDatasetCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteDatasetCommand request, CancellationToken ct)
        {
            var dataset = await datasets.GetByIdAsync(request.Id, ct);
            if (dataset is null || dataset.SiteId != siteState.Alias?.SiteId)
            {
                return Unit.Value; // idempotent
            }

            var references = new List<string>();
            foreach (var provider in usageProviders)
            {
                references.AddRange(await provider.FindReferencesAsync(dataset.Id, ct));
            }

            if (references.Count > 0)
            {
                throw new InvalidOperationException($"Dataset '{dataset.Name}' is referenced by: {string.Join(", ", references)}. Remove those references first.");
            }

            dataset.MarkDeleted();
            await datasets.DeleteAsync(dataset, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(DatasetPermissions.View)]
    public sealed record ListDatasetsQuery : IQuery<IReadOnlyList<DatasetDescriptor>>;

    public sealed class ListDatasetsQueryHandler(IDatasetCatalog catalog) : IQueryHandler<ListDatasetsQuery, IReadOnlyList<DatasetDescriptor>>
    {
        public Task<IReadOnlyList<DatasetDescriptor>> Handle(ListDatasetsQuery request, CancellationToken ct) => catalog.ListDescriptorsAsync(ct);
    }

    [Requires(DatasetPermissions.View)]
    public sealed record GetDatasetQuery(Guid Id) : IQuery<DatasetDescriptor?>;

    public sealed class GetDatasetQueryHandler(IDatasetCatalog catalog) : IQueryHandler<GetDatasetQuery, DatasetDescriptor?>
    {
        public Task<DatasetDescriptor?> Handle(GetDatasetQuery request, CancellationToken ct) => catalog.GetDescriptorAsync(request.Id, ct);
    }

    /// <summary>The bounded raw-row preview - the higher privilege; refused for a sensitive dataset whoever asks.</summary>
    [Requires(DatasetPermissions.Preview)]
    public sealed record PreviewDatasetRowsQuery(Guid Id) : IQuery<DatasetPreviewRows>;

    public sealed class PreviewDatasetRowsQueryHandler(IDatasetCatalog catalog) : IQueryHandler<PreviewDatasetRowsQuery, DatasetPreviewRows>
    {
        public Task<DatasetPreviewRows> Handle(PreviewDatasetRowsQuery request, CancellationToken ct) => catalog.GetPreviewRowsAsync(request.Id, ct);
    }
}
