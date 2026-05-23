using LiveArch.Deployment.Controls;
using Pulumi.AzureNative.AppConfiguration;
using Pulumi.AzureNative.Authorization;

namespace LiveArch.Deployment.TestRunner
{
    public class DeploymentTests : DeploymentTestBase
    {
        [Fact]
        public async Task ShouldCreateAllResourcesForOrderService()
        {
            var ws = await ProcessDeployment("order-env");
            var createdSummary = string.Join(", ", ws.CreatedResources.Values.GroupBy(resource => resource.GetType().Name).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));
            var referencedSummary = string.Join(", ", ws.ReferencedResources.Values.GroupBy(resource => resource.GetType().Name).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));

            ws.CreatedResources.Count.Should().Be(13, "created: {0}; referenced: {1}", createdSummary, referencedSummary);

            ws.ReferencedResources.Count.Should().Be(15);
        }

        [Fact]
        public async Task ShouldCreateAllResourcesForDeliveryService()
        {
            var ws = await ProcessDeployment("delivery-env");

            ws.CreatedResources.Count.Should().Be(8);

            ws.ReferencedResources.Count.Should().Be(11);
        }

        [Fact]
        public async Task ShouldCreateAllSharedResources()
        {
            var ws = await ProcessDeployment("shared-env");

            ws.CreatedResources.Count.Should().Be(4);

            ws.ReferencedResources.Count.Should().Be(0);
        }

        [Fact]
        public async Task ShouldGetAllSharedResourceReferences()
        {
            var ws = await ProcessDeployment("shared-ref-env");

            ws.CreatedResources.GroupBy(x => x.Key.Node).Count().Should().Be(0);

            ws.ReferencedResources.GroupBy(x => x.Key.Node).Count().Should().Be(4);
        }

        [Fact]
        public async Task ShouldCreateAllSandboxResources()
        {
            var ws = await ProcessDeployment("sandbox");

            ws.CreatedResources.Count.Should().Be(3);

            ws.ReferencedResources.Count.Should().Be(1);
        }

        [Theory]
        [InlineData("sa1", 1, 2)]
        [InlineData("sa1, sa2, sa3", 3, 2)]
        [InlineData("sa1,sa2,sa3,sa4", 4, 2)]
        [InlineData("", 0, 2)]
        public async Task ShouldCreateExpectedLoopScopesForConfiguredStorageAccounts(string storageAccounts, int expectedScopeCount, int expectedElementsPerScope)
        {
            testMocks.AddGetResourceMock<GetKeyValueArgs>(typeof(GetKeyValue), _ => new Dictionary<string, object>
            {
                ["value"] = storageAccounts,
            });

            var ws = await ProcessDeployment("order-env");
            var loop = ws.CreatedResources
                .Single(resource => resource.Value is ForEachLoop);
            var parentScope = FindScopeById(ws.RootScope, loop.Key.ScopeId);

            var loopScopes = parentScope.ChildScopes
                .Where(scope => ReferenceEquals(scope.OwnerResource, loop.Value))
                .ToList();

            loopScopes.Count.Should().Be(expectedScopeCount);
            loopScopes.Select(scope => scope.CreatedResources.Count + scope.ReferencedResources.Count)
                .Should().OnlyContain(elementCount => elementCount == expectedElementsPerScope);
        }

        [Theory]
        [InlineData("sa1", 1)]
        [InlineData("sa1, sa2, sa3", 3)]
        [InlineData("", 0)]
        public async Task ShouldRepeatIncomingRelationshipResourcesInsideLoopScopes(string storageAccounts, int expectedRoleAssignments)
        {
            testMocks.AddGetResourceMock<GetKeyValueArgs>(typeof(GetKeyValue), _ => new Dictionary<string, object>
            {
                ["value"] = storageAccounts,
            });

            var ws = await ProcessDeployment("order-env");

            ws.CreatedResources.Values.OfType<RoleAssignment>().Count().Should().Be(expectedRoleAssignments);
        }

        private static IEnumerable<StructurizrDeploymentProcessor.ResourceScope> GetDescendantScopes(StructurizrDeploymentProcessor.ResourceScope scope)
        {
            foreach (var childScope in scope.ChildScopes)
            {
                yield return childScope;

                foreach (var nestedScope in GetDescendantScopes(childScope))
                {
                    yield return nestedScope;
                }
            }
        }

        private static StructurizrDeploymentProcessor.ResourceScope FindScopeById(StructurizrDeploymentProcessor.ResourceScope scope, int scopeId)
        {
            return scope.Id == scopeId
                ? scope
                : GetDescendantScopes(scope).Single(childScope => childScope.Id == scopeId);
        }
    }
}
