using LiveArch.Deployment.ResourceHierarchy;
using System.Linq.Expressions;
using System.Reflection;

namespace LiveArch.Deployment.Azure.ResourceHierarchy
{
    public class AzureResourceHierarchy : IResourceHierarchy
    {
        public ResourceHierarchyRegistry StaticRegistry => new()
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
                { web => web.Identity.Apply(id => id == null ? null : id.PrincipalId), [ "principalId" ] },
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
                { ns => ns.PrincipalId, [ "principalId", "policy.objectId" ] }
            },
            new ResourcePropagationRules<Pulumi.AzureNative.ManagedIdentity.GetUserAssignedIdentityResult>
            {
                { ns => ns.PrincipalId, [ "principalId", "policy.objectId" ] }
            },

            new ResourcePropagationRules<Pulumi.AzureNative.KeyVault.Vault>
            {
                { ns => ns.Name, [ "vaultName" ] }
            },
            new ResourcePropagationRules<Pulumi.AzureNative.KeyVault.GetVaultResult>
            {
                { ns => ns.Name, [ "vaultName" ] }
            }
        };

        public ResourceHierarchyRegistry GetDynamicRegistry(IReadOnlyCollection<Type> resourceTypes)
        {
            return new ResourceHierarchyRegistry(resourceTypes
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

        private static Expression<Func<object, object>>? GetIdMemberAccessor(Type resourceType)
        {
            var resourceParameter = Expression.Parameter(typeof(object), "resource");
            var typedResource = Expression.Convert(resourceParameter, resourceType);
            var idProperty = resourceType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (idProperty is { CanRead: true } && idProperty.GetIndexParameters().Length == 0 && idProperty.GetMethod is { IsPublic: true, IsStatic: false })
            {
                return Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Property(typedResource, idProperty), typeof(object)), resourceParameter);
            }

            var idField = resourceType.GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (idField is { IsPublic: true, IsStatic: false })
            {
                return Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Field(typedResource, idField), typeof(object)), resourceParameter);
            }

            return null;
        }
    }
}