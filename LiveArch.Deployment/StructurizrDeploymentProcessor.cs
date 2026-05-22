using LiveArch.Deployment.Adapters;
using LiveArch.Deployment.Configuration;
using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using LiveArch.Transformers;
using Pulumi;
using Pulumi.DockerBuild;
using Structurizr;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Type = System.Type;

namespace LiveArch.Deployment
{
    /// <summary>
    /// Processes a Structurizr deployment view and materializes the corresponding Pulumi resources.
    /// </summary>
    /// <remarks>
    /// This processor coordinates variable substitution, dependency ordering, output-to-input mapping,
    /// loop expansion, and resource registration across nested deployment scopes.
    /// </remarks>
    public partial class StructurizrDeploymentProcessor : IStructurizrDeploymentProcessor
    {
        /// <summary>
        /// Identifies a resource registration by its model node and owning scope.
        /// </summary>
        public readonly record struct ResourceKey(ModelItem Node, int ScopeId);

        private readonly record struct PendingDependency(ModelItem Node);

        /// <summary>
        /// Represents a logical resource scope used to isolate created and referenced resources.
        /// </summary>
        public sealed class ResourceScope(int id, int level, ResourceScope? parentScope, object ownerResource)
        {
            /// <summary>
            /// Gets the unique numeric identifier of the scope.
            /// </summary>
            public int Id { get; } = id;

            /// <summary>
            /// Gets the nesting level of the scope, starting at <c>1</c> for the root scope.
            /// </summary>
            public int Level { get; } = level;

            /// <summary>
            /// Gets the parent scope, or <c>null</c> when this is the root scope.
            /// </summary>
            public ResourceScope? ParentScope { get; } = parentScope;

            /// <summary>
            /// Gets the resource or model object that owns this scope.
            /// </summary>
            public object OwnerResource { get; } = ownerResource;

            /// <summary>
            /// Gets resources created directly in this scope.
            /// </summary>
            public Dictionary<ModelItem, object> CreatedResources { get; } = new();

            /// <summary>
            /// Gets resources referenced directly in this scope.
            /// </summary>
            public Dictionary<ModelItem, object> ReferencedResources { get; } = new();

            /// <summary>
            /// Gets child scopes created beneath this scope.
            /// </summary>
            public List<ResourceScope> ChildScopes { get; } = [];
        }

        /// <summary>
        /// Carries the current scope, resolved variables, and optional loop metadata during processing.
        /// </summary>
        private sealed class DeploymentContext(ResourceScope scope, IReadOnlyDictionary<string, object> variables, DeploymentNode? loopDeploymentNode = null)
        {
            public ResourceScope Scope { get; } = scope;
            public IReadOnlyDictionary<string, object> Variables { get; } = variables;
            public DeploymentNode? LoopDeploymentNode { get; } = loopDeploymentNode;
        }

        /// <summary>
        /// Stores a deferred node together with the context and unresolved dependencies required to resume it.
        /// </summary>
        private sealed class WaitingNodeRegistration(
            IDeploymentAdapter deployNode,
            DeploymentContext context,
            IEnumerable<ModelItem> pendingDependencies)
        {
            public IDeploymentAdapter DeployNode { get; } = deployNode;
            public DeploymentContext Context { get; } = context;
            public HashSet<ModelItem> PendingDependencies { get; } = [.. pendingDependencies];
        }

        private static readonly Regex VarRegex = new(@"\$\{([a-zA-Z0-9_\.\:\-]+)\}", RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));
        private int scopeId;
        private readonly IDeploymentCommandOptions options;
        private readonly IDeploymentVariablesProvider variablesProvider;
        private readonly IResourceHierarchyBuilder resourceHierarchyBuilder;
        private readonly ResourceHierarchyRegistry hierarchyRegistry;
        private readonly ResourceTypesRegistry resourceTypesRegistry;
        private readonly DockerImageReferenceConfigurator dockerImageReferenceConfigurator;
        private readonly IConversionEngine conversionEngine;
        private DeploymentView deploymentView = null!;
        private DeploymentContext rootContext = null!;
        private Workspace workspace = null!;
        private Dictionary<ResourceKey, WaitingNodeRegistration> waitingNodes = new();
        private readonly OutputValueReader outputValueReader = new();
        private readonly InputValueBinder inputValueBinder;
        private readonly ITransformerRegistry transformerRegistry;
        private readonly TransformerPipeline transformerPipeline;
        private readonly IStructurizrDeploymentObserver observer;

        /// <summary>
        /// Gets the root scope created for the current deployment run.
        /// </summary>
        public ResourceScope RootScope => rootContext.Scope;

        /// <summary>
        /// Gets all created resources flattened across the scope tree.
        /// </summary>
        public IReadOnlyDictionary<ResourceKey, object> CreatedResources => FlattenResources(static scope => scope.CreatedResources);

        /// <summary>
        /// Gets all referenced resources flattened across the scope tree.
        /// </summary>
        public IReadOnlyDictionary<ResourceKey, object> ReferencedResources => FlattenResources(static scope => scope.ReferencedResources);

        /// <summary>
        /// Initializes the processor for a specific deployment view and root variable set.
        /// </summary>
        /// <param name="options">Deployment options that identify the workspace, environment, and deployment view.</param>
        /// <param name="variablesProvider">Provider of root variables available during substitution and conversion.</param>
        /// <param name="resourceHierarchyBuilder">Builder that provides parent-to-child property propagation rules.</param>
        /// <param name="resourceTypesRegistry">Registry that maps technology strings to Pulumi resource types or invokes.</param>
        /// <param name="dockerImageReferenceConfigurator">Helper used to map built images into container resource inputs.</param>
        /// <param name="conversionEngine">Conversion engine responsible for typed and named value conversion.</param>
        /// <param name="transformerRegistry">Registry that resolves built-in and custom transformer factories by DSL name.</param>
        /// <param name="observer">Observer that receives resource registration notifications.</param>
        public StructurizrDeploymentProcessor(
            IDeploymentCommandOptions options,
            IDeploymentVariablesProvider variablesProvider,
            IResourceHierarchyBuilder resourceHierarchyBuilder,
            ResourceTypesRegistry resourceTypesRegistry,
            DockerImageReferenceConfigurator dockerImageReferenceConfigurator,
            IConversionEngine conversionEngine,
            ITransformerRegistry transformerRegistry,
            IStructurizrDeploymentObserver observer)
        {
            this.options = options;
            this.variablesProvider = variablesProvider;
            this.resourceHierarchyBuilder = resourceHierarchyBuilder;
            this.hierarchyRegistry = resourceHierarchyBuilder.Registry;
            this.resourceTypesRegistry = resourceTypesRegistry;
            this.dockerImageReferenceConfigurator = dockerImageReferenceConfigurator;
            this.conversionEngine = conversionEngine;
            this.transformerRegistry = transformerRegistry;
            this.transformerPipeline = new TransformerPipeline(this.transformerRegistry);
            this.observer = observer;
            this.inputValueBinder = new InputValueBinder(this);
        }

