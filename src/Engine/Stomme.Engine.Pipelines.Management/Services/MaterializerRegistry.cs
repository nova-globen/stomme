namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class MaterializerRegistry(Func<IEnumerable<IPipelineMaterializer>> materializers) : IMaterializerRegistry
{
    private readonly Lazy<IReadOnlyCollection<IPipelineMaterializer>> _materializers = new(() => materializers().ToArray());

    /// <inheritdoc />
    public IEnumerable<IPipelineMaterializer> GetMaterializers() => _materializers.Value;

    /// <inheritdoc />
    public IPipelineMaterializer? GetMaterializer(string name)
    {
        return _materializers.Value.FirstOrDefault(x => x.Name == name);
    }

    /// <inheritdoc />
    public bool IsMaterializerAvailable(string name)
    {
        return _materializers.Value.Any(x => x.Name == name);
    }
}
