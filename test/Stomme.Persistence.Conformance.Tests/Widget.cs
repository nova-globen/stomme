using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The conformance aggregate. Deliberately shaped like the framework store's aggregates:
/// <list type="bullet">
/// <item><c>AggregateRoot&lt;int&gt;</c> — an INT key, left unset on insert for the database to assign.
/// This is the shape the framework store actually uses and the shape the RavenDB provider used to
/// destroy (every insert blind-upserted <c>Widget/0</c>). The prior live test used a caller-assigned
/// <c>Guid</c> and so could never have caught it.</item>
/// <item>The four audit members mirror <c>Stomme.Models.IAuditable</c> — the contract the framework
/// store's aggregates implement — member for member, which is precisely the shape
/// <c>AuditStamp</c> matches. The interface itself is not implemented here: it lives in
/// <c>Stomme.Shared</c>, whose gRPC contract build has no business in a storage test.</item>
/// </list>
/// </summary>
public sealed class Widget : AggregateRoot<int>
{
    private Widget()
    {
    }

    public Widget(string name) => Name = name;

    public string Name { get; private set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedOn { get; set; }

    public string ModifiedBy { get; set; } = string.Empty;

    public DateTime ModifiedOn { get; set; }

    public void Rename(string name) => Name = name;

    public void RaiseCreated() => RaiseDomainEvent(new WidgetCreated(Name));
}

public sealed record WidgetCreated(string Name) : DomainEvent;
