namespace LiveArch.Deployment.Configuration
{
    public interface IStructurizrDeploymentProcessor
    {
        Task ProcessDeploymentAsync(CancellationToken cancellationToken);
    }
}
