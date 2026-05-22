using Pulumi;
using Structurizr;

namespace LiveArch.Deployment.Configuration
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
        /// <param name="dependsOn">Explicit resource dependencies applied to the created resource.</param>
        void OnResourceCreated(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource, IReadOnlyCollection<Resource> dependsOn);

        /// <summary>
        /// Notifies the observer after a referenced or invoke-backed resource has been registered in a scope.
        /// </summary>
        /// <param name="node">Model node represented by the resource.</param>
        /// <param name="scope">Scope that owns the registration.</param>
        /// <param name="resource">Registered resource or invoke result.</param>
        /// <param name="dependsOn">Explicit resource dependencies applied to the invoke-backed resource lookup.</param>
        void OnResourceReferenced(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource, IReadOnlyCollection<Resource> dependsOn);
    }
}
