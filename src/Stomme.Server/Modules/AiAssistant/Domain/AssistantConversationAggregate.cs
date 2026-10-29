#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.AiAssistant.Domain
{
    /// <summary>Lifecycle of a conversation.</summary>
    public enum ConversationStatus
    {
        Active = 0,
        Closed = 1,
    }

    /// <summary>Who authored a message.</summary>
    public enum MessageRole
    {
        User = 0,
        Assistant = 1,
        System = 2,
        Tool = 3,
    }

    /// <summary>What an agent may propose. Each is applied through the owning module's ordinary gated command.</summary>
    public enum ArtifactKind
    {
        Pipeline = 0,
        Dashboard = 1,
        Widget = 2,
        LandingPage = 3,
    }

    /// <summary>A proposed artifact stays Proposed until the user applies or discards it - never auto-applied.</summary>
    public enum ArtifactState
    {
        Proposed = 0,
        Applied = 1,
        Discarded = 2,
    }

    public sealed record ConversationStartedEvent(Guid ConversationId, int SiteId, int UserId) : DomainEvent;

    public sealed record ArtifactGeneratedEvent(Guid ConversationId, Guid ArtifactId, string Kind, string Title) : DomainEvent;

    public sealed record ArtifactAppliedEvent(Guid ConversationId, Guid ArtifactId, string Kind) : DomainEvent;

    /// <summary>
    /// A conversation between a user and the assistant: the transcript, the datasets vetted as model context, and the artifacts agents have
    /// proposed. Its invariants: only dataset SUMMARIES are shared with a model unless raw rows are opted in;
    /// a proposed artifact may reference only datasets already attached (and so already vetted for the user); an artifact
    /// is never auto-applied - applying is a separate step that routes through the owning module's gated command.
    /// </summary>
    /// <remarks>
    /// A conversation is TENANT-OWNED (the storage-blind store is one per installation) and belongs to a SITE of that tenant and a Stomme user id, as every
    /// Stomme page-owned aggregate does. It holds the user's own text, so the right-to-erasure seam DELETES it. The
    /// transcript, the context and the artifacts ride the module store's JSON collection columns.
    /// </remarks>
    public sealed class AssistantConversationAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxMessages = 500;
        public const int MaxMessageLength = 32_000;
        public const int MaxTitleLength = 200;

        private readonly List<ConversationMessage> _messages = new();
        private readonly List<GeneratedArtifact> _artifacts = new();
        private readonly List<DataContextRef> _dataContext = new();

        // Materialisation constructor. Not for application use.
        private AssistantConversationAggregate()
        {
            TenantId = null!;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The Stomme user the conversation belongs to - the subject its RBAC checks and its erasure answer to.</summary>
        public int UserId { get; private set; }

        public ConversationStatus Status { get; private set; }

        public DateTime StartedUtc { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public IReadOnlyList<ConversationMessage> Messages => _messages;

        public IReadOnlyList<GeneratedArtifact> Artifacts => _artifacts;

        public IReadOnlyList<DataContextRef> DataContext => _dataContext;

        /// <summary>Opens a conversation for a user on a site of a tenant.</summary>
        public static AssistantConversationAggregate Start(Guid id, TenantId tenantId, int siteId, int userId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (id == Guid.Empty)
            {
                throw new ArgumentException("A conversation requires an id.", nameof(id));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A conversation belongs to a site.");
            }

            if (userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "A conversation belongs to a signed-in user.");
            }

            var conversation = new AssistantConversationAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                UserId = userId,
                Status = ConversationStatus.Active,
                StartedUtc = Utc(nowUtc),
                UpdatedUtc = Utc(nowUtc),
            };
            conversation.RaiseDomainEvent(new ConversationStartedEvent(id, siteId, userId));
            return conversation;
        }

        /// <summary>Appends a message to the transcript.</summary>
        public void AddMessage(MessageRole role, string content, DateTime nowUtc)
        {
            EnsureActive();
            if (_messages.Count >= MaxMessages)
            {
                throw new InvalidOperationException($"A conversation holds at most {MaxMessages} messages; start a new one.");
            }

            _messages.Add(ConversationMessage.Create(role, content, Utc(nowUtc)));
            Touch(nowUtc);
        }

        /// <summary>
        /// Attaches a dataset, already vetted for the user, as model context. Only a summary may be shared: a reference
        /// that shares raw rows is refused. Idempotent per dataset.
        /// </summary>
        public void AttachDataContext(DataContextRef reference, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(reference);
            EnsureActive();
            if (!reference.SummaryOnly)
            {
                throw new InvalidOperationException("Only dataset summaries may be shared with the model; sharing raw rows requires explicit opt-in.");
            }

            if (_dataContext.All(existing => existing.DatasetId != reference.DatasetId))
            {
                _dataContext.Add(reference);
                Touch(nowUtc);
            }
        }

        /// <summary>
        /// Stages an agent's artifact as a Proposed draft - never applied here. Every dataset it references must already be
        /// attached to the conversation, so an artifact cannot reach a dataset the user was not vetted for.
        /// </summary>
        public GeneratedArtifact ProposeArtifact(Guid artifactId, ArtifactKind kind, string title, string specJson, AgentTrace trace, IEnumerable<Guid>? referencedDatasetIds, DateTime nowUtc)
        {
            EnsureActive();
            if (artifactId == Guid.Empty)
            {
                throw new ArgumentException("An artifact requires an id.", nameof(artifactId));
            }

            ArgumentNullException.ThrowIfNull(trace);
            var references = (referencedDatasetIds ?? Array.Empty<Guid>()).Distinct().ToArray();
            var attached = _dataContext.Select(reference => reference.DatasetId).ToHashSet();
            var unreachable = references.Where(reference => !attached.Contains(reference)).ToArray();
            if (unreachable.Length > 0)
            {
                throw new InvalidOperationException(
                    "The artifact references datasets not attached to the conversation (out of the user's reach): " + string.Join(", ", unreachable) + ".");
            }

            var artifact = GeneratedArtifact.Propose(artifactId, kind, title, specJson, trace, references);
            _artifacts.Add(artifact);
            Touch(nowUtc);
            RaiseDomainEvent(new ArtifactGeneratedEvent(Id, artifactId, kind.ToString(), artifact.Title));
            return artifact;
        }

        /// <summary>Marks a proposed artifact applied - the owning module's command performs the write.</summary>
        public void ApplyArtifact(Guid artifactId, DateTime nowUtc)
        {
            EnsureActive();
            var artifact = FindArtifact(artifactId);
            artifact.Apply();
            Touch(nowUtc);
            RaiseDomainEvent(new ArtifactAppliedEvent(Id, artifactId, artifact.Kind.ToString()));
        }

        /// <summary>Discards a proposed artifact.</summary>
        public void DiscardArtifact(Guid artifactId, DateTime nowUtc)
        {
            EnsureActive();
            FindArtifact(artifactId).Discard();
            Touch(nowUtc);
        }

        /// <summary>Closes the conversation; nothing may be added afterwards.</summary>
        public void Close(DateTime nowUtc)
        {
            Status = ConversationStatus.Closed;
            Touch(nowUtc);
        }

        private GeneratedArtifact FindArtifact(Guid artifactId) =>
            _artifacts.FirstOrDefault(artifact => artifact.Id == artifactId)
            ?? throw new InvalidOperationException($"Artifact {artifactId} is not part of this conversation.");

        private void EnsureActive()
        {
            if (Status != ConversationStatus.Active)
            {
                throw new InvalidOperationException("The conversation is closed.");
            }
        }

        private void Touch(DateTime nowUtc) => UpdatedUtc = Utc(nowUtc);

        internal static DateTime Utc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => throw new ArgumentException("An instant is UTC.", nameof(value)),
        };
    }

    /// <summary>One turn of a conversation: who said it, the text, and when. Rides the aggregate's JSON column.</summary>
    public sealed class ConversationMessage
    {
        // Materialisation constructor. Not for application use.
        private ConversationMessage()
        {
            Content = string.Empty;
        }

        private ConversationMessage(MessageRole role, string content, DateTime atUtc)
        {
            Role = role;
            Content = content;
            AtUtc = atUtc;
        }

        public MessageRole Role { get; private set; }

        public string Content { get; private set; }

        public DateTime AtUtc { get; private set; }

        public static ConversationMessage Create(MessageRole role, string content, DateTime atUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(content);
            if (content.Length > AssistantConversationAggregate.MaxMessageLength)
            {
                throw new ArgumentException($"A message is at most {AssistantConversationAggregate.MaxMessageLength} characters.", nameof(content));
            }

            return new ConversationMessage(role, content, atUtc);
        }
    }

    /// <summary>
    /// A dataset the conversation may use as model context. By contract only its SUMMARY is shared with a model
    /// (<see cref="SummaryOnly"/>), and it is attached only after the user's access to it was checked.
    /// </summary>
    public sealed class DataContextRef
    {
        // Materialisation constructor. Not for application use.
        private DataContextRef()
        {
        }

        private DataContextRef(Guid datasetId, bool summaryOnly)
        {
            DatasetId = datasetId;
            SummaryOnly = summaryOnly;
        }

        public Guid DatasetId { get; private set; }

        public bool SummaryOnly { get; private set; }

        public static DataContextRef Create(Guid datasetId, bool summaryOnly = true)
        {
            if (datasetId == Guid.Empty)
            {
                throw new ArgumentException("A data-context reference requires a dataset id.", nameof(datasetId));
            }

            return new DataContextRef(datasetId, summaryOnly);
        }
    }

    /// <summary>Provenance of a proposed artifact: which agent produced it and a short rationale.</summary>
    public sealed class AgentTrace
    {
        // Materialisation constructor. Not for application use.
        private AgentTrace()
        {
            AgentName = string.Empty;
            Summary = string.Empty;
        }

        private AgentTrace(string agentName, string summary)
        {
            AgentName = agentName;
            Summary = summary;
        }

        public string AgentName { get; private set; }

        public string Summary { get; private set; }

        public static AgentTrace Create(string agentName, string? summary)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
            return new AgentTrace(agentName.Trim(), (summary ?? string.Empty).Trim());
        }
    }

    /// <summary>
    /// A draft an agent proposed - a pipeline, board, widget or landing page held in the owning module's own serialized form
    /// (<see cref="SpecJson"/>). Proposed until the user applies or discards it; applying routes through the owning
    /// module's command, outside the conversation.
    /// </summary>
    public sealed class GeneratedArtifact : Entity<Guid>
    {
        // Materialisation constructor. Not for application use.
        private GeneratedArtifact()
        {
            Title = string.Empty;
            SpecJson = string.Empty;
            Trace = null!;
            ReferencedDatasetIds = Array.Empty<Guid>();
        }

        private GeneratedArtifact(Guid id, ArtifactKind kind, string title, string specJson, AgentTrace trace, IEnumerable<Guid> references)
        {
            Id = id;
            Kind = kind;
            Title = title;
            SpecJson = specJson;
            Trace = trace;
            State = ArtifactState.Proposed;
            ReferencedDatasetIds = references.ToArray();
        }

        public ArtifactKind Kind { get; private set; }

        public string Title { get; private set; }

        public string SpecJson { get; private set; }

        public ArtifactState State { get; private set; }

        public AgentTrace Trace { get; private set; }

        /// <summary>The datasets the artifact draws on - each attached, and so vetted, before it was proposed.</summary>
        public IReadOnlyList<Guid> ReferencedDatasetIds { get; private set; }

        internal static GeneratedArtifact Propose(Guid id, ArtifactKind kind, string title, string specJson, AgentTrace trace, IEnumerable<Guid> references)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(specJson);
            var clean = title.Trim();
            if (clean.Length > AssistantConversationAggregate.MaxTitleLength)
            {
                throw new ArgumentException($"An artifact title is at most {AssistantConversationAggregate.MaxTitleLength} characters.", nameof(title));
            }

            return new GeneratedArtifact(id, kind, clean, specJson, trace, references);
        }

        internal void Apply()
        {
            if (State != ArtifactState.Proposed)
            {
                throw new InvalidOperationException($"Only a proposed artifact can be applied; this one is {State}.");
            }

            State = ArtifactState.Applied;
        }

        internal void Discard()
        {
            if (State != ArtifactState.Proposed)
            {
                throw new InvalidOperationException($"Only a proposed artifact can be discarded; this one is {State}.");
            }

            State = ArtifactState.Discarded;
        }
    }
}
