using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.Docker
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddDockerImageReferenceConfigurator<TConfigurator>(this IServiceCollection services)
            where TConfigurator : class, IDockerImageReferenceConfigurator
        {
            services.AddTransient<IDockerImageReferenceConfigurator, TConfigurator>();
            services.TryAddTransient<DockerImageReferenceConfigurator>();
            return services;
        }
    }
}
