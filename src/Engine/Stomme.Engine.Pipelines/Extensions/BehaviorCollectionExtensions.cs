using Stomme.Engine.Pipelines;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

public static class BehaviorCollectionExtensions
{
    extension(ICollection<IBehavior> behaviors)
    {
        public void Add<T>(IStep owner) where T : IBehavior => behaviors.Add((T)Activator.CreateInstance(typeof(T), owner)!);

        public void Remove<T>() where T : IBehavior
        {
            var behaviorsToRemove = behaviors.Where(x => x is T).ToList();

            foreach (var behavior in behaviorsToRemove) behaviors.Remove(behavior);
        }
    }
}