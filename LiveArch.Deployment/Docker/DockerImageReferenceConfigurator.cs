using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Docker
{
    public class DockerImageReferenceConfigurator
    {
        private readonly IEnumerable<IDockerImageReferenceConfigurator> configurators;

        public DockerImageReferenceConfigurator(IEnumerable<IDockerImageReferenceConfigurator> configurators)
        {
            this.configurators = configurators;
        }

        public bool TryGetImageReference(object resource, Image dockerImage, out DockerImageReference? imageReference)
        {
            var configurator = configurators.FirstOrDefault(c => c.SupportsResourceType(resource));

            if (configurator != null)
            {
                imageReference = configurator.GetDockerImageReference(resource, dockerImage);
                return true;
            }

            imageReference = null;
            return false;
        }
    }
}
