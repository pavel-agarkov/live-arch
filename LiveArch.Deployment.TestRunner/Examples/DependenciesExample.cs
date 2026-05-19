using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using System;
using System.Collections.Generic;
using System.Text;

namespace LiveArch.Deployment.TestRunner.Examples
{
    public class DependenciesExample
    {
        public DependenciesExample()
        {
            var rg = new ResourceGroup("rg", new ResourceGroupArgs
            {
                ResourceGroupName = "example-rg",
                Location = "westeurope"
            });

            var sa = new StorageAccount("sa", new StorageAccountArgs
            {
                AccountName = "examplesa",
                ResourceGroupName = rg.Name,
                // ...
            });
        }
    }
}
