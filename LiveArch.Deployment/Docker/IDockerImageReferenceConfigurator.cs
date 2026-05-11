using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Docker
{
    public interface IDockerImageReferenceConfigurator
    {
        bool SupportsResourceType(object resource);

        DockerImageReference GetDockerImageReference(object resource, Image dockerImage);
    }
}
