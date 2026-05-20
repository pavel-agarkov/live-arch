namespace LiveArch.Deployment.Configuration
{
    public interface IDeploymentCommandOptions
    {
        public string Environment { get; }
        public string Deployment { get; }
        public string WorkspacePath { get; }
    }
}
