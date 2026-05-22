using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Azure.Docker;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using LiveArch.Deployment.Configuration;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Resources.Azure.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Pulumi.AzureNative.Resources;

namespace LiveArch.Deployment.Azure.Configuration
{
    /// <summary>
    /// Registers the default Azure-specific deployment services on top of the core deployment services.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds the standard Azure resource hierarchy, resource types, Docker image integration, and Azure-specific converters.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
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
