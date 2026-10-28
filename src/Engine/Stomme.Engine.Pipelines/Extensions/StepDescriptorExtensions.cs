using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extensions for <see cref="StepDescriptor"/>.
/// </summary>
public static class StepDescriptorExtensions
{
    extension(StepDescriptor stepDescriptor)
    {
        /// <summary>
        /// Returns the specified attribute from the step descriptor.
        /// </summary>
        public T? GetAttribute<T>() where T : Attribute
        {
            return (T?)stepDescriptor.Attributes.FirstOrDefault(x => x.GetType() == typeof(T));
        }

        /// <summary>
        /// Returns each wrapped input from the specified step.
        /// </summary>
        public IEnumerable<InputDescriptor> GetWrappedInputPropertyDescriptors(IStep step)
        {
            return stepDescriptor.Inputs.Where(x => x.IsWrapped);
        }

        /// <summary>
        /// Returns each naked input from the specified step.
        /// </summary>
        public IEnumerable<InputDescriptor> GetNakedInputPropertyDescriptors(IStep step)
        {
            return stepDescriptor.Inputs.Where(x => !x.IsWrapped);
        }

        /// <summary>
        /// Returns each input from the specified step.
        /// </summary>
        public IDictionary<string, Input?> GetWrappedInputProperties(IStep step)
        {
            var wrappedInputDescriptors = stepDescriptor.Inputs.Where(x => x.IsWrapped).ToList();
            var inputLookup = wrappedInputDescriptors.ToDictionary(x => x.Name, x => (Input?)x.ValueGetter(step));
            return inputLookup;
        }

        /// <summary>
        /// Returns each input descriptor from the specified step.
        /// </summary>
        public InputDescriptor? GetWrappedInputPropertyDescriptor(IStep step, string name)
        {
            return stepDescriptor.Inputs.FirstOrDefault(x => x.Name == name && x.IsWrapped);
        }

        /// <summary>
        /// Returns each input from the specified step.
        /// </summary>
        public Input? GetWrappedInputProperty(IStep step, string name)
        {
            var inputDescriptor = stepDescriptor.Inputs.FirstOrDefault(x => x.Name == name && x.IsWrapped);
            return inputDescriptor?.ValueGetter(step) as Input;
        }
    }
}