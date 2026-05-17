using LiveArch.Deployment.Azure.Docker;
using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using LiveArch.Deployment.Azure.ServiceBus;
using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pulumi.AzureNative.Resources;
using Pulumi.DockerBuild;

namespace LiveArch.Deployment.Runner
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var builder = Host.CreateApplicationBuilder(args);

                builder.Services
                    .AddResourceHierarchy<AzureResourceHierarchy>()
                    .AddResourceTypes<Image>()
                    .AddResourceTypes<ResourceGroup>()
                    .AddResourceTypes<ForEachLoop>()
                    .AddResourceTypes<ReadableSubscription>()
                    .AddDockerImageReferenceConfigurator<AzureDockerImageReferenceConfigurator>()
                    .AddDefaultTransformers()
                    .AddDefaultValueConverters()
                    .AddAzureValueConverters();

                builder.Services.AddSingleton(_ => DeploymentCommandOptions.FromConfiguration(builder.Configuration));
                builder.Services.AddSingleton<DeploymentVariablesProvider>();
                builder.Services.AddTransient<PulumiDeploymentRunner>();

                using var host = builder.Build();
                var runner = host.Services.GetRequiredService<PulumiDeploymentRunner>();

                return await runner.RunAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
