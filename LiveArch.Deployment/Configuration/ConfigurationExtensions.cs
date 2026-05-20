using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Configuration
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddDefaultDeploymentServices(this IServiceCollection services)
        {
            return services
                .AddResourceTypes<Image>()
                .AddResourceTypes<ForEachLoop>()
                .AddDefaultTransformers()
                .AddDefaultValueConverters()
                .AddStructurizrDeploymentProcessor();
        }

        public static IServiceCollection AddStructurizrDeploymentProcessor(this IServiceCollection services)
        {
            services.TryAddTransient<StructurizrDeploymentProcessor>();
            services.TryAddTransient<IStructurizrDeploymentProcessor>(serviceProvider => serviceProvider.GetRequiredService<StructurizrDeploymentProcessor>());
            return services;
        }
    }
}
