using Pulumi.AzureNative.AzureData;
using Pulumi.AzureNative.Network;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Web;

namespace LiveArch.Deployment
{
    public static class ResourceHierarchy
    {
        public static ResourceHierarchyRegistry Registry = new()
        {
            new ResourcePropagationRules<ResourceGroup>
            {
                { rg => rg.Name, [ "resourceGroupName" ] },
                { rg => rg.Location, [ "location" ] },
            },
            new ResourcePropagationRules<GetResourceGroupResult>
            {
                { rg => rg.Name, [ "resourceGroupName" ] },
                { rg => rg.Location, [ "location" ] },
            },

            new ResourcePropagationRules<AppServicePlan>
            {
                { plan => plan.Id, [ "serverFarmId" ] },
            },
            new ResourcePropagationRules<GetAppServicePlanResult>
            {
                { plan => plan.Id, [ "serverFarmId" ] },
            },

            new ResourcePropagationRules<VirtualNetwork>
            {
                { vnet => vnet.Name, [ "virtualNetworkName", "siteConfig.vnetName"] },
            },
            new ResourcePropagationRules<GetVirtualNetworkResult>
            {
                { vnet => vnet.Name, [ "virtualNetworkName", "siteConfig.vnetName"] },
            },

            new ResourcePropagationRules<SqlServerRegistration>
            {
                { reg => reg.Name, [ "sqlServerRegistrationName" ] },
            },
            new ResourcePropagationRules<GetSqlServerRegistrationResult>
            {
                { reg => reg.Name, [ "sqlServerRegistrationName" ] },
            },

            new ResourcePropagationRules<SqlServer>
            {
                { server => server.Name, [ "serverName" ] },
            },
            new ResourcePropagationRules<GetSqlServerResult>
            {
                { server => server.Name, [ "serverName" ] },
            },

            new ResourcePropagationRules<ElasticPool>
            {
                { pool => pool.Id, [ "elasticPoolId" ] },
            },
            new ResourcePropagationRules<GetElasticPoolResult>
            {
                { pool => pool.Id, [ "elasticPoolId" ] },
            }
        };
    }
}