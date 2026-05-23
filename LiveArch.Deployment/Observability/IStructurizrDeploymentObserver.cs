using Pulumi;
using Structurizr;
using System.Reflection;
using LiveArch.Deployment.Expressions;

namespace LiveArch.Deployment.Observability
{
    /// <summary>
    /// Observes resource registrations produced during deployment processing.
    /// </summary>
    public interface IStructurizrDeploymentObserver
    {
        /// <summary>
        /// Notifies the observer after a newly created resource has been registered in a scope.
        /// </summary>
        /// <param name="node">Model node represented by the resource.</param>
        /// <param name="scope">Scope that owns the registration.</param>
        /// <param name="resource">Registered resource.</param>
        /// <param name="resourceType">Concrete Pulumi resource CLR type that was instantiated.</param>
        /// <param name="resourceName">Logical resource name passed to the resource constructor.</param>
        /// <param name="options">Prepared custom resource options used during creation.</param>
        /// <param name="dependsOn">Explicit resource dependencies applied to the created resource.</param>
        /// <param name="expressionModel">Expression trace captured while preparing resource arguments.</param>
        void OnResourceCreated(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            Type resourceType,
            string resourceName,
            CustomResourceOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            CreatedResourceExpressionModel expressionModel);

        /// <summary>
        /// Notifies the observer after a referenced or invoke-backed resource has been registered in a scope.
        /// </summary>
        /// <param name="node">Model node represented by the resource.</param>
        /// <param name="scope">Scope that owns the registration.</param>
        /// <param name="resource">Registered resource or invoke result.</param>
        /// <param name="resourceName">Logical resource name associated with the invoke-backed reference.</param>
        /// <param name="invokeMethod">Invoke method used to materialize the referenced resource.</param>
        /// <param name="options">Prepared invoke options used during the call.</param>
        /// <param name="dependsOn">Explicit resource dependencies applied to the invoke-backed resource lookup.</param>
        /// <param name="expressionModel">Expression trace captured while preparing invoke arguments.</param>
        void OnResourceReferenced(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            string resourceName,
            MethodInfo invokeMethod,
            InvokeOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            ReferencedResourceExpressionModel expressionModel);
    }
}
