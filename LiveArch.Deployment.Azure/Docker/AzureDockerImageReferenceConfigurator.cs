using LiveArch.Deployment.Docker;
using Pulumi;
using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Azure.Docker
{
    public class AzureDockerImageReferenceConfigurator : IDockerImageReferenceConfigurator
    {
        public DockerImageReference GetDockerImageReference(object resource, Image dockerImage)
        {
            return resource switch
            {
                Pulumi.AzureNative.Web.WebAppArgs => new DockerImageReference
                {
                    ResourceImagePropertyPath = "siteConfig.linuxFxVersion",
                    ImageRef = Output.Format($"DOCKER|{dockerImage.Ref}")
                },
                Pulumi.AzureNative.App.ContainerAppArgs => new DockerImageReference
                {
                    ResourceImagePropertyPath = "properties.template.containers[0].image",
                    ImageRef = dockerImage.Ref
                },
                _ => throw new NotSupportedException($"Resource type '{resource.GetType().Name}' is not supported."),
            };
        }

        public bool SupportsResourceType(object resource)
        {
            return resource switch
            {
                Pulumi.AzureNative.Web.WebAppArgs => true,
                Pulumi.AzureNative.App.ContainerAppArgs => true,
                _ => false,
            };
        }
    }
}
