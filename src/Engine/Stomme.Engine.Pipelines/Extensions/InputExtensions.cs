using System.Runtime.CompilerServices;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extensions on <see cref="ExpressionExecutionContext"/> and <see cref="StepExecutionContext"/>
/// </summary>
public static class InputExtensions
{
    /// <param name="input">The input.</param>
    /// <typeparam name="T">The type of the input.</typeparam>
    extension<T>(Input<T>? input)
    {
        /// <summary>
        /// Returns the value of the specified input, or a default value if the input is not found.
        /// </summary>
        public T? GetOrDefault(StepExecutionContext context, Func<T>? defaultValue = default)
        {
            var value = context.Get(input);
            return value != null ? value : defaultValue != null ? defaultValue.Invoke() : default;
        }

        /// <summary>
        /// Returns the value of the specified input, or a default value if the input is not found.
        /// </summary>
        public T? GetOrDefault(ExpressionExecutionContext context, Func<T>? defaultValue = default)
        {
            var value = context.Get(input);
            return value != null ? value : defaultValue != null ? defaultValue.Invoke() : default;
        }

        /// <summary>
        /// Returns the value of the specified input.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="inputName">The name of the input.</param>
        /// <returns>The value of the specified input.</returns>
        /// <exception cref="Exception">Throws an exception if the input is not found.</exception>
        public T Get(StepExecutionContext context, [CallerArgumentExpression("input")] string? inputName = default)
        {
            return context.Get(input) ?? throw new Exception($"{inputName} is required.");
        }

        /// <summary>
        /// Returns the value of the specified input.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="inputName">The name of the input.</param>
        /// <returns>The value of the specified input.</returns>
        /// <exception cref="Exception">Throws an exception if the input is not found.</exception>
        public T Get(ExpressionExecutionContext context, [CallerArgumentExpression("input")] string? inputName = default)
        {
            return context.Get(input) ?? throw new Exception($"{inputName} is required.");
        }
    }
}