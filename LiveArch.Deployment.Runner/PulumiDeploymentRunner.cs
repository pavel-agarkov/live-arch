using LiveArch.Deployment;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
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

        public PulumiDeploymentRunner(
            DeploymentCommandOptions options,
            DeploymentVariablesProvider variablesProvider,
            IResourceHierarchyBuilder resourceHierarchyBuilder,
            ResourceTypesRegistry resourceTypesRegistry,
            DockerImageReferenceConfigurator dockerImageReferenceConfigurator)
        {
            this.options = options;
            this.variablesProvider = variablesProvider;
            this.resourceHierarchyBuilder = resourceHierarchyBuilder;
            this.resourceTypesRegistry = resourceTypesRegistry;
            this.dockerImageReferenceConfigurator = dockerImageReferenceConfigurator;
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
            }));

            var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs);
            var result = await stack.UpAsync(new UpOptions
            {
                OnEvent = OnEngineEvent
            });

            return result.Summary.Result == UpdateState.Succeeded ? 0 : 1;
        }

        private static void OnEngineEvent(EngineEvent engineEvent)
        {
            if (engineEvent.StandardOutputEvent != null)
            {
                Console.WriteLine(engineEvent.StandardOutputEvent.Message);
            }

            if (engineEvent.DiagnosticEvent != null)
            {
                var message = engineEvent.DiagnosticEvent.Message;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    Console.WriteLine(message);
                }
            }
        }
    }
}
