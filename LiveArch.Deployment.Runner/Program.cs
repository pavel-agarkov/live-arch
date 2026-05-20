using LiveArch.Deployment.Azure.Configuration;
using LiveArch.Deployment.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
                    .AddAzureDeploymentServices();

                builder.Services.AddSingleton(_ => DeploymentCommandOptions.FromConfiguration(builder.Configuration));
                builder.Services.AddSingleton<IDeploymentCommandOptions>(sp => sp.GetRequiredService<DeploymentCommandOptions>());
                builder.Services.AddSingleton<DeploymentVariablesProvider>();
                builder.Services.AddSingleton<IDeploymentVariablesProvider>(sp => sp.GetRequiredService<DeploymentVariablesProvider>());
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
