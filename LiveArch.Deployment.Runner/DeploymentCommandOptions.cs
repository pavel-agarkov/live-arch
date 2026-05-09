using Microsoft.Extensions.Configuration;

namespace LiveArch.Deployment.Runner
{
    internal sealed class DeploymentCommandOptions
    {
        public string ProjectName => "LiveArch.Deployment";

        public string StackName => $"{Environment}-{Deployment}";

        public string Environment { get; }

        public string Deployment { get; }

        public string WorkspacePath { get; }

        private DeploymentCommandOptions(string environment, string deployment, string workspacePath)
        {
            Environment = environment;
            Deployment = deployment;
            WorkspacePath = workspacePath;
        }

        public static DeploymentCommandOptions FromConfiguration(IConfiguration configuration)
        {
            var environment = GetRequiredValue(configuration, "--environment", "environment", "Environment");
            var deployment = GetRequiredValue(configuration, "--deployment", "deployment", "Deployment");
            var workspacePath = GetRequiredValue(configuration, "--workspace-path", "workspace-path", "workspacePath", "workspace", "WORKSPACE_PATH");

            var resolvedWorkspacePath = Path.IsPathRooted(workspacePath)
                ? workspacePath
                : Path.GetFullPath(workspacePath, Directory.GetCurrentDirectory());

            if (!File.Exists(resolvedWorkspacePath))
            {
                throw new FileNotFoundException($"Workspace file '{resolvedWorkspacePath}' was not found.");
            }

            return new DeploymentCommandOptions(environment, deployment, resolvedWorkspacePath);
        }

        private static string GetRequiredValue(IConfiguration configuration, string optionName, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = configuration[key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            throw new InvalidOperationException(
                $"Missing required argument {optionName}. Example: --environment prod --deployment order-env --workspace-path ..\\LiveArch.Diagram\\workspace.json");
        }
    }
}
