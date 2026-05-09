using LiveArch.Deployment.Azure.Docker;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using LiveArch.Deployment.Azure.ServiceBus;
using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.DockerBuild;
using Pulumi.Testing;
using ManagedServiceIdentityType = Pulumi.AzureNative.Web.ManagedServiceIdentityType;

namespace LiveArch.Deployment.TestRunner
{
    public class DeploymentTests
    {
        private readonly Mocks testMocks = new();
        private readonly IReadOnlyDictionary<string, object> variables = new Dictionary<string, object>()
        {
            { "ENV", "prod" },
            { "KEY_VAULT_NAME", "main_prod_kv" },
            { "RESOURCE_GROUP_NAME", "main_prod_rg" },
            { "APP_CONFIG_NAME", "main_prod_app_config" },
            { "TENANT_ID", "pavel.agarkov" },
            { "VNET_NAME", "main_prod_vnet" },
            { "SQL_SERVER_REGISTRATION_NAME", "main_prod_sql_reg" },
            { "SQL_SERVER_NAME", "main_prod_sql_server" },
            { "SQL_ELASTIC_POOL_NAME", "main_prod_sql_elastic_pool" },
        };
        private readonly ResourceTypesRegistry resourceTypesRegistry;
        private readonly DockerImageReferenceConfigurator dockerImageConfig;
        private readonly ResourceHierarchyRegistry hierarchyRegistry;

        public DeploymentTests()
        {
            hierarchyRegistry = new ResourceHierarchyBuilder([new AzureResourceHierarchy()]).Registry;
            resourceTypesRegistry = new ResourceTypesRegistry(new[]
            {
                new ResourceTypesAssemblyMarker(typeof(Image)),
                new ResourceTypesAssemblyMarker(typeof(ResourceGroup)),
                new ResourceTypesAssemblyMarker(typeof(ForEachLoop)),
                new ResourceTypesAssemblyMarker(typeof(ReadableSubscription)),
            });
            dockerImageConfig = new DockerImageReferenceConfigurator([
                new AzureDockerImageReferenceConfigurator()
                ]);
        }

        [Fact]
        public async Task ShouldCreateAllResourcesForOrderService()
        {
            var ws = await ProcessDeployment("order-env");

            ws.NewResources.Should().HaveCount(20);

            ws.OldResources.Should().HaveCount(18);
        }

        [Fact]
        public async Task ShouldCreateAllResourcesForDeliveryService()
        {
            var ws = await ProcessDeployment("delivery-env");

            ws.NewResources.Should().HaveCount(16);

            ws.OldResources.Should().HaveCount(20);
        }

        [Fact]
        public async Task ShouldCreateAllSharedResources()
        {
            var ws = await ProcessDeployment("shared-env");

            ws.NewResources.Should().HaveCount(20);

            ws.OldResources.Should().HaveCount(18);
        }

        private async Task<StructurizrComponent> ProcessDeployment(string deployment)
        {
            var ws = new StructurizrComponent("workspace.json", "prod", deployment, variables,
                hierarchyRegistry, resourceTypesRegistry, dockerImageConfig);

            await Pulumi.Deployment.TestAsync(testMocks, new TestOptions { IsPreview = false }, async () =>
            {
                await ws.ProcessWorkspaceAsync(default);
            });

            return ws;
        }

        public static void TestCases()
        {
            var app = new WebApp("demo-app", new WebAppArgs
            {
                //ResourceGroupName = rg.Name,
                //ServerFarmId = plan.Id,
                Kind = "app,linux",
                Identity = new ManagedServiceIdentityArgs
                {
                    Type = ManagedServiceIdentityType.UserAssigned,
                    //UserAssignedIdentities
                },
                SiteConfig = new SiteConfigArgs
                {
                    LinuxFxVersion = "DOCKER|demoacr.azurecr.io/demoapi:latest",
                    AppSettings =
                    {
                        new NameValuePairArgs { Name = "WEBSITES_PORT", Value = "8080" },
                        //new NameValuePairArgs { Name = "DOCKER_REGISTRY_SERVER_URL", Value = acr.LoginServer },
                        //new NameValuePairArgs { Name = "DOCKER_REGISTRY_SERVER_USERNAME", Value = username },
                        //new NameValuePairArgs { Name = "DOCKER_REGISTRY_SERVER_PASSWORD", Value = password },
                    },
                    Cors = new CorsSettingsArgs
                    {
                        //AllowedOrigins = 
                    }
                }
            });

            //new Pulumi.AzureNative.ServiceBus.GetTopic().Invoke()

            var topic = new Pulumi.AzureNative.ServiceBus.Topic("", new Pulumi.AzureNative.ServiceBus.TopicArgs
            {
                TopicName = ""
            });

            var subs = new Pulumi.AzureNative.ServiceBus.Subscription("", new Pulumi.AzureNative.ServiceBus.SubscriptionArgs
            {
                NamespaceName = "",
                TopicName = topic.Name
            });

            var ra = new RoleAssignment("", new RoleAssignmentArgs
            {
                PrincipalId = app.Identity.Apply(x => x.PrincipalId)
            });
        }
    }
}
