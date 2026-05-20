using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Docker
{
    /// <summary>
    /// Chooses the appropriate Docker image reference mapping strategy for a resource.
    /// </summary>
    public class DockerImageReferenceConfigurator
    {
        private readonly IEnumerable<IDockerImageReferenceConfigurator> configurators;

        /// <summary>
        /// Initializes the configurator with all registered resource-specific mapping strategies.
        /// </summary>
        /// <param name="configurators">Resource-specific Docker image configurators.</param>
        public DockerImageReferenceConfigurator(IEnumerable<IDockerImageReferenceConfigurator> configurators)
        {
            this.configurators = configurators;
        }

        /// <summary>
        /// Tries to build the Docker image reference assignment for a resource.
        /// </summary>
        /// <param name="resource">Target resource argument object.</param>
        /// <param name="dockerImage">Built Docker image resource.</param>
        /// <param name="imageReference">Resolved image reference descriptor when a configurator supports the resource.</param>
        /// <returns><c>true</c> when a configurator was able to map the image to the resource; otherwise <c>false</c>.</returns>
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
