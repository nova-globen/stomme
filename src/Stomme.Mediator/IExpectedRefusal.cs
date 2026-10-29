namespace Stomme.Mediator;

/// <summary>
/// Marks an exception as an EXPECTED refusal of a request - a duplicate, a conflict, a state the request may not move from -
/// rather than a fault: the caller asked for something the domain says no to, and the transport turns it into a 4xx.
/// <see cref="Behaviors.ExceptionBehavior{TRequest,TResponse}"/> logs such an exception at Information instead of Error, so
/// a refused request does not read as a failure in the site's log (a duplicate title was logged at fail). Put it on
/// your module's own refusal types: <c>public sealed class NoteConflictException(string message) : Exception(message), IExpectedRefusal;</c>.
/// </summary>
public interface IExpectedRefusal
{
}
