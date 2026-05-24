using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace LiveArch.Order.Deployment;

public sealed class LiveArchOrderDeploymentVariables
{
    public required global::System.String AppConfigName { get; init; }
    public required global::System.String Env { get; init; }
    public required global::System.String KeyVaultName { get; init; }
    public required global::System.String Location { get; init; }
    public required global::System.String ResourceGroupName { get; init; }
    public required global::System.String SqlElasticPoolName { get; init; }
    public required global::System.String SqlServerName { get; init; }
    public required global::System.String SqlServerRegistrationName { get; init; }
    public required global::System.String TenantId { get; init; }
    public required global::System.String VnetName { get; init; }
}

public static class ExportedDeployment
{
    public const string DeploymentName = "order-env";

    public static Task ProcessAsync(LiveArchOrderDeploymentVariables variables, CancellationToken cancellationToken = default)
    {
        var sharedRgReference = global::Pulumi.AzureNative.Resources.GetResourceGroup.Invoke(new global::Pulumi.AzureNative.Resources.GetResourceGroupInvokeArgs()
        {
            ResourceGroupName = variables.ResourceGroupName
        });
        var serviceBusNamespace = global::Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke(new global::Pulumi.AzureNative.ServiceBus.GetNamespaceInvokeArgs()
        {
            NamespaceName = $"{variables.Env}-sbns",
            ResourceGroupName = default!
        });
        var orderEventsTopic = global::Pulumi.AzureNative.ServiceBus.GetTopic.Invoke(new global::Pulumi.AzureNative.ServiceBus.GetTopicInvokeArgs()
        {
            NamespaceName = default!,
            ResourceGroupName = default!,
            TopicName = $"{variables.Env}-order-events-topic"
        });
        var deliveryEventsTopic = global::Pulumi.AzureNative.ServiceBus.GetTopic.Invoke(new global::Pulumi.AzureNative.ServiceBus.GetTopicInvokeArgs()
        {
            NamespaceName = default!,
            ResourceGroupName = default!,
            TopicName = $"{variables.Env}-delivery-events-topic"
        });
        var orderRg = global::Pulumi.AzureNative.Resources.GetResourceGroup.Invoke(new global::Pulumi.AzureNative.Resources.GetResourceGroupInvokeArgs()
        {
            ResourceGroupName = variables.ResourceGroupName
        });
        var prodKeyVault = global::Pulumi.AzureNative.KeyVault.GetVault.Invoke(new global::Pulumi.AzureNative.KeyVault.GetVaultInvokeArgs()
        {
            ResourceGroupName = default!,
            VaultName = variables.KeyVaultName
        });
        var orderServiceMi = new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity("order-service-mi", new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentityArgs()
        {
            Location = default!,
            ResourceGroupName = default!,
            ResourceName = $"{variables.Env}-order-service-mi"
        }, null);
        var saList = global::Pulumi.AzureNative.AppConfiguration.GetKeyValue.Invoke(new global::Pulumi.AzureNative.AppConfiguration.GetKeyValueInvokeArgs()
        {
            ConfigStoreName = variables.AppConfigName,
            KeyValueName = "storageAccounts",
            ResourceGroupName = default!
        });
        var orderServiceKvAccessPolicy = new global::Pulumi.AzureNative.KeyVault.AccessPolicy("order-service-kv-access-policy", new global::Pulumi.AzureNative.KeyVault.AccessPolicyArgs()
        {
            Policy = new global::Pulumi.AzureNative.KeyVault.Inputs.AccessPolicyEntryArgs()
            {
                ObjectId = default! /* orderServiceMi.principalId */,
                Permissions = new global::Pulumi.AzureNative.KeyVault.Inputs.PermissionsArgs(),
                TenantId = variables.TenantId
            },
            ResourceGroupName = default!,
            VaultName = default! /* prodKeyVault.name */
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var sQLServerRegistration = global::Pulumi.AzureNative.AzureData.GetSqlServerRegistration.Invoke(new global::Pulumi.AzureNative.AzureData.GetSqlServerRegistrationInvokeArgs()
        {
            ResourceGroupName = default!,
            SqlServerRegistrationName = variables.SqlServerRegistrationName
        });
        var sQLServer = global::Pulumi.AzureNative.AzureData.GetSqlServer.Invoke(new global::Pulumi.AzureNative.AzureData.GetSqlServerInvokeArgs()
        {
            ResourceGroupName = default!,
            SqlServerName = variables.SqlServerName,
            SqlServerRegistrationName = default!
        });
        var elasticPool = global::Pulumi.AzureNative.Sql.GetElasticPool.Invoke(new global::Pulumi.AzureNative.Sql.GetElasticPoolInvokeArgs()
        {
            ElasticPoolName = variables.SqlElasticPoolName,
            ResourceGroupName = default!,
            ServerName = default!
        });
        var orderDb = new global::Pulumi.AzureNative.Sql.Database("order-db", new global::Pulumi.AzureNative.Sql.DatabaseArgs()
        {
            DatabaseName = $"{variables.Env}-order-db",
            ElasticPoolId = default!,
            Location = default!,
            ResourceGroupName = default!,
            ServerName = default!
        }, null);
        var virtualNetwork = global::Pulumi.AzureNative.Network.GetVirtualNetwork.Invoke(new global::Pulumi.AzureNative.Network.GetVirtualNetworkInvokeArgs()
        {
            ResourceGroupName = default!,
            VirtualNetworkName = variables.VnetName
        });
        var prodAppServicePlan = global::Pulumi.AzureNative.Web.GetAppServicePlan.Invoke(new global::Pulumi.AzureNative.Web.GetAppServicePlanInvokeArgs()
        {
            Name = $"{variables.Env}-app-service-plan",
            ResourceGroupName = default!
        });
        var orderApi = new global::Pulumi.DockerBuild.Image("orderApi", new global::Pulumi.DockerBuild.ImageArgs()
        {
            Context = new global::Pulumi.DockerBuild.Inputs.BuildContextArgs()
            {
                Location = "../LiveArch.Order.Api/"
            },
            Dockerfile = new global::Pulumi.DockerBuild.Inputs.DockerfileArgs()
            {
                Location = "../.Dockerfile"
            },
            Push = true
        }, null);
        var orderApiWebApp = new global::Pulumi.AzureNative.Web.WebApp("order-api", new global::Pulumi.AzureNative.Web.WebAppArgs()
        {
            Identity = new global::Pulumi.AzureNative.Web.Inputs.ManagedServiceIdentityArgs()
            {
                Type = global::Pulumi.AzureNative.Web.ManagedServiceIdentityType.UserAssigned
            },
            Location = default!,
            Name = $"{variables.Env}-order-api",
            ResourceGroupName = default!,
            ServerFarmId = default!,
            SiteConfig = new global::Pulumi.AzureNative.Web.Inputs.SiteConfigArgs()
            {
                Cors = new global::Pulumi.AzureNative.Web.Inputs.CorsSettingsArgs(),
                LinuxFxVersion = default!,
                VnetName = default!
            }
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var orderWorker = new global::Pulumi.DockerBuild.Image("orderWorker", new global::Pulumi.DockerBuild.ImageArgs()
        {
            Context = new global::Pulumi.DockerBuild.Inputs.BuildContextArgs()
            {
                Location = "../LiveArch.Order.Worker/"
            },
            Dockerfile = new global::Pulumi.DockerBuild.Inputs.DockerfileArgs()
            {
                Location = "../.Dockerfile"
            },
            Push = true
        }, null);
        var orderWorkerWebApp = new global::Pulumi.AzureNative.Web.WebApp("order-worker", new global::Pulumi.AzureNative.Web.WebAppArgs()
        {
            Identity = new global::Pulumi.AzureNative.Web.Inputs.ManagedServiceIdentityArgs()
            {
                Type = global::Pulumi.AzureNative.Web.ManagedServiceIdentityType.UserAssigned
            },
            Location = default!,
            Name = $"{variables.Env}-order-worker",
            ResourceGroupName = default!,
            ServerFarmId = default!,
            SiteConfig = new global::Pulumi.AzureNative.Web.Inputs.SiteConfigArgs()
            {
                LinuxFxVersion = default!,
                VnetName = default!
            }
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderDb, orderServiceMi } });
        var orderWorkerSubscriptionToDeliveryEventsTopic = new global::LiveArch.Resources.Azure.ServiceBus.ReadableSubscription("order-worker-subscription-to-delivery-events-topic", new global::LiveArch.Resources.Azure.ServiceBus.ReadableSubscriptionArgs()
        {
            SubscriptionArgs = new global::Pulumi.AzureNative.ServiceBus.SubscriptionArgs()
            {
                NamespaceName = default!,
                ResourceGroupName = default!,
                TopicName = default!
            },
            PrincipalId = default!
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderWorkerWebApp } });
        var storageAccount = global::Pulumi.AzureNative.Storage.GetStorageAccount.Invoke(new global::Pulumi.AzureNative.Storage.GetStorageAccountInvokeArgs()
        {
            AccountName = "${saName}",
            ResourceGroupName = default!
        });
        var orderServiceSa1Contributor = new global::Pulumi.AzureNative.Authorization.RoleAssignment("order-service-sa1-contributor", new global::Pulumi.AzureNative.Authorization.RoleAssignmentArgs()
        {
            PrincipalId = default!,
            PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
            RoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            Scope = default!
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var storageAccountGetStorageAccountResult = global::Pulumi.AzureNative.Storage.GetStorageAccount.Invoke(new global::Pulumi.AzureNative.Storage.GetStorageAccountInvokeArgs()
        {
            AccountName = "${saName}",
            ResourceGroupName = default!
        });
        var orderServiceSa2Contributor = new global::Pulumi.AzureNative.Authorization.RoleAssignment("order-service-sa2-contributor", new global::Pulumi.AzureNative.Authorization.RoleAssignmentArgs()
        {
            PrincipalId = default!,
            PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
            RoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            Scope = default!
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var storageAccountGetStorageAccountResult2 = global::Pulumi.AzureNative.Storage.GetStorageAccount.Invoke(new global::Pulumi.AzureNative.Storage.GetStorageAccountInvokeArgs()
        {
            AccountName = "${saName}",
            ResourceGroupName = default!
        });
        var orderServiceSa3Contributor = new global::Pulumi.AzureNative.Authorization.RoleAssignment("order-service-sa3-contributor", new global::Pulumi.AzureNative.Authorization.RoleAssignmentArgs()
        {
            PrincipalId = default!,
            PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
            RoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            Scope = default!
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });

        return Task.CompletedTask;
    }
}
