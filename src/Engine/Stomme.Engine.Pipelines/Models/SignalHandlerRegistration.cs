namespace Stomme.Engine.Pipelines.Models;

internal record SignalHandlerRegistration(Type SignalType, Func<object, SignalContext, ValueTask> Handler);