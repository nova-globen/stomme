#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Mediator;
using Stomme.Modules.AiAssistant.Application;
using Stomme.Security.Abstractions;
using FluentValidation;

namespace Stomme.Modules.AiAssistant.Services
{
    /// <summary>
    /// The chat panel's service on the server (the Guestbook pattern): the same gated requests the API sends, through the
    /// mediator, with every refusal turned into the same code and message the API would answer with - so a server circuit
    /// and a WebAssembly client show the user the same thing.
    /// </summary>
    public sealed class ServerAiAssistantService(IMediator mediator, AssistantOptions options) : IAiAssistantService, ITransientService
    {
        public async Task<AssistantOutcome<List<AssistantConversationSummary>>> ListAsync() =>
            await RunAsync(async () => (await mediator.Send(new ListConversationsQuery())).ToList());

        public Task<AssistantOutcome<AssistantConversationView>> StartAsync() => RunAsync<AssistantConversationView>(async () => await mediator.Send(new StartConversationCommand()));

        public Task<AssistantOutcome<AssistantConversationView>> GetAsync(Guid conversationId) =>
            RunAsync(() => mediator.Send(new GetConversationQuery(conversationId)));

        public Task<AssistantOutcome<AssistantConversationView>> SendAsync(Guid conversationId, string message) =>
            RunAsync(() => mediator.Send(new SendAssistantMessageCommand(conversationId, message)));

        public Task<AssistantOutcome<AssistantArtifactApplyResult>> ApplyAsync(Guid conversationId, Guid artifactId) =>
            RunAsync(() => mediator.Send(new ApplyAssistantArtifactCommand(conversationId, artifactId)));

        public async Task<AssistantOutcome<bool>> DiscardAsync(Guid conversationId, Guid artifactId)
        {
            var outcome = await RunAsync<bool?>(async () => await mediator.Send(new DiscardAssistantArtifactCommand(conversationId, artifactId)) ? true : null);
            return new AssistantOutcome<bool>(outcome.Value ?? false, outcome.Refusal);
        }

        public Task<AssistantOutcome<AssistantSettingsView>> GetSettingsAsync() => RunAsync<AssistantSettingsView>(async () => await mediator.Send(new GetAssistantSettingsQuery()));

        public Task<AssistantOutcome<AssistantSettingsView>> SaveSettingsAsync(SaveAssistantSettingsRequest request) =>
            RunAsync<AssistantSettingsView>(async () => await mediator.Send(new SaveAssistantSettingsCommand(request.Enabled, request.Provider, request.Endpoint, request.Model, request.Temperature, request.ApiKey, request.ClearApiKey)));

        private async Task<AssistantOutcome<T>> RunAsync<T>(Func<Task<T?>> send)
        {
            if (!options.Enabled)
            {
                return new AssistantOutcome<T>(default!, new AssistantRefusalView(nameof(AssistantUnavailableReason.Disabled), "The assistant is switched off for this installation."));
            }

            try
            {
                return await send() is { } value
                    ? new AssistantOutcome<T>(value, null!)
                    : new AssistantOutcome<T>(default!, new AssistantRefusalView("404", "That conversation is not available."));
            }
            catch (Exception ex) when (AssistantRefusals.Describe(ex) is { } refusal)
            {
                return new AssistantOutcome<T>(default!, refusal.View);
            }
            catch (AccessDeniedException)
            {
                return new AssistantOutcome<T>(default!, new AssistantRefusalView("403", "You do not have access to that."));
            }
        }
    }
}
