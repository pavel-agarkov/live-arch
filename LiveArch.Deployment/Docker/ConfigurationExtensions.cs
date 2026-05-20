using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.Docker
{
    /// <summary>
    /// Registers Docker image reference configurators used to bind built images into resource inputs.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds a Docker image reference configurator implementation.
        /// </summary>
        /// <typeparam name="TConfigurator">Configurator implementation type.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddDockerImageReferenceConfigurator<TConfigurator>(this IServiceCollection services)
            where TConfigurator : class, IDockerImageReferenceConfigurator
        {
            services.AddTransient<IDockerImageReferenceConfigurator, TConfigurator>();
            services.TryAddTransient<DockerImageReferenceConfigurator>();
            return services;
        }
    }
}
