using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.ResourceHierarchy
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddResourceHierarchy<TRegistry>(this IServiceCollection services) where TRegistry : class, IResourceHierarchy
        {
            services.TryAddTransient<IResourceHierarchyBuilder, ResourceHierarchyBuilder>();
            services.TryAddTransient<IResourceHierarchy, TRegistry>();
            return services;
        }
    }
}
