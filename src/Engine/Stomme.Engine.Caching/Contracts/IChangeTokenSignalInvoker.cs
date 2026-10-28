using Microsoft.Extensions.Primitives;

namespace Stomme.Engine.Caching;

/// <summary>
/// Triggers the change token associated with the specified key.
/// </summary>
public interface IChangeTokenSignalInvoker
{
    /// <summary>
    /// Gets a change token for the specified key.
    /// </summary>
    IChangeToken GetToken(string key);
    
    /// <summary>
    /// Triggers the change token for the specified key.
    /// </summary>
    ValueTask TriggerTokenAsync(string key, CancellationToken cancellationToken = default);
}