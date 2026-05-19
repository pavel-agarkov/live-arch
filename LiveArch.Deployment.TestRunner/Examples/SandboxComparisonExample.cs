using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Storage.Inputs;

namespace LiveArch.Deployment.TestRunner.Examples
{
    public class SandboxComparisonExample
    {
        public SandboxComparisonExample()
        {
            const string storageBlobDataContributor = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe";

            var sandbox = GetResourceGroup.Invoke(new GetResourceGroupInvokeArgs
            {
              ResourceGroupName = "sandbox"
            });
            var testSa = new StorageAccount("testSa", new StorageAccountArgs
            {
              ResourceGroupName = sandbox.Apply(x => x.Name),
              AccountName = "testsa",
              Sku = new SkuArgs
              {
                Name = SkuName.Standard_LRS
              },
              Kind = Kind.StorageV2,
            });

            var testMi = new UserAssignedIdentity("testMi", new UserAssignedIdentityArgs
            {
              ResourceGroupName = sandbox.Apply(x => x.Name),
              Location = sandbox.Apply(x => x.Location),
              ResourceName = "test-mi"
            });

            var testMiTestSaContribute = new RoleAssignment("testMiTestSaContribute", new RoleAssignmentArgs
            {
              PrincipalId = testMi.PrincipalId,
              PrincipalType = PrincipalType.ServicePrincipal,
              RoleDefinitionId = storageBlobDataContributor,
              Scope = testSa.Id
            });
        }
    }
}
