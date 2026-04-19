using Pulumi.Testing;

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
        public async Task ShouldCreateAllResources()
        {
            var ws = await ProcessTestWorkspace();

            ws.NewResources.Should().HaveCount(16);

            ws.OldResources.Should().HaveCount(18);
        }

        private async Task<StructurizrComponent> ProcessTestWorkspace()
        {
            var ws = new StructurizrComponent("workspace.json", "prod", variables);

            await Pulumi.Deployment.TestAsync(testMocks, new TestOptions { IsPreview = false }, async () =>
            {
                await ws.ProcessWorkspaceAsync(default);
            });

            return ws;
        }
    }
}
