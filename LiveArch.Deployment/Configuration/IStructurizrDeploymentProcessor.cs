namespace LiveArch.Deployment.Configuration
{
    /// <summary>
    /// Executes the selected Structurizr deployment view and materializes its resources.
    /// </summary>
    public interface IStructurizrDeploymentProcessor
    {
        /// <summary>
        /// Processes the active deployment view.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the deployment run.</param>
        Task ProcessDeploymentAsync(CancellationToken cancellationToken);
    }
}
