using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using System;
using System.Collections.Generic;
using System.Text;

namespace LiveArch.Deployment.TestRunner.Examples
{
    internal class ResourceExamples
    {
        public static async Task TestCases()
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
                    ConnectionStrings = {
                        new ConnStringInfoArgs
                        {
                            Name = "my-db-conn-string",
                            ConnectionString = "Server=tcp:myserver.database.windows.net,1433;Initial Catalog=mydb;Persist Security Info=False;User ID=myuser;Password=mypassword;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;",
                            Type = ConnectionStringType.SQLAzure
                        }
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

            var db = new Database("", new Pulumi.AzureNative.Sql.DatabaseArgs
            {
                DatabaseName = "",
                ServerName = "",
                ResourceGroupName = ""
            });


            var sa = new Pulumi.AzureNative.Storage.StorageAccount("", new Pulumi.AzureNative.Storage.StorageAccountArgs
            {
                AccountName = "",
                ResourceGroupName = "",
                Sku = new Pulumi.AzureNative.Storage.Inputs.SkuArgs
                {
                    Name = Pulumi.AzureNative.Storage.SkuName.Standard_LRS
                },
                Kind = Pulumi.AzureNative.Storage.Kind.StorageV2,
                AllowBlobPublicAccess = false,
                AccessTier = AccessTier.Cool,
                MinimumTlsVersion = MinimumTlsVersion.TLS1_2
            });


            // 🔐 Managed Identity
            var identity = new UserAssignedIdentity("storage-identity",
                new UserAssignedIdentityArgs
                {
                    ResourceGroupName = "resourceGroup.Name",
                    Location = "resourceGroup.Location"
                });


            // 🔑 Назначаем роль identity на storage account
            var roleAssignment = new RoleAssignment("storage-access",
                new RoleAssignmentArgs
                {
                    PrincipalId = identity.PrincipalId,
                    PrincipalType = Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
                    RoleDefinitionId = "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
                    Scope = sa.Id
                });

            var sbNs = Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke(new Pulumi.AzureNative.ServiceBus.GetNamespaceInvokeArgs
            {
                NamespaceName = "",
                ResourceGroupName = ""
            });
            var sbTopic = new Pulumi.AzureNative.ServiceBus.Topic("sb-topic", new Pulumi.AzureNative.ServiceBus.TopicArgs
            {
                TopicName = "",
                NamespaceName = sbNs.Apply(x => x.ServiceBusEndpoint),
                ResourceGroupName = ""
            });
            var sbSub = new Pulumi.AzureNative.ServiceBus.Subscription("sb-subscription", new Pulumi.AzureNative.ServiceBus.SubscriptionArgs
            {
                SubscriptionName = "",
                TopicName = sbTopic.Name,
                NamespaceName = sbNs.Apply(x => x.Name),
                ResourceGroupName = ""
            });

        }
    }
}
