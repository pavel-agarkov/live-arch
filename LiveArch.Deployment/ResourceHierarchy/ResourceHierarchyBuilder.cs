using LiveArch.Deployment.ResourceTypes;

namespace LiveArch.Deployment.ResourceHierarchy
{
    /// <summary>
    /// Combines all registered hierarchy providers into one effective propagation registry.
    /// </summary>
    public class ResourceHierarchyBuilder : IResourceHierarchyBuilder
    {
        /// <summary>
        /// Gets the merged propagation registry.
        /// </summary>
        public ResourceHierarchyRegistry Registry { get; private set; }

        /// <summary>
        /// Initializes the builder from registered hierarchy providers and known resource types.
        /// </summary>
        /// <param name="registries">Registered hierarchy providers.</param>
        /// <param name="resourceTypesRegistry">Registry used to discover known resource types.</param>
        public ResourceHierarchyBuilder(IEnumerable<IResourceHierarchy> registries, ResourceTypesRegistry resourceTypesRegistry)
        {
            var resourceTypes = resourceTypesRegistry.GetAllResourceTypes();
            Registry = registries
                .SelectMany<IResourceHierarchy, ResourceHierarchyRegistry>(x => [x.StaticRegistry, x.GetDynamicRegistry(resourceTypes)])
                .Aggregate((all, next) => new ResourceHierarchyRegistry(all.Concat(next)
                .GroupBy(kv => kv.Key)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyCollection<ResourcePropagationRule>)[.. g.SelectMany(kv => kv.Value)])));
        }
    }
}
