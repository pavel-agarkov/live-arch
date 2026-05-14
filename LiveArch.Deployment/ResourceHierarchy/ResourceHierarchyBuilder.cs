using LiveArch.Deployment.ResourceTypes;

namespace LiveArch.Deployment.ResourceHierarchy
{
    public class ResourceHierarchyBuilder : IResourceHierarchyBuilder
    {
        public ResourceHierarchyRegistry Registry { get; private set; }

        public ResourceHierarchyBuilder(IEnumerable<IResourceHierarchy> registries, ResourceTypesRegistry resourceTypesRegistry)
        {
            var resourceTypes = resourceTypesRegistry.GetAllResourceTypes();
            Registry = registries
                .SelectMany<IResourceHierarchy, ResourceHierarchyRegistry>(x => [x.Registry, x.GetDynamicRegistry(resourceTypes)])
                .Aggregate((all, next) => new ResourceHierarchyRegistry(all.Concat(next).GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.Last().Value)));
        }
    }
}
