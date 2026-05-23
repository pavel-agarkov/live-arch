using LiveArch.Deployment.Expressions;
using LiveArch.Deployment.Observability;
using Pulumi;
using Structurizr;
using System.Reflection;

namespace LiveArch.Deployment.TestRunner
{
    internal sealed class NoOpStructurizrDeploymentObserver : IStructurizrDeploymentObserver
    {
        public void OnResourceCreated(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            Type resourceType,
            string resourceName,
            CustomResourceOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            CreatedResourceExpressionModel expressionModel)
        {
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
        }
    }
}
