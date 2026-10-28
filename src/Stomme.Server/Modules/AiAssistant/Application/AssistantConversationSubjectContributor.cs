#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant.Persistence;
using Stomme.Portability;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// A person's assistant conversations on a site, for their data package (D107 over D113): the transcript they wrote and
    /// received, the datasets attached as context, and every artifact proposed - its kind, title, state and draft. It is the
    /// person's own text, so it is carried, not left out; the erasure participant deletes the same rows.
    /// </summary>
    public sealed class AssistantConversationSubjectContributor(IAssistantConversationRepository conversations) : ISubjectDataContributor
    {
        public const string Type = "assistant-conversation";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await conversations.ListForUserAsync(scope.SiteId, scope.UserId, cancellationToken))
                .OrderBy(conversation => conversation.StartedUtc)
                .Select(conversation => new PortableEntity(
                    new PortableEntityRef(Type, conversation.Id.ToString("N", CultureInfo.InvariantCulture)),
                    conversation.StartedUtc.ToString("u", CultureInfo.InvariantCulture),
                    1,
                    JsonSerializer.Serialize(new
                    {
                        startedOnUtc = conversation.StartedUtc,
                        updatedOnUtc = conversation.UpdatedUtc,
                        status = conversation.Status.ToString(),
                        messages = conversation.Messages.Select(message => new { role = message.Role.ToString(), content = message.Content, atUtc = message.AtUtc }),
                        datasets = conversation.DataContext.Select(reference => reference.DatasetId),
                        artifacts = conversation.Artifacts.Select(artifact => new
                        {
                            kind = artifact.Kind.ToString(),
                            title = artifact.Title,
                            state = artifact.State.ToString(),
                            agent = artifact.Trace.AgentName,
                            draft = artifact.SpecJson,
                        }),
                    })))
                .ToList();
    }
}
