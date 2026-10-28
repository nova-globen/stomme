using System.Reflection;

namespace Stomme.Engine.Mediator.Middleware;

/// <summary>
/// Provides a set of static methods for working with middleware.
/// </summary>
public static class MiddlewareHelpers
{
    /// <summary>
    /// Gets the Invoke or InvokeAsync method from the middleware type.
    /// </summary>
    /// <param name="middleware">The middleware type.</param>
    /// <returns>The Invoke or InvokeAsync method.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the Invoke or InvokeAsync method cannot be found or the return type is not Task or ValueTask.</exception>
    public static MethodInfo GetInvokeMethod(Type middleware)
    {
        const string invokeMethodName = "Invoke";
        const string invokeAsyncMethodName = "InvokeAsync";
        var methods = middleware.GetMethods(BindingFlags.Instance | BindingFlags.Public);
        var invokeMethods = methods.Where(m => string.Equals(m.Name, invokeMethodName, StringComparison.Ordinal) || string.Equals(m.Name, invokeAsyncMethodName, StringComparison.Ordinal)).ToArray();

        switch (invokeMethods.Length)
        {
            case > 1:
                throw new InvalidOperationException("Multiple Invoke methods were found. Use either Invoke or InvokeAsync.");
            case 0:
                throw new InvalidOperationException("No Invoke methods were found. Use either Invoke or InvokeAsync");
        }

        var methodInfo = invokeMethods[0];

        if (!typeof(Task).IsAssignableFrom(methodInfo.ReturnType) && !typeof(ValueTask).IsAssignableFrom(methodInfo.ReturnType))
            throw new InvalidOperationException($"The {methodInfo.Name} method must return Task or ValueTask");

        return methodInfo;
    }

    private static readonly MethodInfo UnwrapMethod = typeof(MiddlewareHelpers).GetMethod(nameof(UnwrapAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Awaits a <see cref="Task{TResult}"/> whose result type is only known at runtime and returns its result boxed.
    /// The handler invokers use this instead of reflecting on <c>Task&lt;T&gt;.Result</c>: the task is awaited,
    /// never blocked on (D33).
    /// </summary>
    /// <param name="task">A task whose runtime type is <see cref="Task{TResult}"/> for <paramref name="resultType"/>.</param>
    /// <param name="resultType">The task's result type.</param>
    public static Task<object?> AwaitAndUnboxAsync(Task task, Type resultType)
    {
        var unwrap = UnwrapMethod.MakeGenericMethod(resultType);
        return (Task<object?>)unwrap.Invoke(null, [task])!;
    }

    private static async Task<object?> UnwrapAsync<T>(Task<T> task) => await task.ConfigureAwait(false);
}
