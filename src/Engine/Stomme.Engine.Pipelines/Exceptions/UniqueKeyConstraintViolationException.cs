namespace Stomme.Engine.Pipelines.Exceptions;

/// <summary>
/// An exception that when a unique key constraint has been violated.
/// </summary>
public class UniqueKeyConstraintViolationException(string message, Exception exception) : Exception(message, exception);