using System.Reflection;

namespace Stomme.Engine.Pipelines;

public interface IStepPropertyDefaultValueProvider
{
    object GetDefaultValue(PropertyInfo property);
}