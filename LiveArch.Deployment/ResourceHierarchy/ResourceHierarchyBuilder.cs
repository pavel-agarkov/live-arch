namespace LiveArch.Deployment.ResourceHierarchy
{
    public class ResourceHierarchyBuilder : IResourceHierarchyBuilder
    {
        public ResourceHierarchyRegistry Registry { get; private set; }

        public ResourceHierarchyBuilder(IEnumerable<IResourceHierarchy> registries)
        {
            Registry = registries.Select(x => x.Registry).Aggregate((all, next) => new ResourceHierarchyRegistry(
                all.Concat(next).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value)));
        }
    }
}
