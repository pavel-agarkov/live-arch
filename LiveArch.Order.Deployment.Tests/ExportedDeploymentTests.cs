using LiveArch.Deployment.Export.Testing;
using Xunit;

namespace LiveArch.Order.Deployment.Tests;

public class ExportedDeploymentTests
{
    [Fact]
    public async Task ProcessAsync_Should_Create_Resources()
    {
        var mocks = await ExportedDeploymentTestHost.ExecuteAsync(() => global::LiveArch.Order.Deployment.ExportedDeployment.ProcessAsync(CreateVariables()));

        Assert.NotEmpty(mocks.Resources);
    }

    private static global::LiveArch.Order.Deployment.LiveArchOrderDeploymentVariables CreateVariables() => new()
    {
        AppConfigName = "main_prod_app_config",
        Env = "prod",
        KeyVaultName = "main_prod_kv",
        Location = "westeurope",
        ResourceGroupName = "main_prod_rg",
        SqlElasticPoolName = "main_prod_sql_elastic_pool",
        SqlServerName = "main_prod_sql_server",
        SqlServerRegistrationName = "main_prod_sql_reg",
        TenantId = "pavel.agarkov",
        VnetName = "main_prod_vnet",
    };
}
