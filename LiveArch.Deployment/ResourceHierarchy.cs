namespace LiveArch.Deployment
{
    public static class ResourceHierarchy
    {
        public static ResourceHierarchyRegistry Registry = new()
        {
            new ResourcePropagationRules<Pulumi.AzureNative.Resources.ResourceGroup>
            {
                { rg => rg.Name, [ "resourceGroupName" ] },
                { rg => rg.Location, [ "location" ] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.Resources.GetResourceGroupResult>
            {
                { rg => rg.Name, [ "resourceGroupName" ] },
                { rg => rg.Location, [ "location" ] },
            },

            new ResourcePropagationRules<Pulumi.AzureNative.Web.AppServicePlan>
            {
                { plan => plan.Id, [ "serverFarmId" ] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.Web.GetAppServicePlanResult>
            {
                { plan => plan.Id, [ "serverFarmId" ] },
            },

            new ResourcePropagationRules<Pulumi.AzureNative.Network.VirtualNetwork>
            {
                { vnet => vnet.Name, [ "virtualNetworkName", "siteConfig.vnetName"] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.Network.GetVirtualNetworkResult>
            {
                { vnet => vnet.Name, [ "virtualNetworkName", "siteConfig.vnetName"] },
            },

            new ResourcePropagationRules<Pulumi.AzureNative.AzureData.SqlServerRegistration>
            {
                { reg => reg.Name, [ "sqlServerRegistrationName" ] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.AzureData.GetSqlServerRegistrationResult>
            {
                { reg => reg.Name, [ "sqlServerRegistrationName" ] },
            },

            new ResourcePropagationRules<Pulumi.AzureNative.AzureData.SqlServer>
            {
                { server => server.Name, [ "serverName" ] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.AzureData.GetSqlServerResult>
            {
                { server => server.Name, [ "serverName" ] },
            },

            new ResourcePropagationRules<Pulumi.AzureNative.Sql.ElasticPool>
            {
                { pool => pool.Id, [ "elasticPoolId" ] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.Sql.GetElasticPoolResult>
            {
                { pool => pool.Id, [ "elasticPoolId" ] },
            },

            new ResourcePropagationRules<Pulumi.AzureNative.ServiceBus.Namespace>
            {
                { ns => ns.Name, [ "namespaceName" ] }
            },
            new ResourcePropagationRules<Pulumi.AzureNative.ServiceBus.GetNamespaceResult>
            {
                { ns => ns.Name, [ "namespaceName" ] }
            }
        };
    }
}