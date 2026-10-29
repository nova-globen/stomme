using System;
using Stomme.SharedKernel.Domain;

namespace Stomme.Sdk.Smoke;

/// <summary>
/// A storage-blind aggregate: it names an id + a couple of scalar properties, and authors NO EF
/// <c>DbContext</c>, NO RavenDB store, NO file store, NO provider branch. The HOST decides where it is
/// persisted (SQLite / PostgreSQL / RavenDB / file), keyed on <c>Database:DefaultDBType</c>. It compiles
/// against <c>Stomme.SharedKernel</c> from a package — no source reference to the framework.
/// </summary>
public sealed class TrivialAggregate : AggregateRoot<Guid>
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }

    public void Rename(string name) => Name = name;
}
