using LiveArch.Deployment.Expressions;
using LiveArch.Deployment.Observability;
using Pulumi;
using Structurizr;
using System.Reflection;

namespace LiveArch.Deployment.TestRunner.Export
{
    internal sealed class RecordingStructurizrDeploymentObserver : IStructurizrDeploymentObserver
    {
        public List<RegisteredResource> CreatedResources { get; } = [];
        public List<RegisteredResource> ReferencedResources { get; } = [];

        public void OnResourceCreated(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            global::System.Type resourceType,
            string resourceName,
            CustomResourceOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            CreatedResourceExpressionModel expressionModel)
        {
            CreatedResources.Add(new RegisteredResource(node, scope.Id, resource, dependsOn, resourceName, resourceType, null, options, expressionModel));
        }

        public void OnResourceReferenced(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            string resourceName,
            MethodInfo invokeMethod,
            InvokeOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            ReferencedResourceExpressionModel expressionModel)
        {
            ReferencedResources.Add(new RegisteredResource(node, scope.Id, resource, dependsOn, resourceName, null, invokeMethod, options, expressionModel));
        }
    }
}
