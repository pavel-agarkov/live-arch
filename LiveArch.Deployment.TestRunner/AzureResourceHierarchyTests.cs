using FluentAssertions;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using Xunit;

namespace LiveArch.Deployment.TestRunner
{
    public class AzureResourceHierarchyTests
    {
        [Fact]
        public void GetDynamicRegistry_ShouldCreateScopeRuleForTypesWithIdProperty()
        {
            var hierarchy = new AzureResourceHierarchy();

            var registry = hierarchy.GetDynamicRegistry([
                typeof(NewResource),
                typeof(ExistingResourceResult),
                typeof(ResourceWithoutId)
            ]);

            registry.Should().ContainKey(typeof(NewResource));
            registry.Should().ContainKey(typeof(ExistingResourceResult));
            registry.Should().NotContainKey(typeof(ResourceWithoutId));

            var newResourceRule = registry[typeof(NewResource)].Should().ContainSingle().Subject;
            newResourceRule.TargetInputProperties.Should().Equal("scope");
            newResourceRule.ParentOutputProperty.Compile()(new NewResource { Id = "new-resource-scope" }).Should().Be("new-resource-scope");

            var existingResourceRule = registry[typeof(ExistingResourceResult)].Should().ContainSingle().Subject;
            existingResourceRule.TargetInputProperties.Should().Equal("scope");
            existingResourceRule.ParentOutputProperty.Compile()(new ExistingResourceResult { Id = "existing-resource-scope" }).Should().Be("existing-resource-scope");
        }

        private abstract class ResourceBase
        {
            public string Id { get; set; } = string.Empty;
        }

        private sealed class NewResource : ResourceBase
        {
        }

        private sealed class ExistingResourceResult
        {
            public string Id = string.Empty;
        }

        private sealed class ResourceWithoutId
        {
            public string Name { get; set; } = string.Empty;
        }
    }
}
