namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Remembers the RavenDB change vector an aggregate was loaded with, so the repository can enforce
/// optimistic concurrency across its per-operation sessions: a load-modify-save within one scope (the
/// usual command-handler flow) saves conditionally on the loaded change vector, and a stale write is
/// rejected. Registered scoped, so it spans the repositories used within a single request/unit of work.
/// </summary>
public sealed class RavenChangeVectorTracker
{
    private readonly Dictionary<string, string> _changeVectors = new(StringComparer.Ordinal);

    public void Remember(string documentId, string? changeVector)
    {
        if (!string.IsNullOrEmpty(changeVector))
        {
            _changeVectors[documentId] = changeVector;
        }
    }

    public string? Get(string documentId) =>
        _changeVectors.TryGetValue(documentId, out var changeVector) ? changeVector : null;
}
