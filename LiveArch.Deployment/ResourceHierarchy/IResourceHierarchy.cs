namespace LiveArch.Deployment.ResourceHierarchy
{
    public interface IResourceHierarchy
    {
        ResourceHierarchyRegistry StaticRegistry { get; }

        ResourceHierarchyRegistry GetDynamicRegistry(IReadOnlyCollection<Type> resourceTypes);
    }
}
