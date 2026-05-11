namespace LiveArch.Deployment.ResourceTypes
{
    public class ResourceTypesAssemblyMarker
    {
        public Type AssemblyMarker { get; }

        public ResourceTypesAssemblyMarker(Type assemblyMarker)
        {
            AssemblyMarker = assemblyMarker;
        }
    }
}
