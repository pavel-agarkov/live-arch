using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Azure.Docker;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using LiveArch.Deployment.Configuration;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.Observability;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Pulumi.AzureNative.Resources;
using Pulumi.DockerBuild;
using Pulumi.Testing;

namespace LiveArch.Deployment.TestRunner
{
    public abstract class DeploymentTestBase
    {
        protected readonly Mocks testMocks = new();
        protected readonly IReadOnlyDictionary<string, object> variables = new Dictionary<string, object>()
        {
            { "ENV", "prod" },
            { "LOCATION", "westeurope" },
            { "KEY_VAULT_NAME", "main_prod_kv" },
            { "RESOURCE_GROUP_NAME", "main_prod_rg" },
            { "APP_CONFIG_NAME", "main_prod_app_config" },
            { "TENANT_ID", "pavel.agarkov" },
            { "VNET_NAME", "main_prod_vnet" },
            { "SQL_SERVER_REGISTRATION_NAME", "main_prod_sql_reg" },
            { "SQL_SERVER_NAME", "main_prod_sql_server" },
            { "SQL_ELASTIC_POOL_NAME", "main_prod_sql_elastic_pool" },
        };

        protected readonly ResourceTypesRegistry resourceTypesRegistry;
        protected readonly DockerImageReferenceConfigurator dockerImageConfig;
        protected readonly ResourceHierarchyRegistry hierarchyRegistry;
        protected readonly IConversionEngine conversionEngine;
        protected readonly ITransformerRegistry transformerRegistry;

        protected DeploymentTestBase()
        {
            resourceTypesRegistry = new ResourceTypesRegistry(new[]
            {
                new ResourceTypesAssemblyMarker(typeof(Image)),
                new ResourceTypesAssemblyMarker(typeof(ResourceGroup)),
                new ResourceTypesAssemblyMarker(typeof(Controls.ForEachLoop)),
                new ResourceTypesAssemblyMarker(typeof(LiveArch.Resources.Azure.ServiceBus.ReadableSubscription)),
            });
            dockerImageConfig = new DockerImageReferenceConfigurator([
                new AzureDockerImageReferenceConfigurator()
            ]);
            hierarchyRegistry = new ResourceHierarchyBuilder([new AzureResourceHierarchy()], resourceTypesRegistry).Registry;

            var services = new ServiceCollection();
            services.AddDefaultTransformers();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            var serviceProvider = services.BuildServiceProvider();
            conversionEngine = serviceProvider.GetRequiredService<IConversionEngine>();
            transformerRegistry = serviceProvider.GetRequiredService<ITransformerRegistry>();
        }

        protected async Task<StructurizrDeploymentProcessor> ProcessDeployment(string deployment, IStructurizrDeploymentObserver? observer = null)
        {
            var ws = new StructurizrDeploymentProcessor(
                new TestDeploymentCommandOptions("prod", deployment, "workspace.json"),
                new TestDeploymentVariablesProvider(variables),
                new ResourceHierarchyBuilder([new AzureResourceHierarchy()], resourceTypesRegistry),
                resourceTypesRegistry,
                dockerImageConfig,
                conversionEngine,
                transformerRegistry,
                observer ?? new NoOpStructurizrDeploymentObserver());

            await Pulumi.Deployment.TestAsync(testMocks, new TestOptions { IsPreview = false }, async () =>
            {
                await ws.ProcessDeploymentAsync(default);
            });

            return ws;
        }
    }
}
