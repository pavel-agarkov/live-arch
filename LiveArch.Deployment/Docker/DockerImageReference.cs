using Pulumi;

namespace LiveArch.Deployment.Docker
{
    public class DockerImageReference
    {
        /// <summary>
        /// Property path on the recource to set <see cref="ImageRef"/> value to.
        /// </summary>
        /// <example>siteConfig.linuxFxVersion</example>
        public required string ResourceImagePropertyPath { get; set; }

        /// <summary>
        /// Docker image reference in the format required for this resource
        /// </summary>
        /// <example>DOCKER|myregistry.azurecr.io/myimage:tag</example>
        public required Output<string> ImageRef { get; set; }
    }
}
