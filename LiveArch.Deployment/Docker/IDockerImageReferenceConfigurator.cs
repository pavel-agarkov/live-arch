using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Docker
{
    /// <summary>
    /// Describes how a built Docker image should be assigned to a specific resource type.
    /// Implement this contract when another team wants custom resource-specific image wiring.
    /// </summary>
    public interface IDockerImageReferenceConfigurator
    {
        /// <summary>
        /// Determines whether this configurator supports the supplied resource argument object.
        /// </summary>
        /// <param name="resource">Target resource argument object.</param>
        /// <returns><c>true</c> when the configurator can map an image into the resource; otherwise <c>false</c>.</returns>
        bool SupportsResourceType(object resource);

        /// <summary>
        /// Builds the Docker image reference descriptor for the supplied resource.
        /// </summary>
        /// <param name="resource">Target resource argument object.</param>
        /// <param name="dockerImage">Built Docker image resource.</param>
        /// <returns>The descriptor that tells the engine where and how to assign the image reference.</returns>
        DockerImageReference GetDockerImageReference(object resource, Image dockerImage);
    }
}
