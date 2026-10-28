#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Notifications.Application
{
    // Lifted from the predecessor platform the predecessor platform.Notifications.Application/NotificationChannelCommands.cs (BL-78 step 5). Every request is
    // USER-FACING and carries [Requires(ManageChannels)]; the dispatch job never sends one (F3). Reshaped: the site is the
    // resolved alias's and a channel of another site is "does not exist"; the credential goes to ISiteSecretStore (D101)
    // under the channel's DERIVED reference key - a caller can no longer name a reference key (the predecessor platform's VaultSecretRef
    // input would let a channel present another feature's secret to a webhook endpoint); a NAMED provider that is not
    // registered is REJECTED (the predecessor platform skipped validation and the resolver fell back to the first candidate); the secret is
    // written only AFTER the settings validated.

    /// <summary>Creates (null or unknown <see cref="Id"/>) or reconfigures a channel of the current site. The kind is immutable.</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record ConfigureChannelCommand(
        Guid? Id,
        NotificationChannelKind Kind,
        string Name,
        string? Endpoint,
        string? Secret,
        IReadOnlyList<NotificationChannelOption>? Options,
        NotificationRateLimitInput? RateLimit) : ICommand<Guid>;

    public sealed class ConfigureChannelCommandValidator : AbstractValidator<ConfigureChannelCommand>
    {
        public ConfigureChannelCommandValidator()
        {
            RuleFor(command => command.Name).NotEmpty().MaximumLength(120);
            When(command => command.RateLimit is not null, () =>
            {
                RuleFor(command => command.RateLimit!.MaxPerWindow).GreaterThan(0);
                RuleFor(command => command.RateLimit!.WindowSeconds).GreaterThan(0);
            });
        }
    }

    public sealed class ConfigureChannelCommandHandler(
        INotificationChannelRepository channels,
        NotificationChannelResolver resolver,
        ISiteSecretStore secrets,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState)
        : ICommandHandler<ConfigureChannelCommand, Guid>
    {
        public async Task<Guid> Handle(ConfigureChannelCommand request, CancellationToken ct)
        {
            var siteId = NotificationWire.RequireSiteId(siteState);
            var kind = NotificationWire.ToDomain(request.Kind);

            NotificationChannelAggregate? existing = null;
            if (request.Id is { } id)
            {
                existing = await channels.GetByIdAsync(id, ct);
                if (existing is not null && existing.SiteId != siteId)
                {
                    throw new InvalidOperationException($"Channel '{id}' does not exist.");
                }
            }

            if (existing is not null && existing.Kind != kind)
            {
                throw new InvalidOperationException("A channel's kind cannot be changed.");
            }

            var channelId = existing?.Id ?? request.Id ?? Guid.NewGuid();
            var options = (request.Options ?? Array.Empty<NotificationChannelOption>())
                .Select(option => new TransportOption(option.Key, option.Value))
                .ToList();

            var hasNewSecret = !string.IsNullOrWhiteSpace(request.Secret);
            var secretReference = hasNewSecret
                ? WebhookNotificationChannel.CredentialReferenceKey(channelId)
                : existing?.Transport.SecretReference;

            // Structural validation (endpoint shape, no inline secret, a well-formed reference key) is the value object's.
            var transport = new TransportSettings(kind, request.Endpoint, secretReference, options);

            var providerKey = options
                .FirstOrDefault(option => string.Equals(option.Key, NotificationProviderKeys.SelectorOption, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            var provider = resolver.For(kind, providerKey);
            if (provider is null && !string.IsNullOrWhiteSpace(providerKey))
            {
                throw new InvalidOperationException($"No transport named '{providerKey.Trim()}' is registered for {kind} channels.");
            }

            if (provider is not null)
            {
                var errors = await provider.ValidateSettingsAsync(siteId, transport, ct);
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(" ", errors));
                }
            }

            var rateLimit = request.RateLimit is { } limit
                ? new RateLimit(limit.MaxPerWindow, TimeSpan.FromSeconds(limit.WindowSeconds))
                : null;

            if (hasNewSecret)
            {
                await secrets.SetAsync(siteId, secretReference!, request.Secret!, ct);
            }

            if (existing is not null)
            {
                existing.Reconfigure(request.Name, transport, rateLimit);
                await channels.UpdateAsync(existing, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return existing.Id;
            }

            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved in this scope.");
            var channel = NotificationChannelAggregate.Create(channelId, tenantId, siteId, kind, request.Name, transport, rateLimit);
            await channels.AddAsync(channel, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return channel.Id;
        }
    }

    /// <summary>Replaces (a non-empty <see cref="Secret"/>) or removes (null/empty) a channel's credential. WRITE-ONLY: nothing returns it.</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record SetChannelCredentialCommand(Guid Id, string? Secret) : ICommand<Unit>;

    public sealed class SetChannelCredentialCommandHandler(
        INotificationChannelRepository channels,
        ISiteSecretStore secrets,
        IUnitOfWork unitOfWork,
        SiteState siteState)
        : ICommandHandler<SetChannelCredentialCommand, Unit>
    {
        public async Task<Unit> Handle(SetChannelCredentialCommand request, CancellationToken ct)
        {
            var siteId = NotificationWire.RequireSiteId(siteState);
            var channel = await ChannelLookup.RequireAsync(channels, siteId, request.Id, ct);
            var derived = WebhookNotificationChannel.CredentialReferenceKey(channel.Id);

            string? reference;
            if (string.IsNullOrWhiteSpace(request.Secret))
            {
                if (channel.Transport.SecretReference is { } current)
                {
                    await secrets.DeleteAsync(siteId, current, ct);
                }

                reference = null;
            }
            else
            {
                await secrets.SetAsync(siteId, derived, request.Secret, ct);
                reference = derived;
            }

            if (!string.Equals(channel.Transport.SecretReference, reference, StringComparison.Ordinal))
            {
                var transport = new TransportSettings(channel.Kind, channel.Transport.Endpoint, reference, channel.Transport.Options);
                channel.Reconfigure(channel.Name, transport, channel.RateLimit);
                await channels.UpdateAsync(channel, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }

            return Unit.Value;
        }
    }

    /// <summary>Enables a channel so dispatch fans out over it.</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record EnableChannelCommand(Guid Id) : ICommand<Unit>;

    public sealed class EnableChannelCommandHandler(INotificationChannelRepository channels, IUnitOfWork unitOfWork, SiteState siteState)
        : ICommandHandler<EnableChannelCommand, Unit>
    {
        public async Task<Unit> Handle(EnableChannelCommand request, CancellationToken ct)
        {
            var channel = await ChannelLookup.RequireAsync(channels, NotificationWire.RequireSiteId(siteState), request.Id, ct);
            channel.Enable();
            await channels.UpdateAsync(channel, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Disables a channel so dispatch skips it.</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record DisableChannelCommand(Guid Id) : ICommand<Unit>;

    public sealed class DisableChannelCommandHandler(INotificationChannelRepository channels, IUnitOfWork unitOfWork, SiteState siteState)
        : ICommandHandler<DisableChannelCommand, Unit>
    {
        public async Task<Unit> Handle(DisableChannelCommand request, CancellationToken ct)
        {
            var channel = await ChannelLookup.RequireAsync(channels, NotificationWire.RequireSiteId(siteState), request.Id, ct);
            channel.Disable();
            await channels.UpdateAsync(channel, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    /// <summary>Deletes a channel and the credential it holds (NEW - the predecessor platform had no delete).</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record DeleteChannelCommand(Guid Id) : ICommand<Unit>;

    public sealed class DeleteChannelCommandHandler(
        INotificationChannelRepository channels, ISiteSecretStore secrets, IUnitOfWork unitOfWork, SiteState siteState)
        : ICommandHandler<DeleteChannelCommand, Unit>
    {
        public async Task<Unit> Handle(DeleteChannelCommand request, CancellationToken ct)
        {
            var siteId = NotificationWire.RequireSiteId(siteState);
            var channel = await ChannelLookup.RequireAsync(channels, siteId, request.Id, ct);
            if (channel.Transport.SecretReference is { } reference)
            {
                await secrets.DeleteAsync(siteId, reference, ct);
            }

            await channels.DeleteAsync(channel, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }

    internal static class ChannelLookup
    {
        /// <summary>The site's channel, or "does not exist" - a channel of ANOTHER site is indistinguishable from none.</summary>
        public static async Task<NotificationChannelAggregate> RequireAsync(INotificationChannelRepository channels, int siteId, Guid id, CancellationToken ct)
        {
            var channel = await channels.GetByIdAsync(id, ct);
            if (channel is null || channel.SiteId != siteId)
            {
                throw new InvalidOperationException($"Channel '{id}' does not exist.");
            }

            return channel;
        }
    }
}