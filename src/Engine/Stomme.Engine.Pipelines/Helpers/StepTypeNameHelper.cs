using System.Reflection;
using Stomme.Engine.Pipelines.Attributes;

namespace Stomme.Engine.Pipelines.Helpers;

public static class StepTypeNameHelper
{
    public static string? GenerateNamespace(Type stepType)
    {
        var stepAttr = stepType.GetCustomAttribute<StepAttribute>();
        return stepAttr?.Namespace ?? stepType.Namespace;
    }

    public static string GenerateTypeName(Type type, string? ns)
    {
        var stepAttr = type.GetCustomAttribute<StepAttribute>();
        string typeName;
        
        if (type.IsGenericType)
        {
            var sanitizedTypeName = stepAttr?.Type ?? type.Name.Substring(0, type.Name.IndexOf("`", StringComparison.InvariantCulture));
            var genericArgs = type.GenericTypeArguments.Select(type1 => GenerateTypeName(type1, null));
            typeName = $"{sanitizedTypeName}<{string.Join(',', genericArgs)}>";
        }
        else
            typeName = stepAttr?.Type ?? type.Name;

        return ns != null ? $"{ns}.{typeName}" : typeName;
    }

    public static string GenerateTypeName<T>() where T : IStep => GenerateTypeName(typeof(T));

    public static string GenerateTypeName(Type type)
    {
        var ns = GenerateNamespace(type);
        return GenerateTypeName(type, ns);
    }

    public static string? GetCategoryFromNamespace(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return null;

        var index = ns.LastIndexOf('.');

        return index < 0 ? ns : ns[(index + 1)..];
    }
}