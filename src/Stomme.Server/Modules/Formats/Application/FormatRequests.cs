#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Formats;
using Stomme.Formats.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Formats.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Formats.Application
{
    /// <summary>
    /// The Formats module's CQRS surface (BL-94 increment 2, D110). Every request carries <c>[Requires]</c> and is decided
    /// by the authorization bridge; the site is the request's, never a parameter. A system preset is read-only: it can
    /// be neither saved over nor deleted. A name is unique per site through the store's normalised column (D49).
    /// </summary>
    [Requires(FormatPermissions.Manage)]
    public sealed record SaveFormatDefinitionCommand(Guid? Id, string Name, FormatKind Kind, string? Pattern, string? Culture, string? TimeZone = null) : ICommand<Guid>;

    public sealed class SaveFormatDefinitionCommandValidator : AbstractValidator<SaveFormatDefinitionCommand>
    {
        public SaveFormatDefinitionCommandValidator()
        {
            RuleFor(command => command.Name).NotEmpty().MaximumLength(FormatDefinitionAggregate.MaxNameLength);
            RuleFor(command => command.Pattern)
                .NotEmpty()
                .When(command => command.Kind != FormatKind.Locale)
                .WithMessage("A pattern is required for date, number, and currency formats.");
            RuleFor(command => command.Pattern).MaximumLength(FormatPattern.MaxLength);
        }
    }

    public sealed class SaveFormatDefinitionCommandHandler(IFormatDefinitionRepository formats, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState, TimeProvider clock)
        : ICommandHandler<SaveFormatDefinitionCommand, Guid>
    {
        public async Task<Guid> Handle(SaveFormatDefinitionCommand request, CancellationToken ct)
        {
            if (request.Id is { } presetId && SystemFormats.Find(presetId) is not null)
            {
                throw new InvalidOperationException("System formats are read-only and cannot be modified.");
            }

            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A format definition belongs to the request's site; none is resolved.");
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A format definition belongs to a tenant; none is resolved in this scope.");
            var now = clock.GetUtcNow().UtcDateTime;

            var clash = await formats.FindByNameAsync(siteId, request.Name, ct);
            if (clash is not null && clash.Id != request.Id)
            {
                throw new InvalidOperationException($"A format named '{request.Name.Trim()}' already exists.");
            }

            // One write per path (the per-write invariant's rule): a create OR an update, never both.
            Guid id;
            if (request.Id is null)
            {
                var created = FormatDefinitionAggregate.Create(Guid.NewGuid(), tenantId, siteId, request.Name, request.Kind, request.Pattern, request.Culture, request.TimeZone, now);
                await formats.AddAsync(created, ct);
                id = created.Id;
            }
            else
            {
                var format = await formats.GetByIdAsync(request.Id.Value, ct);
                if (format is null || format.SiteId != siteId)
                {
                    throw new InvalidOperationException($"Format '{request.Id}' does not exist.");
                }

                format.Update(request.Name, request.Kind, request.Pattern, request.Culture, request.TimeZone, now);
                await formats.UpdateAsync(format, ct);
                id = format.Id;
            }

            await unitOfWork.SaveChangesAsync(ct);
            return id;
        }
    }

    [Requires(FormatPermissions.Manage)]
    public sealed record DeleteFormatDefinitionCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteFormatDefinitionCommandHandler(IFormatDefinitionRepository formats, IUnitOfWork unitOfWork, SiteState siteState, IEnumerable<IFormatUsageProvider> usageProviders)
        : ICommandHandler<DeleteFormatDefinitionCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteFormatDefinitionCommand request, CancellationToken ct)
        {
            if (SystemFormats.Find(request.Id) is not null)
            {
                throw new InvalidOperationException("System formats are read-only and cannot be deleted.");
            }

            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A format definition belongs to the request's site; none is resolved.");
            var format = await formats.GetByIdAsync(request.Id, ct);
            if (format is null || format.SiteId != siteId)
            {
                return Unit.Value; // idempotent
            }

            var references = new List<string>();
            foreach (var provider in usageProviders)
            {
                references.AddRange(await provider.FindReferencesAsync(request.Id, ct));
            }

            if (references.Count > 0)
            {
                throw new InvalidOperationException($"Format '{format.Name}' is referenced by: {string.Join(", ", references)}. Remove those references first.");
            }

            format.MarkDeleted();
            await formats.DeleteAsync(format, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    [Requires(FormatPermissions.View)]
    public sealed record ListFormatsQuery(FormatKind? Kind = null, string? Culture = null, FormatScope? Scope = null) : IQuery<IReadOnlyList<FormatDescriptor>>;

    public sealed class ListFormatsQueryHandler(IFormatService formatService) : IQueryHandler<ListFormatsQuery, IReadOnlyList<FormatDescriptor>>
    {
        public Task<IReadOnlyList<FormatDescriptor>> Handle(ListFormatsQuery request, CancellationToken ct) =>
            formatService.ListAsync(request.Kind, request.Culture, request.Scope, ct);
    }

    [Requires(FormatPermissions.View)]
    public sealed record GetFormatQuery(Guid Id) : IQuery<FormatDescriptor?>;

    public sealed class GetFormatQueryHandler(IFormatService formatService) : IQueryHandler<GetFormatQuery, FormatDescriptor?>
    {
        public Task<FormatDescriptor?> Handle(GetFormatQuery request, CancellationToken ct) => formatService.GetAsync(request.Id, ct);
    }

    /// <summary>
    /// The Shared port's server implementation: the presets plus the REQUEST's site's definitions. The list is kind, then
    /// scope, then name; a culture filter compares names case-insensitively.
    /// </summary>
    public sealed class SiteFormatService(IFormatDefinitionRepository formats, SiteState siteState) : IFormatService
    {
        public async Task<FormatDescriptor?> GetAsync(Guid id, CancellationToken ct = default)
        {
            if (SystemFormats.Find(id) is { } system)
            {
                return system;
            }

            var definition = await formats.GetByIdAsync(id, ct);
            return definition is null || definition.SiteId != siteState.Alias?.SiteId ? null : definition.ToDescriptor();
        }

        public async Task<IReadOnlyList<FormatDescriptor>> ListAsync(FormatKind? kind = null, string? culture = null, FormatScope? scope = null, CancellationToken ct = default)
        {
            IEnumerable<FormatDescriptor> all = SystemFormats.All;
            if (scope != FormatScope.System)
            {
                var siteId = siteState.Alias?.SiteId;
                var mine = siteId is { } id ? (await formats.ListForSiteAsync(id, ct)).Select(definition => definition.ToDescriptor()).ToList() : new List<FormatDescriptor>();
                all = scope == FormatScope.Tenant ? mine : all.Concat(mine);
            }

            if (kind is { } wanted)
            {
                all = all.Where(format => format.Kind == wanted);
            }

            if (!string.IsNullOrWhiteSpace(culture))
            {
                all = all.Where(format => string.Equals(format.Culture, culture.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return all.OrderBy(format => format.Kind).ThenBy(format => format.Scope).ThenBy(format => format.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
