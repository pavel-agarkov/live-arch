using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Azure.Docker;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using LiveArch.Deployment.Azure.ServiceBus;
using LiveArch.Deployment.Configuration;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using Microsoft.Extensions.DependencyInjection;
using Pulumi.AzureNative.Resources;

namespace LiveArch.Deployment.Azure.Configuration
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddAzureDeploymentServices(this IServiceCollection services)
        {
            return services
                .AddDefaultDeploymentServices()
                .AddResourceHierarchy<AzureResourceHierarchy>()
                .AddResourceTypes<ResourceGroup>()
                .AddResourceTypes<ReadableSubscription>()
                .AddDockerImageReferenceConfigurator<AzureDockerImageReferenceConfigurator>()
                .AddAzureValueConverters();
        }
    }
}
