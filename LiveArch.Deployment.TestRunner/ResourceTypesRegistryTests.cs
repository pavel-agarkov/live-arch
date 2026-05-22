using LiveArch.Deployment.ResourceTypes;
using Pulumi;
using Pulumi.AzureNative.ServiceBus;

namespace LiveArch.Deployment.TestRunner
{
    public class ResourceTypesRegistryTests
    {
        [Fact]
        public void ShouldPreferInvokeOutputOptionsOverInvokeOptionsForInvokeMethods()
        {
            var registry = new ResourceTypesRegistry([
                new ResourceTypesAssemblyMarker(typeof(GetNamespace))
            ]);

            var found = registry.TryGetInvokeMethod("azure-native:servicebus:getNamespace", out var method);

            found.Should().BeTrue();
            method.Should().NotBeNull();
            method!.GetParameters()[1].ParameterType.Should().Be(typeof(InvokeOutputOptions));
        }
    }
}
