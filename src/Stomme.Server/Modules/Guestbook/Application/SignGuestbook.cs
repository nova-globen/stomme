using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Guestbook.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.SharedKernel.Domain;
using FluentValidation;
using WireModel = Stomme.Modules.Guestbook.Models.GuestbookEntry;

namespace Stomme.Modules.Guestbook.Application
{
    /// <summary>
    /// CQRS COMMAND: sign a guestbook. One command per file with its validator and handler nested, the
    /// handler-pattern shape #07 lifts from the predecessor platform. Dispatched through <c>IMediator.Send</c> — never called
    /// directly — so it flows through the whole behavior pipeline (exception → logging → performance →
    /// validation → handler).
    /// </summary>
    public sealed record SignGuestbookCommand(int ModuleId, string Author, string Message) : ICommand<WireModel>;

    /// <summary>
    /// The FluentValidation validator the <c>ValidationBehavior</c> runs before the handler. An invalid
    /// command (blank author/message, over length, non-positive module) is rejected here with a
    /// <see cref="ValidationException"/> and the handler never runs — the visible "the pipeline rejected a bad
    /// command" evidence D74 drives.
    /// </summary>
    public sealed class SignGuestbookCommandValidator : AbstractValidator<SignGuestbookCommand>
    {
        public SignGuestbookCommandValidator()
        {
            RuleFor(command => command.ModuleId).GreaterThan(0);
            RuleFor(command => command.Author)
                .NotEmpty().WithMessage("An author name is required.")
                .MaximumLength(AuthorName.MaxLength);
            RuleFor(command => command.Message)
                .NotEmpty().WithMessage("A guestbook message is required.")
                .MaximumLength(GuestbookEntryAggregate.MaxMessageLength);
        }
    }

    /// <summary>
    /// The command handler. It builds the aggregate through its <c>Sign</c> factory (which raises the domain
    /// event), persists it through the clean-arch repository, drains the unit of work — which dispatches the
    /// <see cref="GuestbookEntrySignedDomainEvent"/> to the FRAMEWORK dispatcher in this scope — and then
    /// PUBLISHES a <see cref="GuestbookSignedNotification"/> through the MEDIATOR. Both in-process fan-out
    /// mechanisms fire, deliberately (D74 keeps them coexisting; it does not collapse them). Since BL-36(c)
    /// it ALSO announces the signature across contexts: a <see cref="GuestbookEntrySignedIntegrationEvent"/>
    /// over the bus, after commit — the bus is opt-in, so with messaging off no <c>IEventBus</c> is
    /// registered and the enumerable is empty (a stock install is unaffected).
    /// </summary>
    public sealed class SignGuestbookCommandHandler(
        IGuestbookRepository repository,
        IUnitOfWork unitOfWork,
        IMediator mediator,
        ITenantContext tenantContext,
        System.Collections.Generic.IEnumerable<IEventBus> eventBus)
        : ICommandHandler<SignGuestbookCommand, WireModel>
    {
        public async Task<WireModel> Handle(SignGuestbookCommand request, CancellationToken ct)
        {
            // BL-111: the signature is stamped with the AMBIENT tenant (the request's alias resolves it; a job's scope
            // sets it). No tenant - an unresolved alias, a system scope - is refused rather than written under a
            // default that every real tenant's filter would then hide.
            var tenantId = tenantContext.CurrentTenantId
                ?? throw new InvalidOperationException("A guestbook is signed under a tenant; none is resolved in this scope.");
            var entry = GuestbookEntryAggregate.Sign(tenantId, request.ModuleId, request.Author, request.Message);

            await repository.AddAsync(entry, ct);
            await unitOfWork.SaveChangesAsync(ct);

            await mediator.Publish(
                new GuestbookSignedNotification(entry.Id, entry.ModuleId, entry.Author.Value), ct);

            var bus = eventBus.FirstOrDefault();
            if (bus != null)
            {
                await bus.PublishAsync(
                    new GuestbookEntrySignedIntegrationEvent(entry.Id, entry.ModuleId, entry.Author.Value), ct);
            }

            return GuestbookMapper.ToWire(entry);
        }
    }
}
