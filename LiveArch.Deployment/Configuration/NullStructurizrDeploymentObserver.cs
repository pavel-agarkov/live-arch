using Structurizr;

namespace LiveArch.Deployment.Configuration
{
    /// <summary>
    /// Default observer that keeps the deployment processor behavior unchanged.
    /// </summary>
    internal sealed class NullStructurizrDeploymentObserver : IStructurizrDeploymentObserver
    {
        public void OnResourceCreated(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource)
        {
        }

        public void OnResourceReferenced(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource)
        {
        }
    }
}
