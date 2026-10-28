using System.Linq.Expressions;
using System.Reflection;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extension methods for <see cref="IStep"/>.
/// </summary>
[PublicAPI]
public static class StepExtensions
{
    /// <param name="step">The step to get the output from.</param>
    extension(IStep step)
    {
        /// <summary>
        /// Gets the input properties of the specified step.
        /// </summary>
        public IDictionary<string, Input> GetNamedInputs()
        {
            var inputProps = step.GetInputProperties().ToList();

            var query =
                from inputProp in inputProps
                let inputValue = (Input?)inputProp.GetValue(step)
                where inputValue != null
                select (inputProp, inputValue);

            return query.DistinctBy(x => x.inputProp.Name).ToDictionary(x => x.inputProp.Name, x => x.inputValue);
        }

        /// <summary>
        /// Gets the input properties of the specified step.
        /// </summary>
        public IEnumerable<Input> GetInputs() => GetNamedInputs(step).Values;

        /// <summary>
        /// Gets the input with the specified name.
        /// </summary>
        public Input? GetInput(string inputName) => GetNamedInputs(step).TryGetValue(inputName, out var input) ? input : null;

        /// <summary>
        /// Gets the output properties of the specified step.
        /// </summary>
        public IEnumerable<NamedOutput> GetOutputs()
        {
            var outputProps = step.GetType().GetProperties().Where(x => typeof(Output).IsAssignableFrom(x.PropertyType)).ToList();

            var query =
                from outputProp in outputProps
                let output = (Output?)outputProp.GetValue(step)
                where output != null
                select new NamedOutput(outputProp.Name, output);

            return query.Select(x => x!).ToList();
        }

        /// <summary>
        /// Gets the output with the specified name.
        /// </summary>
        /// <param name="context">The step execution context.</param>
        /// <param name="outputName">Name of the output.</param>
        /// <returns>The output value.</returns>
        public object? GetOutput(StepExecutionContext context, string? outputName = null)
        {
            var pipelineExecutionContext = context.PipelineExecutionContext;
            var outputRegister = pipelineExecutionContext.GetStepOutputRegister();
        
            // If the provided step execution context is the same as the current step's execution context, we return the exact output value of the current step execution context.
            if(context.Step.NodeId == step.NodeId)
                return outputRegister.FindOutputByStepInstanceId(context.Id, outputName);
        
            // If the provided step execution context is different from the current step's execution context, we look for the last output value of the step.
            return outputRegister.FindOutputByStepId(step.Id, outputName);
        }

        /// <summary>
        /// Gets the output with the specified name.
        /// </summary>
        /// <param name="context">The expression execution context.</param>
        /// <param name="outputName">Name of the output.</param>
        /// <returns>The output value.</returns>
        public object? GetOutput(ExpressionExecutionContext context, string? outputName = null)
        {
            var stepExecutionContext = context.GetStepExecutionContext();

            if (stepExecutionContext == null)
                return null;
        
            return step.GetOutput(stepExecutionContext, outputName);
        }

        /// <summary>
        /// Gets the output with the specified name.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="outputName">Name of the output.</param>
        /// <typeparam name="T">The type of the output.</typeparam>
        /// <returns>The output value.</returns>
        public T? GetOutput<T>(StepExecutionContext context, string outputName)
        {
            var outputValue = step.GetOutput(context, outputName);
            return outputValue == null ? default! : outputValue.ConvertTo<T>();
        }

        /// <summary>
        /// Gets the output with the specified name.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="outputName">Name of the output.</param>
        /// <typeparam name="T">The type of the output.</typeparam>
        /// <returns>The output value.</returns>
        public T? GetOutput<T>(ExpressionExecutionContext context, string outputName)
        {
            return step.GetOutput<T>(context.GetStepExecutionContext(), outputName);
        }
    }

    /// <param name="step">The step.</param>
    /// <typeparam name="TStep">The type of the step.</typeparam>
    extension<TStep>(TStep step)
    {
        /// <summary>
        /// Gets the output with the specified name.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="outputExpression">The output expression.</param>
        /// <typeparam name="T">The type of the output.</typeparam>
        /// <returns>The output value.</returns>
        public T? GetOutput<T>(StepExecutionContext context, Expression<Func<TStep, object?>> outputExpression)
        {
            var outputName = outputExpression.GetPropertyName();
            return ((IStep)step!).GetOutput<T>(context, outputName);
        }

