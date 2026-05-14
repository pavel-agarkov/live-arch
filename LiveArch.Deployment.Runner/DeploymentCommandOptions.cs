using Microsoft.Extensions.Configuration;

namespace LiveArch.Deployment.Runner
{
    internal sealed class DeploymentCommandOptions
    {
        public string ProjectName => "LiveArch.Deployment";

        public string StackName => $"{Environment}-{Deployment}";

        public string Environment { get; }

        /// <summary>
        /// Structurizr deployment view name.
        /// This is used to select the relevant deployment view from the Structurizr workspace.
        /// It will be used as a suffix for the Pulumi stack name, e.g. "prod-order-env".
        /// Only resources that are part of the specified deployment view will be deployed or referenced.
        /// </summary>
        public string Deployment { get; }

        /// <summary>
        /// Structurizr workspace file path. Can be absolute or relative to the current working directory. The file must exist.
        /// </summary>
        public string WorkspacePath { get; }

        /// <summary>
        /// Pulumi command to execute.
        /// </summary>
        public string PulumiCommand { get; }


        private DeploymentCommandOptions(string environment, string deployment, string workspacePath, string pulumiCommand)
        {
            Environment = environment;
            Deployment = deployment;
            WorkspacePath = workspacePath;
            PulumiCommand = pulumiCommand;
        }

        public static DeploymentCommandOptions FromConfiguration(IConfiguration configuration)
        {
            var environment = GetRequiredValue(configuration, "--environment", "environment", "Environment", "ENVIRONMENT");
            var deployment = GetRequiredValue(configuration, "--deployment", "deployment", "Deployment", "DEPLOYMENT");
            var workspacePath = GetRequiredValue(configuration, "--workspace-path", "workspace-path", "workspacePath", "workspace", "WORKSPACE_PATH");
            var pulumiCommand = GetRequiredValue(configuration, "--pulumi-command", "pulumi-command", "PulumiCommand", "PULUMI_COMMAND");

            var resolvedWorkspacePath = Path.IsPathRooted(workspacePath)
                ? workspacePath
                : Path.GetFullPath(workspacePath, Directory.GetCurrentDirectory());

            if (!File.Exists(resolvedWorkspacePath))
            {
                throw new FileNotFoundException($"Structurizr workspace file '{resolvedWorkspacePath}' was not found.");
            }

            return new DeploymentCommandOptions(environment, deployment, resolvedWorkspacePath, pulumiCommand);
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
                $"Missing required argument {optionName}. Example: --environment prod --deployment order-env --workspace-path workspace.json");
        }
    }
}
