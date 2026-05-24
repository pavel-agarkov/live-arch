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

    public static Task ProcessAsync(LiveArchOrderDeploymentVariables vars, CancellationToken cancellationToken = default)
    {
        var sharedRgReference = global::Pulumi.AzureNative.Resources.GetResourceGroup.Invoke(new global::Pulumi.AzureNative.Resources.GetResourceGroupInvokeArgs()
        {
            ResourceGroupName = vars.ResourceGroupName
        });
        var serviceBusNamespace = global::Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke(new global::Pulumi.AzureNative.ServiceBus.GetNamespaceInvokeArgs()
        {
            NamespaceName = $"{vars.Env}-sbns",
            ResourceGroupName = sharedRgReference.Apply(value => value.Name)
        });
        var orderEventsTopic = global::Pulumi.AzureNative.ServiceBus.GetTopic.Invoke(new global::Pulumi.AzureNative.ServiceBus.GetTopicInvokeArgs()
        {
            NamespaceName = serviceBusNamespace.Apply(value => value.Name),
            ResourceGroupName = sharedRgReference.Apply(value => value.Name),
            TopicName = $"{vars.Env}-order-events-topic"
        });
        var deliveryEventsTopic = global::Pulumi.AzureNative.ServiceBus.GetTopic.Invoke(new global::Pulumi.AzureNative.ServiceBus.GetTopicInvokeArgs()
        {
            NamespaceName = serviceBusNamespace.Apply(value => value.Name),
            ResourceGroupName = sharedRgReference.Apply(value => value.Name),
            TopicName = $"{vars.Env}-delivery-events-topic"
        });
        var orderRg = global::Pulumi.AzureNative.Resources.GetResourceGroup.Invoke(new global::Pulumi.AzureNative.Resources.GetResourceGroupInvokeArgs()
        {
            ResourceGroupName = vars.ResourceGroupName
        });
        var prodKeyVault = global::Pulumi.AzureNative.KeyVault.GetVault.Invoke(new global::Pulumi.AzureNative.KeyVault.GetVaultInvokeArgs()
        {
            ResourceGroupName = orderRg.Apply(value => value.Name),
            VaultName = vars.KeyVaultName
        });
        var orderServiceMi = new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity("order-service-mi", new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentityArgs()
        {
            Location = orderRg.Apply(value => value.Location),
            ResourceGroupName = orderRg.Apply(value => value.Name),
            ResourceName = $"{vars.Env}-order-service-mi"
        }, null);
        var saList = global::Pulumi.AzureNative.AppConfiguration.GetKeyValue.Invoke(new global::Pulumi.AzureNative.AppConfiguration.GetKeyValueInvokeArgs()
        {
            ConfigStoreName = vars.AppConfigName,
            KeyValueName = "storageAccounts",
            ResourceGroupName = orderRg.Apply(value => value.Name)
        });
        var orderServiceKvAccessPolicy = new global::Pulumi.AzureNative.KeyVault.AccessPolicy("order-service-kv-access-policy", new global::Pulumi.AzureNative.KeyVault.AccessPolicyArgs()
        {
            Policy = new global::Pulumi.AzureNative.KeyVault.Inputs.AccessPolicyEntryArgs()
            {
                ObjectId = orderServiceMi.PrincipalId,
                Permissions = new global::Pulumi.AzureNative.KeyVault.Inputs.PermissionsArgs(),
                TenantId = vars.TenantId
            },
            ResourceGroupName = orderRg.Apply(value => value.Name),
            VaultName = prodKeyVault.Apply(value => value.Name)
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var sQLServerRegistration = global::Pulumi.AzureNative.AzureData.GetSqlServerRegistration.Invoke(new global::Pulumi.AzureNative.AzureData.GetSqlServerRegistrationInvokeArgs()
        {
            ResourceGroupName = orderRg.Apply(value => value.Name),
            SqlServerRegistrationName = vars.SqlServerRegistrationName
        });
        var sQLServer = global::Pulumi.AzureNative.AzureData.GetSqlServer.Invoke(new global::Pulumi.AzureNative.AzureData.GetSqlServerInvokeArgs()
        {
            ResourceGroupName = orderRg.Apply(value => value.Name),
            SqlServerName = vars.SqlServerName,
            SqlServerRegistrationName = sQLServerRegistration.Apply(value => value.Name)
        });
        var elasticPool = global::Pulumi.AzureNative.Sql.GetElasticPool.Invoke(new global::Pulumi.AzureNative.Sql.GetElasticPoolInvokeArgs()
        {
            ElasticPoolName = vars.SqlElasticPoolName,
            ResourceGroupName = orderRg.Apply(value => value.Name),
            ServerName = sQLServer.Apply(value => value.Name)
        });
        var orderDb = new global::Pulumi.AzureNative.Sql.Database("order-db", new global::Pulumi.AzureNative.Sql.DatabaseArgs()
        {
            DatabaseName = $"{vars.Env}-order-db",
            ElasticPoolId = elasticPool.Apply(value => value.Id),
            Location = orderRg.Apply(value => value.Location),
            ResourceGroupName = orderRg.Apply(value => value.Name),
            ServerName = sQLServer.Apply(value => value.Name)
        }, null);
        var virtualNetwork = global::Pulumi.AzureNative.Network.GetVirtualNetwork.Invoke(new global::Pulumi.AzureNative.Network.GetVirtualNetworkInvokeArgs()
        {
            ResourceGroupName = orderRg.Apply(value => value.Name),
            VirtualNetworkName = vars.VnetName
        });
        var prodAppServicePlan = global::Pulumi.AzureNative.Web.GetAppServicePlan.Invoke(new global::Pulumi.AzureNative.Web.GetAppServicePlanInvokeArgs()
        {
            Name = $"{vars.Env}-app-service-plan",
            ResourceGroupName = orderRg.Apply(value => value.Name)
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
            Location = orderRg.Apply(value => value.Location),
            Name = $"{vars.Env}-order-api",
            ResourceGroupName = orderRg.Apply(value => value.Name),
            ServerFarmId = prodAppServicePlan.Apply(value => value.Id),
            SiteConfig = new global::Pulumi.AzureNative.Web.Inputs.SiteConfigArgs()
            {
                Cors = new global::Pulumi.AzureNative.Web.Inputs.CorsSettingsArgs(),
                LinuxFxVersion = default!,
                VnetName = virtualNetwork.Apply(value => value.Name)
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
            Location = orderRg.Apply(value => value.Location),
            Name = $"{vars.Env}-order-worker",
            ResourceGroupName = orderRg.Apply(value => value.Name),
            ServerFarmId = prodAppServicePlan.Apply(value => value.Id),
            SiteConfig = new global::Pulumi.AzureNative.Web.Inputs.SiteConfigArgs()
            {
                LinuxFxVersion = default!,
                VnetName = virtualNetwork.Apply(value => value.Name)
            }
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderDb, orderServiceMi } });
        var orderWorkerSubscriptionToDeliveryEventsTopic = new global::LiveArch.Resources.Azure.ServiceBus.ReadableSubscription("order-worker-subscription-to-delivery-events-topic", new global::LiveArch.Resources.Azure.ServiceBus.ReadableSubscriptionArgs()
        {
            SubscriptionArgs = new global::Pulumi.AzureNative.ServiceBus.SubscriptionArgs()
            {
                NamespaceName = serviceBusNamespace.Apply(value => value.Name),
                ResourceGroupName = sharedRgReference.Apply(value => value.Name),
                TopicName = deliveryEventsTopic.Apply(value => value.Name)
            },
            PrincipalId = orderWorkerWebApp.Identity.Apply(value => value!.PrincipalId)
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderWorkerWebApp } });
        var storageAccount = global::Pulumi.AzureNative.Storage.GetStorageAccount.Invoke(new global::Pulumi.AzureNative.Storage.GetStorageAccountInvokeArgs()
        {
            AccountName = "${saName}",
            ResourceGroupName = orderRg.Apply(value => value.Name)
        });
        var orderServiceSa1Contributor = new global::Pulumi.AzureNative.Authorization.RoleAssignment("order-service-sa1-contributor", new global::Pulumi.AzureNative.Authorization.RoleAssignmentArgs()
        {
            PrincipalId = orderServiceMi.PrincipalId,
            PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
            RoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            Scope = storageAccount.Apply(value => value.Id)
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var storageAccountGetStorageAccountResult = global::Pulumi.AzureNative.Storage.GetStorageAccount.Invoke(new global::Pulumi.AzureNative.Storage.GetStorageAccountInvokeArgs()
        {
            AccountName = "${saName}",
            ResourceGroupName = orderRg.Apply(value => value.Name)
        });
        var orderServiceSa2Contributor = new global::Pulumi.AzureNative.Authorization.RoleAssignment("order-service-sa2-contributor", new global::Pulumi.AzureNative.Authorization.RoleAssignmentArgs()
        {
            PrincipalId = orderServiceMi.PrincipalId,
            PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
            RoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            Scope = storageAccountGetStorageAccountResult.Apply(value => value.Id)
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });
        var storageAccountGetStorageAccountResult2 = global::Pulumi.AzureNative.Storage.GetStorageAccount.Invoke(new global::Pulumi.AzureNative.Storage.GetStorageAccountInvokeArgs()
        {
            AccountName = "${saName}",
            ResourceGroupName = orderRg.Apply(value => value.Name)
        });
        var orderServiceSa3Contributor = new global::Pulumi.AzureNative.Authorization.RoleAssignment("order-service-sa3-contributor", new global::Pulumi.AzureNative.Authorization.RoleAssignmentArgs()
        {
            PrincipalId = orderServiceMi.PrincipalId,
            PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
            RoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            Scope = storageAccountGetStorageAccountResult2.Apply(value => value.Id)
        }, new global::Pulumi.CustomResourceOptions { DependsOn = { orderServiceMi } });

        return Task.CompletedTask;
    }
}
