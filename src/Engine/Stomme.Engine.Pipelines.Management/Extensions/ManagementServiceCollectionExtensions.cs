using System.Reflection;
using Stomme.Engine.Expressions.Extensions;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Options;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Pipelines.Management.Extensions;

/// <summary>
/// <see cref="IServiceCollection"/> extension methods for registering Stomme step types
/// and variable descriptors via <see cref="ManagementOptions"/>.
/// </summary>
/// <remarks>
/// These extensions are the shell-feature-compatible replacement for calling
/// <c>PipelineManagementFeature.AddStepsFrom&lt;T&gt;()</c> in the old-style feature system.
/// Because <c>services.Configure&lt;ManagementOptions&gt;</c> is additive, multiple features
/// can independently register steps without any coupling to each other.
/// </remarks>
public static class ManagementServiceCollectionExtensions
{
    // -------------------------------------------------------------------------
    // Steps
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers the supplied step <paramref name="types"/> with <see cref="ManagementOptions"/>.
    /// </summary>
    public static IServiceCollection AddSteps(this IServiceCollection services, IEnumerable<Type> types) =>
        services.Configure<ManagementOptions>(options =>
        {
            foreach (var type in types)
                options.StepTypes.Add(type);
        });

    /// <summary>
    /// Registers a single step type <typeparamref name="TStep"/> with <see cref="ManagementOptions"/>.
    /// </summary>
    public static IServiceCollection AddStep<TStep>(this IServiceCollection services)
        where TStep : IStep =>
        services.AddSteps([typeof(TStep)]);

    /// <summary>
    /// Scans <paramref name="assembly"/> and registers every concrete, non-generic
    /// <see cref="IStep"/> implementation found.
    /// </summary>
    public static IServiceCollection AddStepsFrom(this IServiceCollection services, Assembly assembly)
    {
        var types = assembly.GetExportedTypes()
            .Where(t => typeof(IStep).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });
        return services.AddSteps(types);
    }

    /// <summary>
    /// Scans the assembly that contains <typeparamref name="TMarker"/> and registers every
    /// concrete, non-generic <see cref="IStep"/> implementation found.
    /// </summary>
    public static IServiceCollection AddStepsFrom<TMarker>(this IServiceCollection services) =>
        services.AddStepsFrom(typeof(TMarker).Assembly);

    // -------------------------------------------------------------------------
    // Variable descriptors
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers the supplied <paramref name="descriptors"/> with <see cref="ManagementOptions"/>.
    /// </summary>
    public static IServiceCollection AddVariableDescriptors(
        this IServiceCollection services,
        IEnumerable<VariableDescriptor> descriptors) =>
        services.Configure<ManagementOptions>(options =>
        {
            foreach (var descriptor in descriptors)
                options.VariableDescriptors.Add(descriptor);
        });

    /// <summary>
    /// Registers a single variable descriptor.
    /// </summary>
    public static IServiceCollection AddVariableDescriptor(
        this IServiceCollection services,
        VariableDescriptor descriptor) =>
        services.AddVariableDescriptors([descriptor]);

    /// <summary>
    /// Registers a variable descriptor for <typeparamref name="T"/> with the given
    /// <paramref name="category"/> and optional <paramref name="description"/>.
    /// </summary>
    public static IServiceCollection AddVariableDescriptor<T>(
        this IServiceCollection services,
        string category,
        string? description = null) =>
        services.AddVariableDescriptor(new(typeof(T), category, description));

    /// <summary>
    /// Adds a variable type and its alias to the specified service collection.
    /// </summary>
    public static IServiceCollection AddVariableTypeAndAlias<T>(this IServiceCollection services, string alias, string category)
    {
        return services
            .AddVariableDescriptor<T>(category)
            .AddTypeAlias<T>(alias);
    }
}