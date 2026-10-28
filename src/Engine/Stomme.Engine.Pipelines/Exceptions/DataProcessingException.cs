namespace Stomme.Engine.Pipelines.Exceptions;

/// <summary>
/// An exception that occurs during data processing.
/// </summary>
public class DataProcessingException(string message, Exception exception) : Exception(message, exception);