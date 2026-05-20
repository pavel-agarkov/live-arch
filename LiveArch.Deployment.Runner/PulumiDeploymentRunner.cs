using LiveArch.Deployment;
using Microsoft.Extensions.Logging;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace LiveArch.Deployment.Runner
{
    internal sealed class PulumiDeploymentRunner
    {
        private readonly DeploymentCommandOptions options;
            private readonly StructurizrDeploymentProcessor deploymentProcessor;
        private readonly ILogger<PulumiDeploymentRunner> logger;

        public PulumiDeploymentRunner(
            DeploymentCommandOptions options,
            StructurizrDeploymentProcessor deploymentProcessor,
            ILogger<PulumiDeploymentRunner> logger)
        {
            this.options = options;
            this.deploymentProcessor = deploymentProcessor;
            this.logger = logger;
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            var stackArgs = new InlineProgramArgs(options.ProjectName, options.StackName, PulumiFn.Create(async () =>
            {
                await deploymentProcessor.ProcessDeploymentAsync(cancellationToken);
            }))
            {
                WorkDir = "./.pulumi",
                Logger = logger,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["PULUMI_BACKEND_URL"] = "file://./state",
                    ["PULUMI_CONFIG_PASSPHRASE_FILE"] = "./secret"
                }
            };

            stackArgs.ProjectSettings!.Author = "Pavel Agarkov";
            stackArgs.ProjectSettings.License = "MIT";
            stackArgs.ProjectSettings.Runtime = new ProjectRuntime(ProjectRuntimeName.Dotnet)
            {
                Options = new ProjectRuntimeOptions
                {
                    Binary = GetRunnerAssemblyPath()
                }
            };
            stackArgs.ProjectSettings.Main = GetRunnerProjectDirectory();
            stackArgs.ProjectSettings.Backend = new ProjectBackend
            {
                Url = "file://./state"
            };

            var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs, cancellationToken);

            switch (options.PulumiCommand)
            {
                case "up":
                    var result = await stack.UpAsync(new UpOptions
                    {
                        Color = "always",
                        OnStandardError = WritePulumiStandardError,
                        OnStandardOutput = WritePulumiStandardOutput
                    }, cancellationToken);
                    return result.Summary.Result == UpdateState.Succeeded ? 0 : 1;

                case "preview":
                    var previewResult = await stack.PreviewAsync(new PreviewOptions
                    {
                        Color = "always",
                        OnStandardError = WritePulumiStandardError,
                        OnStandardOutput = WritePulumiStandardOutput
                    }, cancellationToken);
                    return 0;

                case "destroy":
                    var destroyResult = await stack.DestroyAsync(new DestroyOptions
                    {
                        RunProgram = false,
                        Debug = true,
                        Color= "always",
                        LogVerbosity = 4,
                        OnStandardError = WritePulumiStandardError,
                        OnStandardOutput = WritePulumiStandardOutput
                    }, cancellationToken);
                    return destroyResult.Summary.Result == UpdateState.Succeeded ? 0 : 1;

                default:
                    logger.LogError("Unsupported command: {Command}", options.PulumiCommand);
                    return 1;
            }

        }

        private static void WritePulumiStandardOutput(string message)
        {
            Console.WriteLine(message);
        }

        private static void WritePulumiStandardError(string message)
        {
            Console.Error.WriteLine(message);
        }

        private static string GetRunnerAssemblyPath()
        {
            return typeof(PulumiDeploymentRunner).Assembly.Location;
        }

        private static string GetRunnerProjectDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (directory.GetFiles("*.csproj").Length > 0)
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate a .csproj file.");
        }
    }
}
