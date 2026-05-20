using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.ResourceTypes
{
    /// <summary>
    /// Registers assemblies whose Pulumi resource types and invoke methods should be discoverable by the deployment engine.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds a marker type whose assembly should be scanned for resource and invoke metadata.
        /// </summary>
        /// <typeparam name="TAssemblyMarker">Any type from the assembly that should be scanned.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddResourceTypes<TAssemblyMarker>(this IServiceCollection services) where TAssemblyMarker : class
        {
            services.TryAddSingleton<ResourceTypesRegistry>();
            services.AddSingleton(svc => new ResourceTypesAssemblyMarker(typeof(TAssemblyMarker)));
            return services;
        }
    }
}
