using Pulumi.AzureNative.DevCenter;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
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

        [Fact]
        public async Task ShouldCreateAllResourcesForOrderService()
        {
            var ws = await ProcessDeployment("order-env");

            ws.NewResources.Should().HaveCount(16);

            ws.OldResources.Should().HaveCount(18);
        }

        [Fact]
        public async Task ShouldCreateAllResourcesForDeliveryService()
        {
            var ws = await ProcessDeployment("delivery-env");

            ws.NewResources.Should().HaveCount(10);

            ws.OldResources.Should().HaveCount(13);
        }

        [Fact]
        public async Task ShouldCreateAllSharedResources()
        {
            var ws = await ProcessDeployment("shared-env");

            ws.NewResources.Should().HaveCount(16);

            ws.OldResources.Should().HaveCount(18);
        }

        private async Task<StructurizrComponent> ProcessDeployment(string deployment)
        {
            var ws = new StructurizrComponent("workspace.json", "prod", deployment, variables);

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
        }
    }
}
