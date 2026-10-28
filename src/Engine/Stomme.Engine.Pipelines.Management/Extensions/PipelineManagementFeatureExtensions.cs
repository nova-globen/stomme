using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Management.Features;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management;

[PublicAPI]
public static class PipelineManagementFeatureExtensions
{
    public static PipelineManagementFeature AddVariableTypeAndAlias<T>(this PipelineManagementFeature management, string category)
    {
        return management.AddVariableTypeAndAlias<T>(typeof(T).Name, category);
    }
    
    public static PipelineManagementFeature AddVariableTypeAndAlias<T>(this PipelineManagementFeature management, string alias, string category)
    {
        management.AddVariableType<T>(category);
        management.Module.AddTypeAlias<T>(alias);
        return management;
    }
}