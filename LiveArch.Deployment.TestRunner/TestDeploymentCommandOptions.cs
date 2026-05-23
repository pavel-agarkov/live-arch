using LiveArch.Deployment.Configuration;

namespace LiveArch.Deployment.TestRunner
{
    internal sealed class TestDeploymentCommandOptions(string environment, string deployment, string workspacePath) : IDeploymentCommandOptions
    {
        public string Environment { get; } = environment;
        public string Deployment { get; } = deployment;
        public string WorkspacePath { get; } = workspacePath;
    }
}