        /// <summary>
        /// Processes the selected deployment view and creates all resources in dependency order.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for asynchronous resource creation.</param>
        /// <remarks>
        /// Throws when at least one node remains unresolved in the waiting queue after traversal completes.
        /// </remarks>
        public async Task ProcessDeploymentAsync(CancellationToken cancellationToken)
        {
            InitializeProcessingState();

            var rootDeploymentNodes = workspace.Model.DeploymentNodes.On(options.Environment, deploymentView, SubstituteVariables(rootContext));
            foreach (var deployNode in rootDeploymentNodes)
            {
                await ProcessDeploymentNodeAsync(deployNode, rootContext, cancellationToken);
            }

            if (waitingNodes.Count > 0)
            {
                var unresolvedNodes = string.Join(", ", waitingNodes.Keys.Select(x => x.Node.ToString()));
                throw new InvalidOperationException($"Unable to resolve resource dependencies for nodes: {unresolvedNodes}");
            }
        }

        private void InitializeProcessingState()
        {
            scopeId = 0;
            waitingNodes = new Dictionary<ResourceKey, WaitingNodeRegistration>();

            var json = new FileInfo(options.WorkspacePath);
            workspace = WorkspaceUtils.LoadWorkspaceFromJson(json);
            deploymentView = workspace.Views.DeploymentViews.FirstOrDefault(v => v.Key == options.Deployment)
                ?? throw new InvalidOperationException($"Deployment '{options.Deployment}' was not found in the current workspace.");
            rootContext = CreateDeploymentContext(CreateScope(null, workspace), variablesProvider.GetVariables());
        }

        /// <summary>
        /// Resolves variable placeholders in the provided value using the current deployment context.
        /// </summary>
        /// <param name="input">Raw value that can contain <c>${...}</c> placeholders.</param>
        /// <param name="context">Current scope and variable set used for substitution.</param>
        /// <returns>The resolved value or the original non-template object when the whole input matches a variable.</returns>
        private object SubstituteVariables(string input, DeploymentContext context)
        {
            var direct = context.Variables.FirstOrDefault(kv => input == $"${{{kv.Key}}}");
            if (direct.Key != null)
            {
                return direct.Value;
            }

            object result = input;
            var outputReplacementCount = 0;
            foreach (Match match in VarRegex.Matches(input))
            {
                var name = match.Groups[1].Value;

                if (!context.Variables.TryGetValue(name, out var value))
                {
                    throw new InvalidOperationException($"Variable '${{{name}}}' is not defined.");
                }

                var replacement = conversionEngine.ConvertValue(typeof(string), value);
                if (ConversionTypeHelpers.IsOutput(replacement.GetType()))
                {
                    outputReplacementCount++;
                    if (outputReplacementCount > 1)
                    {
                        throw new NotSupportedException($"String interpolation with more than one Output-backed variable is not supported: '{input}'.");
                    }

                    var currentTemplate = result;
                    var replacementOutputType = replacement.GetType();
                    var replacementInnerType = replacementOutputType.GetGenericArguments()[0];
                    result = ConversionTypeHelpers.ProjectOutput(
                        replacement,
                        replacementInnerType,
                        typeof(string),
                        resolvedReplacement => ReplaceTemplatePlaceholder(currentTemplate, match.Value, resolvedReplacement));
                    continue;
                }

                result = ReplaceTemplatePlaceholder(result, match.Value, replacement);
            }

            return result;
        }

        private static object ReplaceTemplatePlaceholder(object template, string placeholder, object? replacement)
        {
            var replacementText = replacement?.ToString() ?? string.Empty;
            if (template is string templateText)
            {
                return templateText.Replace(placeholder, replacementText);
            }

            if (ConversionTypeHelpers.IsOutput(template.GetType()))
            {
                var templateType = template.GetType();
                var templateInnerType = templateType.GetGenericArguments()[0];
                return ConversionTypeHelpers.ProjectOutput(
                    template,
                    templateInnerType,
                    typeof(string),
                    currentTemplate => (currentTemplate?.ToString() ?? string.Empty).Replace(placeholder, replacementText));
            }

            throw new InvalidOperationException($"Unsupported template value type '{template.GetType().FullName}'.");
        }

        /// <summary>
        /// Creates a reusable variable substitution delegate bound to the given deployment context.
        /// </summary>
        /// <param name="context">Context that provides variables and scope metadata.</param>
        /// <returns>A delegate that resolves placeholders for string values.</returns>
        private Func<string, object> SubstituteVariables(DeploymentContext context)
        {
            return s => SubstituteVariables(s, context);
        }

        /// <summary>
        /// Returns relationships for the node that are visible in the active deployment view.
        /// </summary>
        /// <param name="deployNode">Source deployment adapter.</param>
        /// <returns>Filtered relationship adapters for the current view.</returns>
        private IEnumerable<RelationshipAdapter> GetRelationshipAdapters(IDeploymentAdapter deployNode)
        {
            return deployNode.Relationships.In(deploymentView);
        }

        /// <summary>
        /// Checks whether the relationship maps a source output into a target input.
        /// </summary>
        /// <param name="relationship">Relationship to inspect.</param>
        /// <returns><c>true</c> when both <c>source</c> and <c>target</c> properties are present.</returns>
        private static bool HasMappedDependency(RelationshipAdapter relationship)
        {
            return relationship.Properties.ContainsKey("source") &&
                relationship.Properties.ContainsKey("target");
        }

