#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Mediator;
using Stomme.Modules.AiAssistant.Domain;
using Stomme.Security.Abstractions;
using FluentValidation;

namespace Stomme.Modules.AiAssistant.Application
{
    // ============================================================================================================
    // The assistant's gated requests (BL-97 increment 10b, D113): every one under assistant:use, decided by the host's
    // authorization behaviour before a handler runs (D99). The key is to TALK to the assistant; what an agent reads and what
    // an apply writes are the owning modules' own requests, gated again for the same user.
    // ============================================================================================================

    [Requires(AssistantPermissions.Use)]
    public sealed record StartConversationCommand : ICommand<AssistantConversationView>;

    public sealed class StartConversationCommandHandler(AssistantConversationService conversations) : ICommandHandler<StartConversationCommand, AssistantConversationView>
    {
        public Task<AssistantConversationView> Handle(StartConversationCommand request, CancellationToken ct) => conversations.StartAsync(ct);
    }

    [Requires(AssistantPermissions.Use)]
    public sealed record ListConversationsQuery : IQuery<IReadOnlyList<AssistantConversationSummary>>;

    public sealed class ListConversationsQueryHandler(AssistantConversationService conversations) : IQueryHandler<ListConversationsQuery, IReadOnlyList<AssistantConversationSummary>>
    {
        public Task<IReadOnlyList<AssistantConversationSummary>> Handle(ListConversationsQuery request, CancellationToken ct) => conversations.ListAsync(ct);
    }

    [Requires(AssistantPermissions.Use)]
    public sealed record GetConversationQuery(Guid Id) : IQuery<AssistantConversationView?>;

    public sealed class GetConversationQueryHandler(AssistantConversationService conversations) : IQueryHandler<GetConversationQuery, AssistantConversationView?>
    {
        public Task<AssistantConversationView?> Handle(GetConversationQuery request, CancellationToken ct) => conversations.GetAsync(request.Id, ct);
    }

    [Requires(AssistantPermissions.Use)]
    public sealed record SendAssistantMessageCommand(Guid ConversationId, string Message) : ICommand<AssistantConversationView?>;

    public sealed class SendAssistantMessageCommandValidator : AbstractValidator<SendAssistantMessageCommand>
    {
        public SendAssistantMessageCommandValidator()
        {
            RuleFor(command => command.Message).NotEmpty().MaximumLength(AssistantConversationAggregate.MaxMessageLength);
        }
    }

    public sealed class SendAssistantMessageCommandHandler(AssistantConversationService conversations) : ICommandHandler<SendAssistantMessageCommand, AssistantConversationView?>
    {
        public Task<AssistantConversationView?> Handle(SendAssistantMessageCommand request, CancellationToken ct) => conversations.SendAsync(request.ConversationId, request.Message, ct);
    }

    [Requires(AssistantPermissions.Use)]
    public sealed record ApplyAssistantArtifactCommand(Guid ConversationId, Guid ArtifactId) : ICommand<AssistantArtifactApplyResult?>;

    public sealed class ApplyAssistantArtifactCommandHandler(AssistantConversationService conversations) : ICommandHandler<ApplyAssistantArtifactCommand, AssistantArtifactApplyResult?>
    {
        public Task<AssistantArtifactApplyResult?> Handle(ApplyAssistantArtifactCommand request, CancellationToken ct) => conversations.ApplyArtifactAsync(request.ConversationId, request.ArtifactId, ct);
    }

    [Requires(AssistantPermissions.Use)]
    public sealed record DiscardAssistantArtifactCommand(Guid ConversationId, Guid ArtifactId) : ICommand<bool>;

    public sealed class DiscardAssistantArtifactCommandHandler(AssistantConversationService conversations) : ICommandHandler<DiscardAssistantArtifactCommand, bool>
    {
        public Task<bool> Handle(DiscardAssistantArtifactCommand request, CancellationToken ct) => conversations.DiscardArtifactAsync(request.ConversationId, request.ArtifactId, ct);
    }
}
