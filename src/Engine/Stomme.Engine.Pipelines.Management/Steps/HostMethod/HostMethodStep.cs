using System.ComponentModel;
using System.Dynamic;
using System.Reflection;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Management.Steps.HostMethod;

/// <summary>
/// Executes a public async method on a configured CLR type. Internal step used by <see cref="HostMethodStepProvider"/>.
/// </summary>
[Browsable(false)]
public class HostMethodStep : Step
{
    [JsonIgnore] internal Type HostType { get; set; } = null!;
    [JsonIgnore] internal string MethodName { get; set; } = null!;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var method = ResolveMethod(MethodName);
        await ExecuteInternalAsync(context, method).ConfigureAwait(false);
    }
    
    private async ValueTask ResumeAsync(StepExecutionContext context)
    {
        var metadata = context.PipelineExecutionContext.ResumedBookmarkContext?.Bookmark.Metadata; 
        if (metadata != null)
        {
            var callbackMethodName = metadata["HostMethodStepResumeCallback"];
            var callbackMethod = ResolveMethod(callbackMethodName);
            await ExecuteInternalAsync(context, callbackMethod).ConfigureAwait(false);
        }
    }

    private async Task ExecuteInternalAsync(StepExecutionContext context, MethodInfo method)
    {
        var cancellationToken = context.CancellationToken;
        var stepDescriptor = context.StepDescriptor;
        var inputDescriptors = stepDescriptor.Inputs.ToList();
        var serviceProvider = context.GetRequiredService<IServiceProvider>();
        var hostInstance = ActivatorUtilities.CreateInstance(serviceProvider, HostType);
        var args = await BuildArgumentsAsync(method, inputDescriptors, context, serviceProvider, cancellationToken).ConfigureAwait(false);
        var currentBookmarks = context.Bookmarks.ToList();

        ApplyPropertyInputs(hostInstance, inputDescriptors, context);

        var resultValue = await InvokeAndGetResultAsync(hostInstance, method, args).ConfigureAwait(false);
        SetOutput(stepDescriptor, resultValue, context);

        // By convention, if no bookmarks are created, complete the step. This may change in the future when we expose more control to the host type.
        var addedBookmarks = context.Bookmarks.Except(currentBookmarks).ToList();
        if (!addedBookmarks.Any())
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }
        
        // If bookmarks were created, overwrite the resume callback. We need to invoke the callback provided by the host type.
        foreach (var bookmark in addedBookmarks)
        {
            var callbackMethodName = bookmark.CallbackMethodName;
            
            if (string.IsNullOrWhiteSpace(callbackMethodName))
                continue;
            
            bookmark.CallbackMethodName = nameof(ResumeAsync);
            var metadata = bookmark.Metadata ?? new Dictionary<string, string>();
            
            metadata["HostMethodStepResumeCallback"] = callbackMethodName;
            bookmark.Metadata = metadata;
        }
    }

    private MethodInfo ResolveMethod(string methodName)
    {
        var method = HostType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
            throw new InvalidOperationException($"Method '{methodName}' not found on type '{HostType.Name}'.");

        return method;
    }

    private async ValueTask<object?[]> BuildArgumentsAsync(MethodInfo method, IReadOnlyCollection<InputDescriptor> inputDescriptors, StepExecutionContext context, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        // Allow multiple providers; call in order.
        var providers = serviceProvider.GetServices<IHostMethodParameterValueProvider>().ToList();
        if (providers.Count == 0)
            providers.Add(new DefaultHostMethodParameterValueProvider());

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            var providerContext = new HostMethodParameterValueProviderContext(
                serviceProvider,
                context,
                inputDescriptors,
                this,
                parameter,
                cancellationToken);

            var handled = false;
            object? value = null;

            foreach (var provider in providers)
            {
                var result = await provider.GetValueAsync(providerContext).ConfigureAwait(false);
                if (!result.Handled)
                    continue;

                handled = true;
                value = result.Value;
                break;
            }

            if (handled)
            {
                args[i] = value;
                continue;
            }

            // No provider handled it: fall back to parameter default value (if any).
            args[i] = parameter.HasDefaultValue ? parameter.DefaultValue : null;
        }

        return args;
    }

    private void ApplyPropertyInputs(object hostInstance, IReadOnlyCollection<InputDescriptor> inputDescriptors, StepExecutionContext context)
    {
        var hostPropertyLookup = HostType.GetProperties().ToDictionary(x => x.Name, x => x);

        foreach (var inputDescriptor in inputDescriptors)
        {
            if (!hostPropertyLookup.TryGetValue(inputDescriptor.Name, out var prop) || !prop.CanWrite)
                continue;

            var input = (Input?)inputDescriptor.ValueGetter(this);
            var inputValue = input != null ? context.Get(input.MemoryBlockReference()) : null;
            inputValue = ConvertIfNeeded(inputValue, prop.PropertyType);

            prop.SetValue(hostInstance, inputValue);
        }
    }

    private async Task<object?> InvokeAndGetResultAsync(object hostInstance, MethodInfo method, object?[] args)
    {
        var invocationResult = method.Invoke(hostInstance, args);

        // Synchronous methods.
        if (invocationResult is not Task task)
        {
            return method.ReturnType == typeof(void) ? null : invocationResult;
        }

        await task.ConfigureAwait(false);

        // Task<T>.
        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty?.GetValue(task);
        }

        // Task.
        return null;
    }

    private void SetOutput(StepDescriptor stepDescriptor, object? resultValue, StepExecutionContext context)
    {
        var outputDescriptor = stepDescriptor.Outputs.SingleOrDefault();
        if (outputDescriptor == null)
            return;

        var output = (Output?)outputDescriptor.ValueGetter(this);
        context.Set(output, resultValue, outputDescriptor.Name);
    }

    private static object? ConvertIfNeeded(object? value, Type targetType)
    {
        if (value is ExpandoObject expandoObject)
            return expandoObject.ConvertTo(targetType);

        return value;
    }
}