        /// <summary>
        /// Checks whether the relationship explicitly declares a dependency through the <c>dependsOn</c> property.
        /// </summary>
        /// <param name="relationship">Relationship to inspect.</param>
        /// <param name="context">Context used to resolve templated property values.</param>
        /// <returns><c>true</c> when the relationship explicitly requires dependency ordering.</returns>
        private bool HasExplicitDependency(RelationshipAdapter relationship, DeploymentContext context)
        {
            if (!relationship.Properties.TryGetValue("dependsOn", out var dependsOnValue))
            {
                return false;
            }

            return bool.TryParse(SubstituteVariables(dependsOnValue, context).ToString(), out var dependsOn) &&
                dependsOn;
        }

        /// <summary>
        /// Determines whether a regular node must wait for the relationship destination to exist.
        /// </summary>
        /// <param name="relationship">Relationship associated with the node.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns><c>true</c> when the relationship affects node creation order.</returns>
        private bool RequiresNodeDependency(RelationshipAdapter relationship, DeploymentContext context)
        {
            return HasMappedDependency(relationship) ||
                HasExplicitDependency(relationship, context);
        }

        /// <summary>
        /// Determines whether a relationship resource itself must wait for prerequisite resources.
        /// </summary>
        /// <param name="relationship">Relationship resource candidate.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns><c>true</c> when the relationship requires ordered creation.</returns>
        private bool RequiresRelationshipDependency(RelationshipAdapter relationship, DeploymentContext context)
        {
            return !string.IsNullOrEmpty(relationship.Technology) ||
                RequiresNodeDependency(relationship, context);
        }

