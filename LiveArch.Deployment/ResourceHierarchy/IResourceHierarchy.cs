namespace LiveArch.Deployment.ResourceHierarchy
{
    /// <summary>
    /// Supplies parent-to-child propagation rules for a provider or domain.
    /// Implement this contract when another team wants custom hierarchy semantics.
    /// </summary>
    public interface IResourceHierarchy
    {
        /// <summary>
        /// Gets the provider's static propagation rules.
        /// </summary>
        ResourceHierarchyRegistry StaticRegistry { get; }

        /// <summary>
        /// Builds propagation rules that depend on the discovered resource type set.
        /// </summary>
        /// <param name="resourceTypes">Resource types currently known to the engine.</param>
        /// <returns>A registry containing dynamic propagation rules.</returns>
        ResourceHierarchyRegistry GetDynamicRegistry(IReadOnlyCollection<Type> resourceTypes);
    }
}
