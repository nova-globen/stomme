using System.Collections;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides extension methods for the StepExecutionContext class.
/// </summary>
public static class ItemSourceStepExecutionContextExtensions
{
    /// <summary>
    /// Retrieves the item source and returns it as an asynchronous enumerable.
    /// Supported types are <see cref="IEnumerable{T}"/>, <see cref="IAsyncEnumerable{T}"/> and <see> <cref>IAsyncEnumerable{IEnumerable{T}}</cref></see>.
    /// </summary>
    /// <typeparam name="T">The type of the items in the source collection.</typeparam>
    /// <param name="context">The step execution context.</param>
    /// <param name="input">The input object.</param>
    /// <returns>An asynchronous enumerable of items from the source collection.</returns>
    public static async IAsyncEnumerable<T> GetItemSource<T>(this StepExecutionContext context, Input<object> input)
    {
        var items = context.Get(input);

        if (items == null)
            yield break;
        
        var itemsType = items.GetType();
        if (itemsType.Name == "AsyncEnumerableAdapter`1")
        {
            var isBatch = itemsType.GenericTypeArguments.Length == 1 && typeof(IEnumerable).IsAssignableFrom(itemsType.GenericTypeArguments[0]);

            if (isBatch)
            {
                if(items is IAsyncEnumerable<IEnumerable<T>> typedItems)
                    await foreach (var typedItem in typedItems.ConfigureAwait(false))
                    foreach (T item in typedItem)
                        yield return item;
            }
        }
        else if (items is IAsyncEnumerable<T> asyncEnumerable)
        {
            await foreach (T item in asyncEnumerable.ConfigureAwait(false))
                yield return item;
        }
        else if (items is IEnumerable<T> enumerable)
        {
            foreach (T item in enumerable)
                yield return item;
        }
    }
}