        /// <summary>
        /// Wraps a Structurizr deployment node and creates it when it is enabled.
        /// </summary>
        /// <param name="deployNode">Deployment node from the workspace model.</param>
        /// <param name="context">Current deployment scope and variables.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        private async Task ProcessDeploymentNodeAsync(DeploymentNode deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            var deploymentNode = new DeploymentNodeAdapter(deployNode, SubstituteVariables(context));
            if (deploymentNode.IsDisabled == false)
            {
                await CreateNodeAsync(deploymentNode, context, cancellationToken);
            }
        }

        /// <summary>
        /// Creates all child resources for a deployment node in the expected order.
        /// </summary>
        /// <param name="deployNode">Parent deployment node whose children are being processed.</param>
        /// <param name="infraNodes">Infrastructure nodes already filtered for the current context.</param>
        /// <param name="context">Current deployment scope and variables.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        /// <remarks>
        /// Infrastructure nodes are created before container instances and nested deployment nodes.
        /// </remarks>
        private async Task CreateChildResources(DeploymentNode deployNode, IReadOnlyCollection<InfrastructureNodeAdapter> infraNodes, DeploymentContext context, CancellationToken cancellationToken)
        {
            foreach (var infraNode in infraNodes)
            {
                await CreateNodeAsync(infraNode, context, cancellationToken);
            }

            foreach (var containerInstance in deployNode.ContainerInstances.On(options.Environment, deploymentView, SubstituteVariables(context)))
            {
                await ProcessContainerInstanceAsync(containerInstance!, context, cancellationToken);
            }

            foreach (var childNode in deployNode.Children)
            {
                await ProcessDeploymentNodeAsync(childNode!, context, cancellationToken);
            }
        }

        /// <summary>
        /// Creates a container instance resource when the instance is enabled.
        /// </summary>
        /// <param name="containerInstance">Container instance from the deployment model.</param>
        /// <param name="context">Current deployment scope and variables.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        private async Task ProcessContainerInstanceAsync(ContainerInstance containerInstance, DeploymentContext context, CancellationToken cancellationToken)
        {
            var container = new ContainerInstanceAdapter(containerInstance, SubstituteVariables(context));
            if (container.IsDisabled == false)
            {
                await CreateNodeAsync(container, context, cancellationToken);
            }
        }

        /// <summary>
        /// Ensures the image required by a container instance is created before the instance itself.
        /// </summary>
        /// <param name="containerInstance">Container instance whose image must be prepared.</param>
        /// <param name="context">Current deployment scope and variables.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        private async Task BuildContainerInstance(ContainerInstance containerInstance, DeploymentContext context, CancellationToken cancellationToken)
        {
            await CreateNodeAsync(new ContainerBuildAdapter(containerInstance.Container, SubstituteVariables(context)), context, cancellationToken);
        }

        /// <summary>
        /// Creates or resolves a resource for the supplied deployment adapter.
        /// </summary>
        /// <param name="deployNode">Deployment adapter describing the target model item.</param>
        /// <param name="context">Current deployment scope and variable bag.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        /// <returns>The created or existing resource, or <c>null</c> when creation is deferred.</returns>
        /// <remarks>
        /// This method handles dependency waiting, invoke-based resources, custom resources, relation resources, and loop-aware relation replication.
        /// </remarks>
        private async Task<object?> CreateNodeAsync(IDeploymentAdapter deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            if (TryGetExistingResourceByNode(deployNode.Node, context.Scope, out var existingResource))
            {
                return existingResource;
            }

            if (resourceTypesRegistry.TryGetResourceType(deployNode.Technology, out var type))
            {
                if (TryWaitForDependencies(deployNode, context))
                {
                    return null;
                }

                RemoveWaitingNode(new ResourceKey(deployNode.Node, context.Scope.Id));
                await PreProcessNodeAsync(deployNode, context, cancellationToken);

                if (type!.IsAbstract && type.IsSealed)
                {
                    if (resourceTypesRegistry.TryGetInvokeMethod(deployNode.Technology, out var invoke))
                    {
                        var paramType = invoke!.GetParameters().First();
                        var param = Activator.CreateInstance(paramType.ParameterType)!;
                        var paramInputProps = inputValueBinder.GetInputProps(paramType.ParameterType);

                        foreach (var parent in deployNode.Parents)
                        {
                            PropagateParentProperties(parent, param, paramInputProps, context);
                        }

                        ApplyRelations(deployNode, param, context);

                        foreach ((var propName, var propVal) in deployNode.Properties)
                        {
                            inputValueBinder.SetProperty(param, propName, propVal, paramInputProps, context, parseInlineTransformers: true);
                        }

                        var resource = invoke.Invoke(null, [param, null!])!;

                        AddReferencedResource(deployNode.Node, context.Scope, resource!);

                        await CreateRelationNodesAsync(deployNode, context, cancellationToken);
                        await CreateIncomingLoopRelationNodesAsync(deployNode, context, cancellationToken);
                        await PostProcessNodeAsync(deployNode, resource, context, cancellationToken);
                        await TryResumeWaitingNodesAsync(deployNode.Node, cancellationToken);

                        return resource;
                    }
                }
                else
                {
                    var paramType = type.GetConstructors()[0].GetParameters()[1];
                    var param = Activator.CreateInstance(paramType.ParameterType)!;
                    var paramInputProps = inputValueBinder.GetInputProps(paramType.ParameterType);

                    foreach (var parent in deployNode.Parents)
                    {
                        PropagateParentProperties(parent, param, paramInputProps, context);
                    }

                    ApplyRelations(deployNode, param, context);

                    foreach ((var propName, var propVal) in deployNode.Properties)
                    {
                        inputValueBinder.SetProperty(param, propName, propVal, paramInputProps, context, parseInlineTransformers: true);
                    }

                    if (!deployNode.Properties.TryGetValue("var", out var resVar) &&
                        (!deployNode.Properties.TryGetValue("structurizr.dsl.identifier", out resVar) || Guid.TryParse(resVar, out _)) &&
                        !deployNode.Properties.TryGetValue("name", out resVar))
                    {
                        if (deployNode.Node is Element element)
                        {
                            resVar = element.Name;
                        }
                        else if (deployNode.Node is Relationship rel)
                        {
                            resVar = rel.Description;
                        }
                        else
                        {
                            throw new InvalidOperationException($"Cannot determine resource identifier for node {deployNode.Node}. Please specify 'var' property or assign to a variable.");
                        }
                    }

                    var newRes = Activator.CreateInstance(type, [SubstituteVariables(resVar, context), param, null!]);
                    AddCreatedResource(deployNode.Node, context.Scope, newRes!);

                    await CreateRelationNodesAsync(deployNode, context, cancellationToken);
                    await CreateIncomingLoopRelationNodesAsync(deployNode, context, cancellationToken);
                    await PostProcessNodeAsync(deployNode, newRes, context, cancellationToken);
                    await TryResumeWaitingNodesAsync(deployNode.Node, cancellationToken);

                    return newRes;
                }
            }
            return null;
        }

        /// <summary>
        /// Puts the node into the waiting queue when one or more required dependencies are still missing.
        /// </summary>
        /// <param name="deployNode">Node that may need to wait.</param>
        /// <param name="context">Current deployment scope and variables.</param>
        /// <returns><c>true</c> when the node was deferred; otherwise <c>false</c>.</returns>
        private bool TryWaitForDependencies(IDeploymentAdapter deployNode, DeploymentContext context)
        {
            var missingDependencies = GetMissingDependencies(deployNode, context);
            if (missingDependencies.Count == 0)
            {
                return false;
            }

            RegisterWaitingNode(deployNode, context, missingDependencies);
            return true;
        }

        /// <summary>
        /// Collects dependencies that must exist before the node can be created in the current scope.
        /// </summary>
        /// <param name="deployNode">Node whose prerequisites are being evaluated.</param>
        /// <param name="context">Current deployment scope and variables.</param>
        /// <returns>A distinct set of missing model items.</returns>
        private IReadOnlyCollection<ModelItem> GetMissingDependencies(IDeploymentAdapter deployNode, DeploymentContext context)
        {
            var missingDependencies = new HashSet<ModelItem>();

            foreach (var relationship in GetRelationshipAdapters(deployNode))
            {
                var requiresDependency = deployNode is RelationshipAdapter
                    ? RequiresRelationshipDependency(relationship, context)
                    : RequiresNodeDependency(relationship, context);

                if (!requiresDependency)
                {
                    continue;
                }

                var relation = (Relationship)relationship.Node;
                if (!TryGetExistingResourceByNode(relation.Destination, context.Scope, out _))
                {
                    missingDependencies.Add(relation.Destination);
                }
            }

            if (deployNode is RelationshipAdapter)
            {
                foreach (var parentNode in deployNode.Parents)
                {
                    if (!TryGetExistingResourceByNode(parentNode.Node, context.Scope, out _))
                    {
                        missingDependencies.Add(parentNode.Node);
                    }
                }
            }

            if (deployNode.Node is DeploymentNode deploymentNode && deployNode.Technology == ForEachLoop.Technology)
            {
                var sourceNode = GetSourceNode(deploymentNode, context);

                if (sourceNode != null)
                {
                    foreach (var dependency in GetMissingDependencies(sourceNode, context))
                    {
                        missingDependencies.Add(dependency);
                    }
                }
            }

            return missingDependencies;
        }

        /// <summary>
        /// Finds the active <c>foreach:source</c> infrastructure node for the specified loop node.
        /// </summary>
        /// <param name="deploymentNode">Deployment node that may represent a foreach loop.</param>
        /// <param name="context">Current deployment context used for variable substitution.</param>
        /// <returns>The active source adapter, or <c>null</c> when none exists.</returns>
        private InfrastructureNodeAdapter? GetSourceNode(DeploymentNode deploymentNode, DeploymentContext context)
        {
            return deploymentNode.InfrastructureNodes
                .Where(x => x.Technology == ForEachSource.Technology)
                .Select(x => new InfrastructureNodeAdapter(x, SubstituteVariables(context)))
                .Where(x => x.IsDisabled == false)
                .FirstOrDefault();
        }

        /// <summary>
        /// Stores a deferred node together with the dependencies that still need to be created.
        /// </summary>
        /// <param name="deployNode">Deferred node.</param>
        /// <param name="context">Scope in which the node must eventually be created.</param>
        /// <param name="missingDependencies">Dependencies still missing for the node.</param>
        private void RegisterWaitingNode(IDeploymentAdapter deployNode, DeploymentContext context, IReadOnlyCollection<ModelItem> missingDependencies)
        {
            var key = new ResourceKey(deployNode.Node, context.Scope.Id);

            var waitingNode = new WaitingNodeRegistration(deployNode, context, missingDependencies);
            waitingNodes[key] = waitingNode;
        }

        /// <summary>
        /// Removes a deferred node registration from the waiting queue.
        /// </summary>
        /// <param name="key">Key that uniquely identifies the deferred node in a scope.</param>
        private void RemoveWaitingNode(ResourceKey key)
        {
            waitingNodes.Remove(key);
        }

        /// <summary>
        /// Removes stale waiting registrations for the same model item from ancestor scopes.
        /// </summary>
        /// <param name="node">Model item whose ancestor waiters should be cleared.</param>
        /// <param name="scope">Current scope that already owns the realized item.</param>
        /// <remarks>
        /// This is primarily used for loop-scoped relationship resources that should not remain pending in outer scopes.
        /// </remarks>
        private void RemoveAncestorWaitingNodes(ModelItem node, ResourceScope scope)
        {
            for (var currentScope = scope.ParentScope; currentScope != null; currentScope = currentScope.ParentScope)
            {
                waitingNodes.Remove(new ResourceKey(node, currentScope.Id));
            }
        }

        /// <summary>
        /// Rechecks deferred nodes after a new resource has been created.
        /// </summary>
        /// <param name="createdNode">Model item that has just been materialized.</param>
        /// <param name="cancellationToken">Cancellation token for resumed asynchronous work.</param>
        private async Task TryResumeWaitingNodesAsync(ModelItem createdNode, CancellationToken cancellationToken)
        {
            var waitersToResume = new List<WaitingNodeRegistration>();
            foreach (var waiter in waitingNodes.ToList())
            {
                var matchingDependencies = waiter.Value.PendingDependencies
                    .Where(node => node == createdNode && TryGetExistingResourceByNode(node, waiter.Value.Context.Scope, out _))
                    .ToList();

                if (matchingDependencies.Count == 0)
                {
                    continue;
                }

                foreach (var dependency in matchingDependencies)
                {
                    waiter.Value.PendingDependencies.Remove(dependency);
                }

                if (waiter.Value.PendingDependencies.Count > 0)
                {
                    continue;
                }

                waitingNodes.Remove(waiter.Key);
                waitersToResume.Add(waiter.Value);
            }

            foreach (var waiter in waitersToResume)
            {
                await CreateNodeAsync(waiter.DeployNode, waiter.Context, cancellationToken);
            }
        }

        /// <summary>
        /// Performs preparation steps that must happen before the node resource is instantiated.
        /// </summary>
        /// <param name="deployNode">Node being created.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        private async Task PreProcessNodeAsync(IDeploymentAdapter deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            switch (deployNode)
            {
                case ContainerInstanceAdapter when deployNode.Node is ContainerInstance containerInstance:
                    await BuildContainerInstance(containerInstance, context, cancellationToken);
                    break;
            }
        }

        /// <summary>
        /// Performs follow-up actions after a node resource has been created.
        /// </summary>
        /// <param name="deployNode">Node that was just created.</param>
        /// <param name="resource">Created Pulumi resource or invoke result.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        private async Task PostProcessNodeAsync(IDeploymentAdapter deployNode, object? resource, DeploymentContext context, CancellationToken cancellationToken)
        {
            switch (deployNode)
            {
                case DeploymentNodeAdapter when deployNode.Node is DeploymentNode deploymentNode:
                    await PostProcessDeploymentNodeAsync(deploymentNode, resource, context, cancellationToken);
                    break;
            }
        }

        /// <summary>
        /// Continues processing for deployment-node resources, including loop expansion and child creation.
        /// </summary>
        /// <param name="deploymentNode">Deployment node that owns the created resource.</param>
        /// <param name="resource">Created resource instance.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        private async Task PostProcessDeploymentNodeAsync(DeploymentNode deploymentNode, object? resource, DeploymentContext context, CancellationToken cancellationToken)
        {
            if (resource is ForEachLoop loop)
            {
                await ProcessForEachLoopAsync(deploymentNode, loop, context, cancellationToken);
                return;
            }

            await CreateChildResources(deploymentNode,
                GetInfrastructureNodes(deploymentNode, context), context, cancellationToken);
        }

        /// <summary>
        /// Expands a foreach loop by creating a child scope for each source item.
        /// </summary>
        /// <param name="deploymentNode">Deployment node that represents the loop definition.</param>
        /// <param name="loop">Created loop control resource.</param>
        /// <param name="context">Parent deployment context.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        /// <remarks>
        /// Each iteration inherits the parent variables and additionally exposes the loop item under the loop name.
        /// </remarks>
        private async Task ProcessForEachLoopAsync(DeploymentNode deploymentNode, ForEachLoop loop, DeploymentContext context, CancellationToken cancellationToken)
        {
            var sourceNode = GetSourceNode(deploymentNode, context);
            if (sourceNode == null || await CreateNodeAsync(sourceNode, context, cancellationToken) is not ForEachSource sourceComponent)
            {
                throw new Exception($"ForEach loop '{loop.Name}' is missing an active source element with technology '{ForEachSource.Technology}'");
            }

            sourceComponent.Source.Apply(async items =>
            {
                foreach (var item in items)
                {
                    var loopContext = CreateChildContext(context.Scope, loop, context.Variables, variables => variables[loop.Name] = item, deploymentNode);
                    await CreateChildResources(deploymentNode,
                        GetInfrastructureNodes(deploymentNode, loopContext, x => x.Technology != ForEachSource.Technology), loopContext, cancellationToken);
                }
            });
        }

        /// <summary>
        /// Creates a child deployment context with a fresh scope and optionally extended variables.
        /// </summary>
        /// <param name="parentScope">Parent scope for the new context.</param>
        /// <param name="ownerResource">Resource that owns the child scope.</param>
        /// <param name="parentVariables">Variables inherited from the parent context.</param>
        /// <param name="configureVariables">Optional callback used to add or override variables.</param>
        /// <param name="loopDeploymentNode">Loop node associated with the child scope, when applicable.</param>
        /// <returns>A new deployment context for nested resource creation.</returns>
        private DeploymentContext CreateChildContext(ResourceScope parentScope, object ownerResource, IReadOnlyDictionary<string, object> parentVariables, Action<Dictionary<string, object>>? configureVariables = null, DeploymentNode? loopDeploymentNode = null)
        {
            var scope = CreateScope(parentScope, ownerResource);
            var variables = new Dictionary<string, object>(parentVariables);
            configureVariables?.Invoke(variables);
            return CreateDeploymentContext(scope, variables, loopDeploymentNode);
        }

        /// <summary>
        /// Enumerates active infrastructure nodes for a deployment node.
        /// </summary>
        /// <param name="deploymentNode">Deployment node that contains infrastructure children.</param>
        /// <param name="context">Context used for variable substitution and disable checks.</param>
        /// <param name="predicate">Optional filter applied before adapters are created.</param>
        /// <returns>Active infrastructure adapters for the current context.</returns>
        private IReadOnlyCollection<InfrastructureNodeAdapter> GetInfrastructureNodes(DeploymentNode deploymentNode, DeploymentContext context, Func<InfrastructureNode, bool>? predicate = null)
        {
            var infraNodes = deploymentNode.InfrastructureNodes.AsEnumerable();

            if (predicate != null)
            {
                infraNodes = infraNodes.Where(predicate);
            }

            var infrastructureNodes = infraNodes
                .Select(x => new InfrastructureNodeAdapter(x, SubstituteVariables(context)))
                .Where(x => x.IsDisabled == false);

            return [.. infrastructureNodes];
        }

        /// <summary>
        /// Builds value transformers declared on relationship properties.
        /// </summary>
        /// <param name="properties">Relationship properties that may declare transformer arguments.</param>
        /// <returns>The instantiated transformer pipeline.</returns>
        private IReadOnlyCollection<ITransformer> GetTransformers(Dictionary<string, string> properties, DeploymentContext context)
        {
            var transformers = new List<ITransformer>();
            foreach (var (name, param) in properties)
            {
                var paramValue = SubstituteVariables(param, context);
                if (paramValue is string paramStr && transformerRegistry.TryCreate(name, paramStr, out var transformer))
                {
                    transformers.Add(transformer);
                }
            }
            return transformers;
        }

        /// <summary>
        /// Extracts an optional named converter identifier from relationship properties.
        /// </summary>
        /// <param name="properties">Relationship properties to inspect.</param>
        /// <returns>The normalized converter name, or <c>null</c> when no converter is configured.</returns>
        private static string? GetConverterName(IDictionary<string, string> properties)
        {
            return properties.TryGetValue("converter", out var converterName) && !string.IsNullOrWhiteSpace(converterName)
                ? converterName.Trim()
                : null;
        }

        /// <summary>
        /// Creates relationship resources owned by the supplied node in the current scope.
        /// </summary>
        /// <param name="deployNode">Owner node whose outgoing relationship resources should be created.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        /// <remarks>
        /// Loop-replicated incoming relationships are intentionally excluded here and handled separately.
        /// </remarks>
        private async Task CreateRelationNodesAsync(IDeploymentAdapter deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            if (deployNode is not RelationshipAdapter)
            {
                foreach (var relNode in GetRelationshipAdapters(deployNode)
                    .Where(relationship => !string.IsNullOrEmpty(relationship.Technology))
                    .Where(relationship => ShouldCreateRelationInCurrentScope(relationship) == false))
                {
                    await CreateNodeAsync(relNode, context, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Recreates incoming relationship resources for elements materialized inside a foreach iteration.
        /// </summary>
        /// <param name="deployNode">Node created inside the current loop scope.</param>
        /// <param name="context">Current loop-aware deployment context.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        /// <remarks>
        /// This method supports scenarios where the relationship source is outside the loop and the destination is inside the loop.
        /// </remarks>
        private async Task CreateIncomingLoopRelationNodesAsync(IDeploymentAdapter deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            if (context.LoopDeploymentNode == null || deployNode is RelationshipAdapter)
            {
                return;
            }

            foreach (var relNode in GetIncomingLoopRelationshipAdapters(deployNode, context))
            {
                await CreateNodeAsync(relNode, context, cancellationToken);
                RemoveAncestorWaitingNodes(relNode.Node, context.Scope);
            }
        }

        /// <summary>
        /// Finds incoming relationship resources that should be replayed inside the current loop scope.
        /// </summary>
        /// <param name="deployNode">Node created in the loop scope.</param>
        /// <param name="context">Current deployment context that carries loop metadata.</param>
        /// <returns>Incoming relationship adapters that should be created in the current iteration.</returns>
        private IEnumerable<RelationshipAdapter> GetIncomingLoopRelationshipAdapters(IDeploymentAdapter deployNode, DeploymentContext context)
        {
            if (context.LoopDeploymentNode == null || deployNode.Node is not Element element)
            {
                return Array.Empty<RelationshipAdapter>();
            }

            return workspace.Model.Relationships
                .Where(relationship =>
                    ReferenceEquals(relationship.Destination, element) &&
                    !IsDescendantOf(relationship.Source, context.LoopDeploymentNode))
                .Select(relationship => new RelationshipAdapter(relationship, SubstituteVariables(context)))
                .Where(relationship => !relationship.IsDisabled && !string.IsNullOrEmpty(relationship.Technology))
                .In(deploymentView);
        }

        /// <summary>
        /// Checks whether the element belongs to the subtree rooted at the specified deployment node.
        /// </summary>
        /// <param name="element">Element to test.</param>
        /// <param name="ancestor">Potential ancestor deployment node.</param>
        /// <returns><c>true</c> when the element is inside the ancestor subtree.</returns>
        private static bool IsDescendantOf(Element element, DeploymentNode ancestor)
        {
            for (Element? current = element; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Decides whether a relationship resource should be skipped in the current outer scope.
        /// </summary>
        /// <param name="relationship">Relationship resource candidate.</param>
        /// <returns><c>true</c> when the relationship must instead be created inside a loop iteration scope.</returns>
        private static bool ShouldCreateRelationInCurrentScope(RelationshipAdapter relationship)
        {
            var relation = (Relationship)relationship.Node;
            if (relation.Source is not Element source || relation.Destination is not Element destination)
            {
                return false;
            }

            var loopAncestor = GetLoopAncestor(destination);
            return loopAncestor != null && !IsDescendantOf(source, loopAncestor);
        }

        /// <summary>
        /// Returns the nearest foreach loop ancestor for the element, if any.
        /// </summary>
        /// <param name="element">Element whose ancestry should be inspected.</param>
        /// <returns>The nearest loop deployment node or <c>null</c>.</returns>
        private static DeploymentNode? GetLoopAncestor(Element element)
        {
            for (Element? current = element; current != null; current = current.Parent)
            {
                if (current is DeploymentNode deploymentNode && deploymentNode.Technology == ForEachLoop.Technology)
                {
                    return deploymentNode;
                }
            }

            return null;
        }

        /// <summary>
        /// Applies relationship-driven input mappings and container image references to a resource argument object.
        /// </summary>
        /// <param name="deployNode">Node whose outgoing relationships supply input values.</param>
        /// <param name="param">Target argument object being populated.</param>
        /// <param name="context">Current deployment context.</param>
        private void ApplyRelations(IDeploymentAdapter deployNode, object param, DeploymentContext context)
        {
            foreach (var relationship in GetRelationshipAdapters(deployNode))
            {
                var relation = (Relationship)relationship.Node;
                if (relationship.Properties.TryGetValue("source", out var sourcePath) &&
                    relationship.Properties.TryGetValue("target", out var targetPath) &&
                    TryGetResourceByNode(relation.Destination, context, out var source))
                {
                    ApplyDependency(
                        source!,
                        param,
                        sourcePath,
                        targetPath,
                        context,
                        GetTransformers(new Dictionary<string, string>(relationship.Properties), context),
                        GetConverterName(relationship.Properties));
                }
            }

            if (deployNode.Node is ContainerInstance ci && TryGetExistingResourceByNode(ci.Container, context.Scope, out var image) && image is Image dockerImage)
            {
                if (dockerImageReferenceConfigurator.TryGetImageReference(param, dockerImage, out var dockerImageRef))
                {
                    inputValueBinder.SetProperty(param, dockerImageRef!.ResourceImagePropertyPath, dockerImageRef!.ImageRef, inputValueBinder.GetInputProps(param.GetType()), context);
                }
            }
        }

        /// <summary>
        /// Propagates hierarchy-derived values from parent resources into a child argument object.
        /// </summary>
        /// <param name="deployNode">Parent node whose resource outputs may be propagated.</param>
        /// <param name="param">Target argument object receiving propagated values.</param>
        /// <param name="paramInputProps">Cached writable input properties for the target argument object.</param>
        /// <param name="context">Current deployment context.</param>
        private void PropagateParentProperties(IDeploymentAdapter deployNode, object param, Dictionary<string, PropertyInfo> paramInputProps, DeploymentContext context)
        {
            foreach (var parent in deployNode.Parents)
            {
                PropagateParentProperties(parent, param, paramInputProps, context);
            }

            if (!TryGetResourceByNode(deployNode.Node, context, out var resource))
            {
                return;
            }

            var resourceType = resource!.GetType();
            if (ConversionTypeHelpers.IsOutput(resourceType))
            {
                var innerType = resourceType.GetGenericArguments()[0];
                if (!hierarchyRegistry.TryGetValue(innerType, out var outputRules))
                {
                    return;
                }

                foreach (var rule in outputRules)
                {
                    var value = ConversionTypeHelpers.ProjectOutput(resource, innerType, typeof(object), current => rule.ParentOutputProperty(current));
                    foreach (var targetProp in rule.TargetInputProperties)
                    {
                        inputValueBinder.SetProperty(param, targetProp, value, paramInputProps, context);
                    }
                }

                return;
            }

            if (hierarchyRegistry.TryGetValue(resourceType, out var rules))
            {
                foreach (var rule in rules)
                {
                    var value = rule.ParentOutputProperty(resource);
                    if (value != null)
                    {
                        foreach (var targetProp in rule.TargetInputProperties)
                        {
                            inputValueBinder.SetProperty(param, targetProp, value, paramInputProps, context);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Resolves a resource for the specified model item within the current context.
        /// </summary>
        /// <param name="node">Model item whose resource is required.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="resource">Resolved resource instance when available.</param>
        /// <returns><c>true</c> when the resource exists or can be safely ignored; otherwise an exception is thrown.</returns>
        /// <remarks>
        /// Disabled elements and static structure elements are treated as non-fatal misses.
        /// </remarks>
        private bool TryGetResourceByNode(ModelItem node, DeploymentContext context, out object? resource)
        {
            if (!TryGetExistingResourceByNode(node, context.Scope, out resource))
            {
                if (node is StaticStructureElement)
                {
                    return false;
                }

                if (node is Element element && new ElementAdapter(element, SubstituteVariables(context)).IsDisabled)
                {
                    return false;
                }

                throw new InvalidOperationException($"Resource for node {node} is out of scope");
            }

            return true;
        }

        /// <summary>
        /// Resolves a resource by walking the current scope and its ancestors.
        /// </summary>
        /// <param name="node">Model item to resolve.</param>
        /// <param name="scope">Scope where lookup starts.</param>
        /// <param name="resource">Resolved resource when found.</param>
        /// <returns><c>true</c> when the resource is visible from the supplied scope.</returns>
        private bool TryGetExistingResourceByNode(ModelItem node, ResourceScope scope, out object? resource)
        {
            for (var currentScope = scope; currentScope != null; currentScope = currentScope.ParentScope)
            {
                if (currentScope.ReferencedResources.TryGetValue(node, out resource) ||
                    currentScope.CreatedResources.TryGetValue(node, out resource))
                {
                    return true;
                }
            }

            resource = null;
            return false;
        }

        /// <summary>
        /// Creates a new resource scope and attaches it to the optional parent scope.
        /// </summary>
        /// <param name="parentScope">Parent scope, or <c>null</c> for the root.</param>
        /// <param name="ownerResource">Resource that owns the new scope.</param>
        /// <returns>The newly created scope.</returns>
        private ResourceScope CreateScope(ResourceScope? parentScope, object ownerResource)
        {
            var createdScope = new ResourceScope(scopeId++, (parentScope?.Level ?? 0) + 1, parentScope, ownerResource);
            parentScope?.ChildScopes.Add(createdScope);
            return createdScope;
        }

        /// <summary>
        /// Creates a deployment context for the supplied scope and variable set.
        /// </summary>
        /// <param name="scope">Resource scope represented by the context.</param>
        /// <param name="variables">Variables available inside the context.</param>
        /// <param name="loopDeploymentNode">Loop node associated with the context, when applicable.</param>
        /// <returns>A deployment context augmented with scope metadata.</returns>
        private static DeploymentContext CreateDeploymentContext(ResourceScope scope, IReadOnlyDictionary<string, object> variables, DeploymentNode? loopDeploymentNode = null)
        {
            var contextVariables = new Dictionary<string, object>(variables)
            {
                ["level"] = scope.Level
            };

            return new DeploymentContext(scope, contextVariables, loopDeploymentNode);
        }

        /// <summary>
        /// Stores a newly created resource in the current scope.
        /// </summary>
        /// <param name="node">Model item represented by the resource.</param>
        /// <param name="scope">Scope that should own the registration.</param>
        /// <param name="resource">Pulumi resource instance.</param>
        private void AddCreatedResource(ModelItem node, ResourceScope scope, object resource)
        {
            scope.CreatedResources.Add(node, resource);
            observer.OnResourceCreated(node, scope, resource);
        }

        /// <summary>
        /// Stores a referenced resource in the current scope.
        /// </summary>
        /// <param name="node">Model item represented by the resource.</param>
        /// <param name="scope">Scope that should own the registration.</param>
        /// <param name="resource">Referenced resource or invoke result.</param>
        private void AddReferencedResource(ModelItem node, ResourceScope scope, object resource)
        {
            scope.ReferencedResources.Add(node, resource);
            observer.OnResourceReferenced(node, scope, resource);
        }

        /// <summary>
        /// Flattens resources from the scope tree into a dictionary keyed by model item and scope id.
        /// </summary>
        /// <param name="getResources">Selector that chooses created or referenced resources from a scope.</param>
        /// <returns>Flattened resource dictionary.</returns>
        private IReadOnlyDictionary<ResourceKey, object> FlattenResources(Func<ResourceScope, Dictionary<ModelItem, object>> getResources)
        {
            var resources = new Dictionary<ResourceKey, object>();
            FlattenResources(rootContext.Scope, getResources, resources);
            return resources;
        }

        /// <summary>
        /// Recursively collects resources from a scope subtree into a flat result dictionary.
        /// </summary>
        /// <param name="currentScope">Current scope being traversed.</param>
        /// <param name="getResources">Selector that chooses created or referenced resources from a scope.</param>
        /// <param name="resources">Accumulator that receives flattened entries.</param>
        private static void FlattenResources(ResourceScope currentScope, Func<ResourceScope, Dictionary<ModelItem, object>> getResources, Dictionary<ResourceKey, object> resources)
        {
            foreach (var resource in getResources(currentScope))
            {
                resources.Add(new ResourceKey(resource.Key, currentScope.Id), resource.Value);
            }

            foreach (var childScope in currentScope.ChildScopes)
            {
                FlattenResources(childScope, getResources, resources);
            }
        }

        /// <summary>
        /// Applies a transformer pipeline to either a plain value or an <c>Output&lt;T&gt;</c> projection.
        /// </summary>
        /// <param name="value">Source value to transform.</param>
        /// <param name="transformers">Ordered transformer pipeline.</param>
        /// <returns>The transformed plain value or projected output.</returns>
        private static object ApplyTransformers(object value, IReadOnlyCollection<ITransformer> transformers)
        {
            if (transformers.Count == 0)
            {
                return value;
            }

            if (ConversionTypeHelpers.IsOutput(value.GetType()))
            {
                var sourceInnerType = value.GetType().GetGenericArguments()[0];
                var resultType = transformers.Last().OutputType;
                return ConversionTypeHelpers.ProjectOutput(value, sourceInnerType, resultType, current => TransformerPipeline.Apply(current, transformers));
            }

            return TransformerPipeline.Apply(value, transformers);
        }

        /// <summary>
        /// Resolves inline transformer syntax in direct DSL values before conversion.
        /// </summary>
        /// <param name="value">Raw direct value from the DSL or relationship mapping.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="parseInlineTransformers">Whether inline transformer syntax should be parsed.</param>
        /// <returns>The substituted and transformed value, or the original value when no inline pipeline is present.</returns>
        private object PrepareDirectValue(object value, DeploymentContext context, bool parseInlineTransformers)
        {
            if (!parseInlineTransformers || value is not string stringValue)
            {
                return value;
            }

            if (!transformerPipeline.TryParse(stringValue, out var sourceValue, out var transformers))
            {
                return value;
            }

            var substituted = SubstituteVariables(sourceValue, context);
            return ApplyTransformers(substituted, transformers);
        }

        /// <summary>
        /// Reads a value from the source resource, optionally transforms it, and writes it into the target arguments.
        /// </summary>
        /// <param name="source">Resource or invoke result that provides the source value.</param>
        /// <param name="target">Argument object receiving the value.</param>
        /// <param name="sourcePath">Dot-separated output path on the source.</param>
        /// <param name="targetPath">Dot-separated input path on the target.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="transformers">Optional transformer pipeline applied before assignment.</param>
        private void ApplyDependency(object source, object target, string sourcePath, string targetPath, DeploymentContext context, IReadOnlyCollection<ITransformer> transformers, string? converterName = null)
        {
            var value = outputValueReader.GetValue(source, sourcePath);
            if (value == null)
                return;

            var inputProps = inputValueBinder.GetInputProps(target.GetType());
            value = ApplyTransformers(value, transformers);
            inputValueBinder.SetProperty(target, targetPath, value, inputProps, context, parseInlineTransformers: false, converterName: converterName);
        }

        /// <summary>
        /// Converts an arbitrary source value into the target CLR or Pulumi input type.
        /// </summary>
        /// <param name="targetType">Destination type expected by the argument model.</param>
        /// <param name="sourceValue">Raw source value.</param>
        /// <param name="context">Current deployment context used for string substitution.</param>
        /// <returns>The converted value ready for assignment.</returns>
        /// <remarks>
        /// Supports primitives, enums, inputs, input collections, unions, dictionaries, lists, and selected output-to-input conversions.
        /// </remarks>
        private object ConvertValue(Type targetType, object sourceValue, DeploymentContext context, string? converterName = null)
        {
            if (sourceValue is string stringValue)
            {
                sourceValue = SubstituteVariables(stringValue, context);
            }

            return conversionEngine.ConvertValue(targetType, sourceValue, converterName);
        }
    }
}
