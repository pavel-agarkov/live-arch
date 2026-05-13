using LiveArch.Deployment;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
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
        private readonly ILogger<PulumiDeploymentRunner> logger;

        public PulumiDeploymentRunner(
            DeploymentCommandOptions options,
            DeploymentVariablesProvider variablesProvider,
            IResourceHierarchyBuilder resourceHierarchyBuilder,
            ResourceTypesRegistry resourceTypesRegistry,
            DockerImageReferenceConfigurator dockerImageReferenceConfigurator,
            ILogger<PulumiDeploymentRunner> logger)
        {
            this.options = options;
            this.variablesProvider = variablesProvider;
            this.resourceHierarchyBuilder = resourceHierarchyBuilder;
            this.resourceTypesRegistry = resourceTypesRegistry;
            this.dockerImageReferenceConfigurator = dockerImageReferenceConfigurator;
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
                    dockerImageReferenceConfigurator);

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
            stackArgs.ProjectSettings.Backend = new ProjectBackend
            {
                Url = "file://./state"
            };

            var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs, cancellationToken);
            var result = await stack.UpAsync(new UpOptions
            {
                OnEvent = OnEngineEvent
            }, cancellationToken);

            return result.Summary.Result == UpdateState.Succeeded ? 0 : 1;
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
                    logger.LogWarning(message);
                }
            }
        }
    }
}
