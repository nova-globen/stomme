#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Connectivity.Application
{
    /// <summary>
    /// The Connectivity module's CQRS surface (BL-94 increment 6, D110). Every request carries <c>[Requires]</c> and is
    /// decided by the authorization bridge; the site is the request's, never a parameter; a HostOnly connector's
    /// connection is created or edited by the Host role alone; a credential travels ONLY into the per-site secret
    /// store, and a connection records the field NAMES; a connector's message never reaches the caller - a category does.
    /// </summary>
    [Requires(ConnectionPermissions.Manage)]
    public sealed record CreateConnectionCommand(
        string Name,
        string ConnectorType,
        string Endpoint,
        IReadOnlyDictionary<string, string>? Settings = null,
        IReadOnlyDictionary<string, string>? Credentials = null,
        int TimeoutSeconds = 30) : ICommand<Guid>;

    public sealed class CreateConnectionCommandValidator : AbstractValidator<CreateConnectionCommand>
    {
        public CreateConnectionCommandValidator()
        {
            RuleFor(command => command.Name).NotEmpty().MaximumLength(ConnectionAggregate.MaxNameLength);
            RuleFor(command => command.ConnectorType).NotEmpty();
            RuleFor(command => command.Endpoint).NotEmpty().MaximumLength(ConnectionEndpoint.MaxLength);
            RuleFor(command => command.TimeoutSeconds).InclusiveBetween(ConnectionAggregate.MinTimeoutSeconds, ConnectionAggregate.MaxTimeoutSeconds);
        }
    }

    public sealed class CreateConnectionCommandHandler(IConnectionRepository connections, IUnitOfWork unitOfWork, ITenantContext tenantContext, SiteState siteState,
        ICurrentUser currentUser, IConnectorCatalog catalog, ConnectionSecrets secrets, TimeProvider clock)
        : ICommandHandler<CreateConnectionCommand, Guid>
    {
        public async Task<Guid> Handle(CreateConnectionCommand request, CancellationToken ct)
        {
            var connector = catalog.Find(request.ConnectorType) ?? throw new InvalidOperationException($"Unknown connector type '{request.ConnectorType}'.");
            ConnectionAuthoring.RequireHostIfHostOnly(connector, currentUser);
            ConnectionAuthoring.ValidateSettings(connector, request.Settings);
            ConnectionAuthoring.Preflight(connector, request.Endpoint, request.Settings);

            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A connection belongs to the request's site; none is resolved.");
            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("A connection belongs to a tenant; none is resolved in this scope.");
            if (await connections.FindByNameAsync(siteId, request.Name, ct) is not null)
            {
                throw new InvalidOperationException($"A connection named '{request.Name.Trim()}' already exists.");
            }

            // The secrets first (every field name is checked before the first write), then the row that names them.
            var id = Guid.NewGuid();
            var stored = await secrets.StoreAsync(siteId, id, connector.AuthType, request.Credentials, null, ct);
            var connection = ConnectionAggregate.Create(id, tenantId, siteId, request.Name, connector.ConnectorType, connector.EndpointKind, request.Endpoint,
                request.Settings, stored, request.TimeoutSeconds, clock.GetUtcNow().UtcDateTime);
            await connections.AddAsync(connection, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return id;
        }
    }

    /// <summary>A reconfiguration: the name, the endpoint, the settings, the timeout; a credential given replaces the stored one, a blank or absent one leaves it as it was; the verdict resets to Untested.</summary>
    [Requires(ConnectionPermissions.Manage)]
    public sealed record UpdateConnectionCommand(
        Guid ConnectionId,
        string Name,
        string Endpoint,
        IReadOnlyDictionary<string, string>? Settings = null,
        IReadOnlyDictionary<string, string>? Credentials = null,
        int TimeoutSeconds = 30) : ICommand<Unit>;

    public sealed class UpdateConnectionCommandValidator : AbstractValidator<UpdateConnectionCommand>
    {
        public UpdateConnectionCommandValidator()
        {
            RuleFor(command => command.ConnectionId).NotEmpty();
            RuleFor(command => command.Name).NotEmpty().MaximumLength(ConnectionAggregate.MaxNameLength);
            RuleFor(command => command.Endpoint).NotEmpty().MaximumLength(ConnectionEndpoint.MaxLength);
            RuleFor(command => command.TimeoutSeconds).InclusiveBetween(ConnectionAggregate.MinTimeoutSeconds, ConnectionAggregate.MaxTimeoutSeconds);
        }
    }

    public sealed class UpdateConnectionCommandHandler(IConnectionRepository connections, IUnitOfWork unitOfWork, SiteState siteState, ICurrentUser currentUser,
        IConnectorCatalog catalog, ConnectionSecrets secrets, TimeProvider clock)
        : ICommandHandler<UpdateConnectionCommand, Unit>
    {
        public async Task<Unit> Handle(UpdateConnectionCommand request, CancellationToken ct)
        {
            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A connection belongs to the request's site; none is resolved.");
            var connection = await connections.GetByIdAsync(request.ConnectionId, ct);
            if (connection is null || connection.SiteId != siteId)
            {
                throw new InvalidOperationException($"Connection '{request.ConnectionId}' was not found.");
            }

            var connector = catalog.Find(connection.ConnectorType) ?? throw new InvalidOperationException($"Connector '{connection.ConnectorType}' is not installed; the connection cannot be reconfigured.");
            ConnectionAuthoring.RequireHostIfHostOnly(connector, currentUser);
            ConnectionAuthoring.ValidateSettings(connector, request.Settings);
            ConnectionAuthoring.Preflight(connector, request.Endpoint, request.Settings);
            var clash = await connections.FindByNameAsync(siteId, request.Name, ct);
            if (clash is not null && clash.Id != connection.Id)
            {
                throw new InvalidOperationException($"A connection named '{request.Name.Trim()}' already exists.");
            }

            var stored = await secrets.StoreAsync(siteId, connection.Id, connector.AuthType, request.Credentials, connection.CredentialFields, ct);
            connection.Reconfigure(request.Name, connector.EndpointKind, request.Endpoint, request.Settings, stored, request.TimeoutSeconds, clock.GetUtcNow().UtcDateTime);
            await connections.UpdateAsync(connection, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Tests a connection and records the verdict: the category and its fixed text, never the connector's own words.</summary>
    [Requires(ConnectionPermissions.Manage)]
    public sealed record TestConnectionCommand(Guid ConnectionId) : ICommand<ConnectionTestView>;

    public sealed class TestConnectionCommandHandler(IConnectionRepository connections, IUnitOfWork unitOfWork, SiteState siteState, ConnectionTestRunner runner, TimeProvider clock)
        : ICommandHandler<TestConnectionCommand, ConnectionTestView>
    {
        public async Task<ConnectionTestView> Handle(TestConnectionCommand request, CancellationToken ct)
        {
            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A connection belongs to the request's site; none is resolved.");
            var connection = await connections.GetByIdAsync(request.ConnectionId, ct);
            if (connection is null || connection.SiteId != siteId)
            {
                throw new InvalidOperationException($"Connection '{request.ConnectionId}' was not found.");
            }

            var outcome = await runner.TestAsync(connection, ct);
            var success = ConnectorOutcomes.IsSuccess(outcome);
            var message = ConnectorOutcomes.Message(outcome);
            connection.RecordTestResult(success, message, clock.GetUtcNow().UtcDateTime);
            await connections.UpdateAsync(connection, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return new ConnectionTestView(success, outcome, message, connection.Status.ToString());
        }
    }

    /// <summary>Removes a connection and every secret it names; refused while a consumer still references it; idempotent for an unknown id.</summary>
    [Requires(ConnectionPermissions.Manage)]
    public sealed record DeleteConnectionCommand(Guid ConnectionId) : ICommand<Unit>;

    public sealed class DeleteConnectionCommandHandler(IConnectionRepository connections, IUnitOfWork unitOfWork, SiteState siteState,
        IEnumerable<IConnectionUsageProvider> usageProviders, ConnectionSecrets secrets)
        : ICommandHandler<DeleteConnectionCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteConnectionCommand request, CancellationToken ct)
        {
            var siteId = siteState.Alias?.SiteId ?? throw new InvalidOperationException("A connection belongs to the request's site; none is resolved.");
            var connection = await connections.GetByIdAsync(request.ConnectionId, ct);
            if (connection is null || connection.SiteId != siteId)
            {
                return Unit.Value; // idempotent
            }

            foreach (var provider in usageProviders)
            {
                var usage = await provider.GetUsageAsync(connection.Id, ct);
                if (usage.InUse)
                {
                    throw new InvalidOperationException(usage.Description ?? $"Connection '{connection.Name}' is in use and cannot be deleted.");
                }
            }

            var fields = connection.CredentialFields.ToList();
            connection.MarkDeleted();
            await connections.DeleteAsync(connection, ct);
            await unitOfWork.SaveChangesAsync(ct);

            // The row first, the secrets after: an orphaned private row is inert, a connection naming a missing secret is not.
            await secrets.DeleteAsync(siteId, connection.Id, fields, ct);
            return Unit.Value;
        }
    }

    [Requires(ConnectionPermissions.View)]
    public sealed record ListConnectionsQuery : IQuery<IReadOnlyList<ConnectionView>>;

    public sealed class ListConnectionsQueryHandler(IConnectionRepository connections, SiteState siteState, IConnectorCatalog catalog)
        : IQueryHandler<ListConnectionsQuery, IReadOnlyList<ConnectionView>>
    {
        public async Task<IReadOnlyList<ConnectionView>> Handle(ListConnectionsQuery request, CancellationToken ct)
        {
            if (siteState.Alias?.SiteId is not { } siteId)
            {
                return Array.Empty<ConnectionView>();
            }

            return (await connections.ListForSiteAsync(siteId, ct)).Select(connection => ToView(connection, catalog)).ToList();
        }

        public static ConnectionView ToView(ConnectionAggregate connection, IConnectorCatalog catalog) => new(
            connection.Id,
            connection.Name,
            connection.ConnectorType,
            catalog.Find(connection.ConnectorType)?.DisplayName ?? connection.ConnectorType,
            connection.Endpoint.Value,
            connection.Settings.GetSettings(),
            connection.CredentialFields.ToList(),
            connection.TimeoutSeconds,
            connection.Status.ToString(),
            connection.LastTestedUtc,
            catalog.Find(connection.ConnectorType) is not null);
    }

    [Requires(ConnectionPermissions.View)]
    public sealed record GetConnectionQuery(Guid ConnectionId) : IQuery<ConnectionView?>;

    public sealed class GetConnectionQueryHandler(IConnectionRepository connections, SiteState siteState, IConnectorCatalog catalog)
        : IQueryHandler<GetConnectionQuery, ConnectionView?>
    {
        public async Task<ConnectionView?> Handle(GetConnectionQuery request, CancellationToken ct)
        {
            var connection = await connections.GetByIdAsync(request.ConnectionId, ct);
            return connection is null || connection.SiteId != siteState.Alias?.SiteId ? null : ListConnectionsQueryHandler.ToView(connection, catalog);
        }
    }

    [Requires(ConnectionPermissions.View)]
    public sealed record ListConnectorsQuery : IQuery<IReadOnlyList<ConnectorView>>;

    public sealed class ListConnectorsQueryHandler(IConnectorCatalog catalog) : IQueryHandler<ListConnectorsQuery, IReadOnlyList<ConnectorView>>
    {
        public Task<IReadOnlyList<ConnectorView>> Handle(ListConnectorsQuery request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConnectorView>>(catalog.All.OrderBy(connector => connector.DisplayName, StringComparer.OrdinalIgnoreCase).Select(ConnectorViews.From).ToList());
    }
}
