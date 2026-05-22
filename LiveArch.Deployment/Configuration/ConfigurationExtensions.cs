using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Configuration
{
    /// <summary>
    /// Registers core deployment services and the default processor implementation.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Registers the core default services required by the deployment engine.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddDefaultDeploymentServices(this IServiceCollection services)
        {
            return services
                .AddResourceTypes<Image>()
                .AddResourceTypes<ForEachLoop>()
                .AddDefaultTransformers()
                .AddDefaultValueConverters()
                .AddStructurizrDeploymentProcessor();
        }

        /// <summary>
        /// Registers the standard <see cref="StructurizrDeploymentProcessor"/> implementation and exposes it through <see cref="IStructurizrDeploymentProcessor"/>.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddStructurizrDeploymentProcessor(this IServiceCollection services)
        {
            services.TryAddSingleton<IStructurizrDeploymentObserver, NullStructurizrDeploymentObserver>();
            services.TryAddTransient<StructurizrDeploymentProcessor>();
            services.TryAddTransient<IStructurizrDeploymentProcessor>(serviceProvider => serviceProvider.GetRequiredService<StructurizrDeploymentProcessor>());
            return services;
        }
    }
}
