using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.AzureData;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.KeyVault.Inputs;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Network;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.DockerBuild.Inputs;
using Pulumi.Testing;
using System.Threading.Tasks;

namespace LiveArch.Deployment
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            await Pulumi.Deployment.TestAsync(new Mocks(), new TestOptions { IsPreview = false }, async () =>
            {
                var ws = new StructurizrComponent("C:\\Projects\\Presentations\\LiveArch\\LiveArch.Diagram\\workspace.json", "Production");
                await ws.ProcessWorkspaceAsync(default);
            });
            return 0;

        }

        public async Task Test()
        {
            // Create an Azure Resource Group
            var resourceGroup = new ResourceGroup("resourceGroup");

            var rg = await GetResourceGroup.InvokeAsync(new GetResourceGroupArgs
            {
                ResourceGroupName = ""
            });

            var kv = await GetVault.InvokeAsync(new GetVaultArgs
            {
                ResourceGroupName = "",
                VaultName = ""
            });

            var kvx = new Vault("keyVault", new VaultArgs
            {
                ResourceGroupName = resourceGroup.Name,
                Properties = new VaultPropertiesArgs
                {
                }
            });

            var mi = new UserAssignedIdentity("", new UserAssignedIdentityArgs
            {
                ResourceName = kv.Name,
            });

            //GetRoleDefinition.InvokeAsync()


            new AccessPolicy("", new AccessPolicyArgs
            {
                ResourceGroupName = rg.Name,
                VaultName = kv.Name,
                Policy = new AccessPolicyEntryArgs
                {
                    ObjectId = mi.PrincipalId,
                    TenantId = "",
                    Permissions = new PermissionsArgs
                    {
                        Secrets = new InputList<Union<string, SecretPermissions>>() { "get", "list" }
                    }
                }
            });

            new RoleAssignment("", new RoleAssignmentArgs
            {
                PrincipalId = mi.PrincipalId,
                Scope = kv.Id,
                RoleDefinitionId = "",
                PrincipalType = Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal

            });

            var sqlReg = await GetSqlServerRegistration.InvokeAsync(new GetSqlServerRegistrationArgs
            {
                SqlServerRegistrationName = kv.Name,
            });

            var pool = await GetElasticPool.InvokeAsync(new GetElasticPoolArgs
            {
                ElasticPoolName = kv.Name,
                ServerName = kv.Name,
            });

            var sqlServer = await GetSqlServer.InvokeAsync(new GetSqlServerArgs
            {
                ResourceGroupName = rg.Name,
                SqlServerName = "",
                SqlServerRegistrationName = ""
            });

            new Database("", new DatabaseArgs
            {
                ResourceGroupName = rg.Name,
                ServerName = sqlServer.Name,
                ElasticPoolId = pool.Id,
                DatabaseName = "",
                Sku = new Pulumi.AzureNative.Sql.Inputs.SkuArgs
                {
                    Name = "Basic"
                }
            });

            var vnet = new VirtualNetwork("", new VirtualNetworkArgs
            {
                VirtualNetworkName = ""
            });

            var oldPlan = await GetAppServicePlan.InvokeAsync(new GetAppServicePlanArgs
            {
                Name = ""
            });

            var plan = new AppServicePlan("", new AppServicePlanArgs
            {
                ResourceGroupName = rg.Name,
                Kind = "Linux",
                Sku = new SkuDescriptionArgs
                {
                    Name = "B1",
                    Tier = "Basic"
                },
                Name = "",
                PerSiteScaling = true
            });

            var myImage = new Pulumi.DockerBuild.Image("", new Pulumi.DockerBuild.ImageArgs
            {
                Builder = new BuilderConfigArgs
                {
                    Name = ""
                },
                Dockerfile = new DockerfileArgs
                {
                    Location = $".Dockerfile",
                },
                Context = new Pulumi.DockerBuild.Inputs.BuildContextArgs
                {
                    Location = $"./fff",
                },
                Tags = new[]
                {
                        Output.Format($"xxx/fff:v1.0.0"),
                    },
                Push = true,
                Registries = new[] {
                        new Pulumi.DockerBuild.Inputs.RegistryArgs
                        {
                            Address = ""
                        }
                    }
            });

            new WebApp("", new WebAppArgs
            {
                ResourceGroupName = rg.Name,
                ServerFarmId = plan.Id,
                Name = "",
                Identity = new Pulumi.AzureNative.Web.Inputs.ManagedServiceIdentityArgs
                {
                    Type = Pulumi.AzureNative.Web.ManagedServiceIdentityType.UserAssigned,
                    UserAssignedIdentities = { mi.Id }
                },
                SiteConfig = new SiteConfigArgs
                {
                    LinuxFxVersion = Output.Format($"DOCKER|{myImage.Ref}"),
                    AcrUseManagedIdentityCreds = true,
                    AcrUserManagedIdentityID = mi.Id,
                    HealthCheckPath = "/health",
                    KeyVaultReferenceIdentity = mi.Id,
                    VnetName = vnet.Name,
                }
            });

            await GetVirtualNetwork.InvokeAsync(new GetVirtualNetworkArgs
            {
                VirtualNetworkName = "",
                ResourceGroupName = rg.Name
            });

            // Create an Azure Storage Account
            var storageAccount = new StorageAccount("sa", new StorageAccountArgs
            {
                ResourceGroupName = resourceGroup.Name,
                Sku = new Pulumi.AzureNative.Storage.Inputs.SkuArgs
                {
                    Name = Pulumi.AzureNative.Storage.SkuName.Standard_LRS
                },
                Kind = Kind.StorageV2
            });

        }
    }
}