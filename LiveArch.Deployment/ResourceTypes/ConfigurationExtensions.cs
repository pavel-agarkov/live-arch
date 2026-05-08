using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.ResourceTypes
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddResourceTypes<TAssemblyMarker>(this IServiceCollection services) where TAssemblyMarker : class
        {
            services.TryAddSingleton<ResourceTypesRegistry>();
            services.AddSingleton(svc => new ResourceTypesAssemblyMarker(typeof(TAssemblyMarker)));
            return services;
        }
    }
}
