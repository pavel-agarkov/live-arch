using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.ResourceHierarchy
{
    /// <summary>
    /// Registers resource hierarchy providers and the builder that combines them.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds a resource hierarchy implementation that contributes parent-to-child propagation rules.
        /// </summary>
        /// <typeparam name="TRegistry">Hierarchy implementation type.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddResourceHierarchy<TRegistry>(this IServiceCollection services) where TRegistry : class, IResourceHierarchy
        {
            services.TryAddTransient<IResourceHierarchyBuilder, ResourceHierarchyBuilder>();
            services.TryAddTransient<IResourceHierarchy, TRegistry>();
            return services;
        }
    }
}
