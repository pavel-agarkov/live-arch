using LiveArch.Deployment.Expressions;
using System.Reflection;
using Pulumi;
using Structurizr;
using LiveArch.Deployment.Observability;

namespace LiveArch.Deployment.Observers
{
    /// <summary>
    /// Default observer that keeps the deployment processor behavior unchanged.
    /// </summary>
    internal sealed class NullStructurizrDeploymentObserver : IStructurizrDeploymentObserver
    {
        public void OnResourceCreated(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            Type resourceType,
            string resourceName,
            object args,
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
            object args,
            InvokeOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            ReferencedResourceExpressionModel expressionModel)
        {
        }
    }
}
