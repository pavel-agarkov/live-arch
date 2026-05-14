namespace LiveArch.Deployment.ResourceHierarchy
{
    public interface IResourceHierarchy
    {
        ResourceHierarchyRegistry Registry { get; }

        ResourceHierarchyRegistry GetDynamicRegistry(IReadOnlyCollection<Type> resourceTypes);
    }
}
