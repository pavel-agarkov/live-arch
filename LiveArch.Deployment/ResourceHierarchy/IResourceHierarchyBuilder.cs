namespace LiveArch.Deployment.ResourceHierarchy
{
    /// <summary>
    /// Combines registered resource hierarchy providers into a single propagation registry.
    /// </summary>
    public interface IResourceHierarchyBuilder
    {
        /// <summary>
        /// Gets the combined propagation registry.
        /// </summary>
        ResourceHierarchyRegistry Registry { get; }
    }
}