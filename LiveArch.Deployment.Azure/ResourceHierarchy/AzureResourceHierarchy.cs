using LiveArch.Deployment.ResourceHierarchy;
using System.Reflection;

namespace LiveArch.Deployment.Azure.ResourceHierarchy
{
    public class AzureResourceHierarchy : IResourceHierarchy
    {
        public ResourceHierarchyRegistry Registry => new()
        {
            new ResourcePropagationRules<Pulumi.AzureNative.Resources.ResourceGroup>
            {
                { rg => rg.Name, [ "resourceGroupName", "sub.resourceGroupName" ] },
                { rg => rg.Location, [ "location" ] },
            },
            new ResourcePropagationRules<Pulumi.AzureNative.Resources.GetResourceGroupResult>
            {
                { rg => rg.Name, [ "resourceGroupName", "sub.resourceGroupName" ] },
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

            new ResourcePropagationRules<Pulumi.AzureNative.Web.WebApp>
            {
                { web => web.Identity.Apply(id => id?.PrincipalId), [ "principalId" ] },
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
                { ns => ns.Name, [ "namespaceName", "sub.namespaceName" ] }
            },
            new ResourcePropagationRules<Pulumi.AzureNative.ServiceBus.GetNamespaceResult>
            {
                { ns => ns.Name, [ "namespaceName", "sub.namespaceName" ] }
            },

            new ResourcePropagationRules<Pulumi.AzureNative.ServiceBus.Topic>
            {
                { ns => ns.Name, [ "topicName", "sub.topicName" ] }
            },
            new ResourcePropagationRules<Pulumi.AzureNative.ServiceBus.GetTopicResult>
            {
                { ns => ns.Name, [ "topicName", "sub.topicName" ] }
            },

            new ResourcePropagationRules<Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity>
            {
                { ns => ns.PrincipalId, [ "principalId" ] }
            },
            new ResourcePropagationRules<Pulumi.AzureNative.ManagedIdentity.GetUserAssignedIdentityResult>
            {
                { ns => ns.PrincipalId, [ "principalId" ] }
            },

            //new ResourcePropagationRules<Pulumi.AzureNative.Storage.StorageAccount>
            //{
            //    { ns => ns.Id, [ "scope" ] }
            //},
            //new ResourcePropagationRules<Pulumi.AzureNative.Storage.GetStorageAccountResult>
            //{
            //    { ns => ns.Id, [ "scope" ] }
            //}

        };

        public ResourceHierarchyRegistry GetDynamicRegistry(IReadOnlyCollection<Type> resourceTypes)
        {
            return new ResourceHierarchyRegistry(
                resourceTypes
                    .Distinct()
                    .Select(CreateScopePropagationRule)
                    .Where(rule => rule is not null)
                    .Select(rule => rule!.Value));
        }

        private static KeyValuePair<Type, IReadOnlyCollection<ResourcePropagationRule>>? CreateScopePropagationRule(Type resourceType)
        {
            var idMemberAccessor = GetIdMemberAccessor(resourceType);
            if (idMemberAccessor == null)
            {
                return null;
            }

            return new KeyValuePair<Type, IReadOnlyCollection<ResourcePropagationRule>>(
                resourceType,
                [
                    new ResourcePropagationRule
                    {
                        ParentOutputProperty = idMemberAccessor,
                        TargetInputProperties = [ "scope" ]
                    }
                ]);
        }

        private static Func<object, object>? GetIdMemberAccessor(Type resourceType)
        {
            var idProperty = resourceType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (idProperty is { CanRead: true } && idProperty.GetIndexParameters().Length == 0 && idProperty.GetMethod is { IsPublic: true, IsStatic: false })
            {
                return resource => idProperty.GetValue(resource)!;
            }

            var idField = resourceType.GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (idField is { IsPublic: true, IsStatic: false })
            {
                return resource => idField.GetValue(resource)!;
            }

            return null;
        }
    }
}