        /// <summary>
        /// Gets the output with the specified name.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="outputExpression">The output expression.</param>
        /// <typeparam name="T">The type of the output.</typeparam>
        /// <returns>The output value.</returns>
        public T? GetOutput<T>(ExpressionExecutionContext context, Expression<Func<TStep, object?>> outputExpression)
        {
            var outputName = outputExpression.GetPropertyName();
            return ((IStep)step!).GetOutput<T>(context, outputName);
        }
    }

    /// <param name="step">The step.</param>
    extension(IStep step)
    {
        /// <summary>
        /// Gets the Result output of the specified step.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <typeparam name="T">The type as which to return the output.</typeparam>
        /// <returns>The output value.</returns>
        public T? GetResult<T>(ExpressionExecutionContext context)
        {
            var value = GetResult(step, context);
            return value == null ? default! : value.ConvertTo<T>();
        }

        /// <summary>
        /// Gets the Result output of the specified step.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>The output value.</returns>
        public object? GetResult(ExpressionExecutionContext context)
        {
            return step.GetResult(context.GetStepExecutionContext());
        }

        /// <summary>
        /// Gets the Result output of the specified step.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>The output value.</returns>
        public object? GetResult(StepExecutionContext context)
        {
            return step.GetOutput(context, StepOutputRegister.DefaultOutputName);
        }
    }

    /// <summary>
    /// Gets the Result output of the specified step.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="step">The step.</param>
    /// <returns>The output value.</returns>
    public static object? GetResult(this StepExecutionContext context, IStep step)
    {
        return step.GetOutput(context, StepOutputRegister.DefaultOutputName);
    }

    /// <summary>
    /// Gets the result of the last step.
    /// </summary>
    /// <param name="context">The context.</param>
    public static T? GetLastResult<T>(this ExpressionExecutionContext context)
    {
        var value = GetLastResult(context.GetPipelineExecutionContext());
        return value == null ? default! : value.ConvertTo<T>();
    }

    /// <summary>
    /// Gets the result of the last step.
    /// </summary>
    /// <param name="context">The context.</param>
    public static object? GetLastResult(this StepExecutionContext context)
    {
        return context.PipelineExecutionContext.GetLastResult();
    }
    
    /// <summary>
    /// Gets the result of the last step.
    /// </summary>
    /// <param name="context">The context.</param>
    public static object? GetLastResult(this PipelineExecutionContext context)
    {
        return context.GetLastStepResult();
    }

    extension(IStep step)
    {
        /// <summary>
        /// Gets the input properties of the specified step.
        /// </summary>
        public IEnumerable<PropertyInfo> GetInputProperties() => step.GetType().GetProperties().Where(x => typeof(Input).IsAssignableFrom(x.PropertyType)).ToList();

        /// <summary>
        /// Gets the method for the specified method name on the specified step.
        /// </summary>
        public TDelegate GetDelegate<TDelegate>(string methodName) where TDelegate : Delegate
        {
            var stepType = step.GetType();
            const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var resumeMethodInfo = default(MethodInfo?);
            var currentType = stepType;

            while (currentType != null && resumeMethodInfo == null)
            {
                resumeMethodInfo = currentType.GetMethod(methodName, bindingFlags);
                currentType = currentType.BaseType;
            }

            if (resumeMethodInfo == null)
                throw new Exception($"Can't find method name {methodName} on type {stepType} or its base type {stepType.BaseType}");

            return resumeMethodInfo.IsStatic ? (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), resumeMethodInfo) : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), step, resumeMethodInfo);
        }

        /// <summary>
        /// Gets the Resume method for the specified step.
        /// </summary>
        public ExecuteStepDelegate GetResumeStepDelegate(string resumeMethodName) => step.GetDelegate<ExecuteStepDelegate>(resumeMethodName);

        /// <summary>
        /// Gets the Child Step Completed method for the specified step.
        /// </summary>
        public StepCompletionCallback GetStepCompletionCallback(string completionMethodName) => step.GetDelegate<StepCompletionCallback>(completionMethodName);
    }
}