using LiveArch.Deployment;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Microsoft.Extensions.Logging;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace LiveArch.Deployment.Runner
{
    internal sealed class PulumiDeploymentRunner
    {
        private readonly DeploymentCommandOptions options;
        private readonly DeploymentVariablesProvider variablesProvider;
        private readonly IResourceHierarchyBuilder resourceHierarchyBuilder;
        private readonly ResourceTypesRegistry resourceTypesRegistry;
        private readonly DockerImageReferenceConfigurator dockerImageReferenceConfigurator;
        private readonly IConversionEngine conversionEngine;
        private readonly ITransformerRegistry transformerRegistry;
        private readonly ILogger<PulumiDeploymentRunner> logger;

        public PulumiDeploymentRunner(
            DeploymentCommandOptions options,
            DeploymentVariablesProvider variablesProvider,
            IResourceHierarchyBuilder resourceHierarchyBuilder,
            ResourceTypesRegistry resourceTypesRegistry,
            DockerImageReferenceConfigurator dockerImageReferenceConfigurator,
            IConversionEngine conversionEngine,
            ITransformerRegistry transformerRegistry,
            ILogger<PulumiDeploymentRunner> logger)
        {
            this.options = options;
            this.variablesProvider = variablesProvider;
            this.resourceHierarchyBuilder = resourceHierarchyBuilder;
            this.resourceTypesRegistry = resourceTypesRegistry;
            this.dockerImageReferenceConfigurator = dockerImageReferenceConfigurator;
            this.conversionEngine = conversionEngine;
            this.transformerRegistry = transformerRegistry;
            this.logger = logger;
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            var stackArgs = new InlineProgramArgs(options.ProjectName, options.StackName, PulumiFn.Create(async () =>
            {
                var deployment = new StructurizrComponent(
                    options.WorkspacePath,
                    options.Environment,
                    options.Deployment,
                    variablesProvider.GetVariables(),
                    resourceHierarchyBuilder.Registry,
                    resourceTypesRegistry,
                    dockerImageReferenceConfigurator,
                    conversionEngine,
                    transformerRegistry);

                await deployment.ProcessWorkspaceAsync(cancellationToken);
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
                        OnEvent = OnEngineEvent,
                        OnStandardError = LogError
                    }, cancellationToken);
                    return result.Summary.Result == UpdateState.Succeeded ? 0 : 1;

                case "preview":
                    var previewResult = await stack.PreviewAsync(new PreviewOptions
                    {
                        OnEvent = OnEngineEvent,
                        OnStandardError = LogError
                    }, cancellationToken);
                    return 0;

                case "destroy":
                    var destroyResult = await stack.DestroyAsync(new DestroyOptions
                    {
                        RunProgram = false,
                        Debug = true,
                        Color= "always",
                        LogVerbosity = 4,
                        OnEvent = OnEngineEvent,
                        OnStandardError = LogError
                    }, cancellationToken);
                    return destroyResult.Summary.Result == UpdateState.Succeeded ? 0 : 1;

                default:
                    logger.LogError("Unsupported command: {Command}", options.PulumiCommand);
                    return 1;
            }

        }

        private void LogError(string message)
        {
            logger.LogError(message);
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

        private void OnEngineEvent(EngineEvent engineEvent)
        {
            if (engineEvent.StandardOutputEvent != null)
            {
                logger.LogInformation(engineEvent.StandardOutputEvent.Message);
            }

            if (engineEvent.DiagnosticEvent != null)
            {
                var message = engineEvent.DiagnosticEvent.Message;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    logger.LogInformation(message);
                }
            }
        }
    }
}
