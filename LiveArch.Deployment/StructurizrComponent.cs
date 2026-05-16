using LiveArch.Deployment.Adapters;
using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Pulumi;
using Pulumi.DockerBuild;
using Structurizr;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Type = System.Type;

namespace LiveArch.Deployment
{
    public partial class StructurizrComponent
    {
        public readonly record struct ResourceKey(ModelItem Node, int ScopeId);

        private readonly record struct PendingDependency(ModelItem Node);

        public sealed class ResourceScope(int id, int level, ResourceScope? parentScope, object ownerResource)
        {
            public int Id { get; } = id;
            public int Level { get; } = level;
            public ResourceScope? ParentScope { get; } = parentScope;
            public object OwnerResource { get; } = ownerResource;
            public Dictionary<ModelItem, object> CreatedResources { get; } = new();
            public Dictionary<ModelItem, object> ReferencedResources { get; } = new();
            public List<ResourceScope> ChildScopes { get; } = [];
        }

        private sealed class DeploymentContext(ResourceScope scope, IReadOnlyDictionary<string, object> variables, DeploymentNode? loopDeploymentNode = null)
        {
            public ResourceScope Scope { get; } = scope;
            public IReadOnlyDictionary<string, object> Variables { get; } = variables;
            public DeploymentNode? LoopDeploymentNode { get; } = loopDeploymentNode;
        }

        private sealed class WaitingNodeRegistration(
            IDeploymentAdapter deployNode,
            DeploymentContext context,
            IEnumerable<ModelItem> pendingDependencies)
        {
            public IDeploymentAdapter DeployNode { get; } = deployNode;
            public DeploymentContext Context { get; } = context;
            public HashSet<ModelItem> PendingDependencies { get; } = [.. pendingDependencies];
        }

        [GeneratedRegex(@"\$\{([a-zA-Z0-9_\.\:\-]+)\}", RegexOptions.Multiline, 1000)]
        private static partial Regex InterpolationRegex();
        private static readonly Regex VarRegex = InterpolationRegex();
        private int scopeId;
        private readonly string environment;
        private readonly ResourceHierarchyRegistry hierarchyRegistry;
        private readonly ResourceTypesRegistry resourceTypesRegistry;
        private readonly DockerImageReferenceConfigurator dockerImageReferenceConfigurator;
        private readonly DeploymentView deploymentView;
        private readonly DeploymentContext rootContext;
        private Workspace workspace;
        private Dictionary<ResourceKey, WaitingNodeRegistration> waitingNodes = new();
        private Dictionary<object, object> childInputWrappers = new();

        private Dictionary<Type, Dictionary<string, PropertyInfo>> allInputProps = new();
        private readonly Dictionary<Type, Dictionary<string, MemberInfo>> _outputMembersCache = new();

        private readonly PropertyInfo inputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly InvokeOptions? invokeOptions = null;
        private readonly CustomResourceOptions? customResourceOptions = null;

        public ResourceScope RootScope => rootContext.Scope;
        public IReadOnlyDictionary<ResourceKey, object> CreatedResources => FlattenResources(static scope => scope.CreatedResources);
        public IReadOnlyDictionary<ResourceKey, object> ReferencedResources => FlattenResources(static scope => scope.ReferencedResources);

        public StructurizrComponent(
            string workspacePath,
            string environment,
            string deployment,
            IReadOnlyDictionary<string, object> variables,
            ResourceHierarchyRegistry hierarchyRegistry,
            ResourceTypesRegistry resourceTypesRegistry,
            DockerImageReferenceConfigurator dockerImageReferenceConfigurator)
        {
            var json = new FileInfo(workspacePath);
            workspace = WorkspaceUtils.LoadWorkspaceFromJson(json);
            this.environment = environment;
            this.hierarchyRegistry = hierarchyRegistry;
            this.resourceTypesRegistry = resourceTypesRegistry;
            this.dockerImageReferenceConfigurator = dockerImageReferenceConfigurator;
            this.deploymentView = workspace.Views.DeploymentViews.FirstOrDefault(v => v.Key == deployment)
                ?? throw new InvalidOperationException($"Deployment '{deployment}' was not found in the current workspace.");

            rootContext = CreateDeploymentContext(CreateScope(null, workspace), variables);
        }

        /// <summary>
        /// Processes the selected deployment view and creates all resources in dependency order.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for asynchronous resource creation.</param>
        /// <remarks>
        /// Throws when at least one node remains unresolved in the waiting queue after traversal completes.
        /// </remarks>
        public async Task ProcessWorkspaceAsync(CancellationToken cancellationToken)
        {
            var rootDeploymentNodes = workspace.Model.DeploymentNodes.On(environment, deploymentView, SubstituteVariables(rootContext));
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
            return VarRegex.Replace(input, match =>
            {
                var name = match.Groups[1].Value;

                if (!context.Variables.TryGetValue(name, out var value))
                {
                    throw new InvalidOperationException($"Variable '${{{name}}}' is not defined.");
                }

                return (string)ConvertValue(typeof(string), value, context);
            });
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

            foreach (var containerInstance in deployNode.ContainerInstances.On(environment, deploymentView, SubstituteVariables(context)))
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
                        var paramInputProps = GetInputProps(paramType.ParameterType);

                        foreach (var parent in deployNode.Parents)
                        {
                            PropagateParentProperties(parent, param, paramInputProps, context);
                        }

                        ApplyRelations(deployNode, param, context);

                        foreach ((var propName, var propVal) in deployNode.Properties)
                        {
                            SetProperty(param, propName, propVal, paramInputProps, context);
                        }

                        var task = (Task)invoke.Invoke(null, [param, invokeOptions!])!;
                        await task.ConfigureAwait(false);

                        var resultProperty = task.GetType().GetProperty("Result");
                        var resource = resultProperty!.GetValue(task);

                        AddResource(deployNode.Node, context.Scope, resource!, isExistingResource: true);

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
                    var paramInputProps = GetInputProps(paramType.ParameterType);

                    foreach (var parent in deployNode.Parents)
                    {
                        PropagateParentProperties(parent, param, paramInputProps, context);
                    }

                    ApplyRelations(deployNode, param, context);

                    foreach ((var propName, var propVal) in deployNode.Properties)
                    {
                        SetProperty(param, propName, propVal, paramInputProps, context);
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

                    var newRes = Activator.CreateInstance(type, [SubstituteVariables(resVar, context), param, customResourceOptions!]);
                    AddResource(deployNode.Node, context.Scope, newRes!, isExistingResource: false);

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
        private IReadOnlyCollection<ITransformer> GetTransformers(Dictionary<string, string> properties)
        {
            var transformers = new List<ITransformer>();
            foreach ((var name, var get) in TransformerRegistry.Registry)
            {
                if (properties.TryGetValue(name, out var param))
                {
                    var transformer = get(param);
                    transformers.Add(transformer);
                }
            }
            return transformers;
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
                    ApplyDependency(source!, param, sourcePath, targetPath, context, GetTransformers(new Dictionary<string, string>(relationship.Properties)));
                }
            }

            if (deployNode.Node is ContainerInstance ci && TryGetExistingResourceByNode(ci.Container, context.Scope, out var image) && image is Image dockerImage)
            {
                if (dockerImageReferenceConfigurator.TryGetImageReference(param, dockerImage, out var dockerImageRef))
                {
                    SetProperty(param, dockerImageRef!.ResourceImagePropertyPath, dockerImageRef!.ImageRef, GetInputProps(param.GetType()), context);
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

            if (hierarchyRegistry.TryGetValue(resource!.GetType(), out var rules))
            {
                foreach (var rule in rules)
                {
                    var value = rule.ParentOutputProperty(resource);
                    if (value != null)
                    {
                        foreach (var targetProp in rule.TargetInputProperties)
                        {
                            SetProperty(param, targetProp, value, paramInputProps, context);
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
        /// Stores a resource in the current scope as either created or referenced.
        /// </summary>
        /// <param name="node">Model item represented by the resource.</param>
        /// <param name="scope">Scope that should own the registration.</param>
        /// <param name="resource">Pulumi resource or invoke result.</param>
        /// <param name="isExistingResource">Whether the resource is referenced rather than newly created.</param>
        private void AddResource(ModelItem node, ResourceScope scope, object resource, bool isExistingResource)
        {
            var resources = isExistingResource ? scope.ReferencedResources : scope.CreatedResources;
            resources.Add(node, resource);
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
        /// Builds and caches a lookup of Pulumi input property names to CLR properties.
        /// </summary>
        /// <param name="type">Argument type to inspect.</param>
        /// <returns>Case-insensitive mapping of Pulumi input names to CLR properties.</returns>
        private Dictionary<string, PropertyInfo> GetInputProps(Type type)
        {
            if (allInputProps.TryGetValue(type, out var props))
            {
                return props;
            }

            props = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. public props
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var attr = prop.GetCustomAttribute<InputAttribute>();
                if (attr != null)
                {
                    var name = (string)inputAttrNameProp.GetValue(attr)!;
                    props[name] = prop;
                }
            }

            // 2. private fields
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                var attr = field.GetCustomAttribute<InputAttribute>();
                if (attr == null) continue;

                var name = (string)inputAttrNameProp.GetValue(attr)!;

                var prop = FindPropertyForBackingField(type, field);
                if (prop != null)
                {
                    props[name] = prop;
                }
            }

            allInputProps[type] = props;
            return props;

        }

        /// <summary>
        /// Builds and caches a lookup of Pulumi output member names to CLR members.
        /// </summary>
        /// <param name="type">Resource or output type to inspect.</param>
        /// <returns>Case-insensitive mapping of output names to readable members.</returns>
        private Dictionary<string, MemberInfo> GetOutputMembers(Type type)
        {
            if (_outputMembersCache.TryGetValue(type, out var cached))
                return cached;

            var dict = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. CustomResource с [Output]
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var outAttr = prop.GetCustomAttribute<OutputAttribute>();
                if (outAttr != null)
                {
                    var name = outAttr.Name; // "name", "numberOfSites" и т.п.
                    dict[name] = prop;
                }
            }

            // 2. [OutputType] – поля/свойства → camelCase
            var outputTypeAttr = type.GetCustomAttribute<OutputTypeAttribute>();
            if (outputTypeAttr != null)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var name = ToCamelCase(field.Name); // Name → name, ResourceGroup → resourceGroup
                    dict[name] = field;
                }

                foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    var name = ToCamelCase(prop.Name);
                    dict[name] = prop;
                }
            }

            _outputMembersCache[type] = dict;
            return dict;
        }

        /// <summary>
        /// Converts a CLR member name to the camelCase shape commonly used by Pulumi outputs.
        /// </summary>
        /// <param name="name">CLR member name.</param>
        /// <returns>The camelCase representation.</returns>
        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
                return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Reads a nested output value from a resource or invoke result using a dot-separated path.
        /// </summary>
        /// <param name="source">Resource or output object to inspect.</param>
        /// <param name="path">Output path such as <c>name</c> or <c>identity.principalId</c>.</param>
        /// <returns>The resolved value, nested output, or <c>null</c> when the path cannot be resolved.</returns>
        /// <remarks>
        /// When the current segment is an <c>Output&lt;T&gt;</c>, the method preserves the output wrapper for downstream conversion.
        /// </remarks>
        private object? GetOutputValue(object source, string path)
        {
            var parts = path.Split('.', 2);
            var head = parts[0];
            var tail = parts.Length > 1 ? parts[1] : null;

            var type = source.GetType();
            var members = GetOutputMembers(type);

            if (!members.TryGetValue(head, out var member))
                return null;

            object? value = member switch
            {
                PropertyInfo p => p.GetValue(source),
                FieldInfo f => f.GetValue(source),
                _ => null
            };

            if (value == null)
            {
                return null;
            }

            // если это Output<T> – дальше работаем с T
            var valueType = value.GetType();
            if (IsOutput(valueType))
            {
                // тут у тебя уже есть своя логика работы с Output<T> (Apply и т.п.)
                // для маппинга зависимостей обычно достаточно сохранить сам Output<T>
                // и передать его в ConvertValue при установке target
                if (tail == null)
                {
                    return value;
                }

                // вложенный путь внутри OutputType – нужно Apply
                // Output<TOuter> → Output<TInner>
                var innerType = valueType.GetGenericArguments()[0];
                return ProjectNestedOutput(value, innerType, tail);
            }

            // если нет хвоста – это конечное значение
            if (tail == null)
            {
                return value;
            }

            // вложенный объект – рекурсивно
            return GetOutputValue(value, tail);
        }

        /// <summary>
        /// Projects a nested value from an <c>Output&lt;T&gt;</c> into another output.
        /// </summary>
        /// <param name="outputObj">Source output object.</param>
        /// <param name="innerType">Inner type carried by the output.</param>
        /// <param name="tailPath">Remaining nested path to evaluate on the inner value.</param>
        /// <returns>The projected output value.</returns>
        /// <remarks>
        /// The current implementation is a placeholder and should be completed if nested output projection becomes necessary.
        /// </remarks>
        private object ProjectNestedOutput(object outputObj, Type innerType, string tailPath)
        {
            // Output<TInner>.Apply(x => GetOutputValue(x, tailPath))
            var outputType = typeof(Output<>).MakeGenericType(innerType);
            var applyMethod = outputType.GetMethods()
                .First(m => m.Name == "Apply" && m.GetParameters().Length == 1);

            // Func<TInner, object?>
            var funcType = typeof(Func<,>).MakeGenericType(innerType, typeof(object));
            //var func = (Delegate)Activator.CreateInstance(
            //    typeof(Func<,>).MakeGenericType(innerType, typeof(object)),
            //    (object?)(TInner x) => GetOutputValue(x!, tailPath))!; // псевдокод, можно собрать через Expression

            //return applyMethod.Invoke(outputObj, new object[] { func })!;

            return null!;
        }

        private static bool IsOutput(Type t)
            => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Output<>);

        /// <summary>
        /// Reads a value from the source resource, optionally transforms it, and writes it into the target arguments.
        /// </summary>
        /// <param name="source">Resource or invoke result that provides the source value.</param>
        /// <param name="target">Argument object receiving the value.</param>
        /// <param name="sourcePath">Dot-separated output path on the source.</param>
        /// <param name="targetPath">Dot-separated input path on the target.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="transformers">Optional transformer pipeline applied before assignment.</param>
        private void ApplyDependency(object source, object target, string sourcePath, string targetPath, DeploymentContext context, IReadOnlyCollection<ITransformer> transformers)
        {
            var value = GetOutputValue(source, sourcePath);
            if (value == null)
                return;

            var inputProps = GetInputProps(target.GetType());
            if (transformers.Count > 0)
            {
                foreach (var transformer in transformers)
                {
                    value = ConvertValue(transformer.InputType, value, context);
                    value = transformer.Transform(value);
                }
            }
            SetProperty(target, targetPath, value, inputProps, context);
        }

        private static PropertyInfo? FindPropertyForBackingField(Type type, FieldInfo field)
        {
            var name = field.Name;

            if (name.StartsWith('_'))
            {
                name = name[1..];
            }

            // PascalCase
            name = char.ToUpper(name[0]) + name[1..];

            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        }

        /// <summary>
        /// Assigns a value into a possibly nested Pulumi input path.
        /// </summary>
        /// <param name="target">Target argument object.</param>
        /// <param name="path">Input path that may include nesting or collection operations.</param>
        /// <param name="value">Raw value to assign.</param>
        /// <param name="inputProps">Cached input property map for the target type.</param>
        /// <param name="context">Current deployment context.</param>
        /// <remarks>
        /// Supports plain assignment, keyed collection assignment via <c>:</c>, and list append via <c>+=</c>.
        /// </remarks>
        private void SetProperty(object target, string path, object value, Dictionary<string, PropertyInfo> inputProps, DeploymentContext context)
        {
            var parts = path.Split('.', 2);

            if (parts.Length == 1)
            {
                if (parts[0].Contains(':'))
                {
                    AddKeyToCollection(target, inputProps, parts[0], value, context);
                    return;
                }

                if (parts[0].Contains("+="))
                {
                    AddItemsToCollection(target, inputProps, parts[0], value, context);
                    return;
                }

                // leaf property
                if (inputProps.TryGetValue(parts[0], out var prop))
                {
                    object converted = ConvertValue(prop.PropertyType, value, context);
                    prop.SetValue(target, converted);
                }
                return;
            }

            var head = parts[0];
            var tail = parts[1];

            if (!inputProps.TryGetValue(head, out var headProp))
                return;

            var current = headProp.GetValue(target);
            if (current == null)
            {
                current = CreateNestedInstance(headProp.PropertyType, context, out var unwrapped);
                childInputWrappers[current] = unwrapped ?? current;
                headProp.SetValue(target, current);
            }

            var nestedProps = GetInputProps(GetUnderlyingArgsType(headProp.PropertyType));

            SetProperty(childInputWrappers[current], tail, value, nestedProps, context);
        }

        /// <summary>
        /// Appends values to an <c>InputList&lt;T&gt;</c> property using the <c>+=</c> syntax.
        /// </summary>
        /// <param name="target">Target argument object.</param>
        /// <param name="inputProps">Cached input property map for the target type.</param>
        /// <param name="path">Collection append expression.</param>
        /// <param name="value">Value to append.</param>
        /// <param name="context">Current deployment context.</param>
        private void AddItemsToCollection(object target, Dictionary<string, PropertyInfo> inputProps, string path, object value, DeploymentContext context)
        {
            var parts = path.Split("+=", 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("Collection append operation requires exactly one '+=' operator");
            }

            var collectionPropName = parts[0];

            if (!inputProps.TryGetValue(collectionPropName, out var collectionProp))
                throw new InvalidOperationException($"Property {collectionPropName} not found on {target.GetType().Name}");

            var collectionType = collectionProp.PropertyType;

            // --- InputList<T> ---
            if (collectionType.IsGenericType &&
                collectionType.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                AddValuesToList(target, collectionProp, value, context);
                return;
            }

            throw new InvalidOperationException(
                $"Append operation supports only properties of type 'InputList<T>'. '{collectionPropName}' has type '{collectionType.Name}'");
        }

        /// <summary>
        /// Converts and appends one or more values into an <c>InputList&lt;T&gt;</c> property.
        /// </summary>
        /// <param name="target">Target argument object.</param>
        /// <param name="listProp">List property being modified.</param>
        /// <param name="value">Raw value or collection to append.</param>
        /// <param name="context">Current deployment context.</param>
        private void AddValuesToList(object target, PropertyInfo listProp, object value, DeploymentContext context)
        {
            var listType = listProp.PropertyType;
            var itemType = listType.GetGenericArguments()[0];

            // Получаем текущий список
            var list = listProp.GetValue(target);
            if (list == null)
            {
                list = Activator.CreateInstance(listType);
                listProp.SetValue(target, list);
            }

            // Находим метод AddRange
            var addRangeMethod = listType.GetMethod("AddRange");

            // Конвертируем значение в InputList<T>
            var inputListType = typeof(InputList<>).MakeGenericType(itemType);
            var inputList = ConvertValue(inputListType, value, context);

            // Добавляем элемент
            addRangeMethod!.Invoke(list, [inputList]);
        }

        /// <summary>
        /// Assigns a keyed value into an <c>InputList&lt;T&gt;</c> or <c>InputMap&lt;T&gt;</c> using the <c>name:key</c> syntax.
        /// </summary>
        /// <param name="target">Target argument object.</param>
        /// <param name="inputProps">Cached input property map for the target type.</param>
        /// <param name="path">Keyed assignment expression.</param>
        /// <param name="value">Value to assign.</param>
        /// <param name="context">Current deployment context.</param>
        private void AddKeyToCollection(object target, Dictionary<string, PropertyInfo> inputProps, string path, object value, DeploymentContext context)
        {
            var parts = path.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("Collection assignment requires exactly one ':' separator");
            }

            var collectionPropName = parts[0];
            var key = parts[1];

            if (!inputProps.TryGetValue(collectionPropName, out var collectionProp))
            {
                throw new InvalidOperationException($"Property {collectionPropName} not found on {target.GetType().Name}");
            }

            var collectionType = collectionProp.PropertyType;

            // --- InputList<T> ---
            if (collectionType.IsGenericType &&
                collectionType.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                AddKeyToList(target, collectionProp, key, value, context);
                return;
            }

            // --- InputMap<T> ---
            if (collectionType.IsGenericType &&
                collectionType.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                AddKeyToMap(target, collectionProp, key, value, context);
                return;
            }

            throw new InvalidOperationException(
                $"{collectionPropName} is neither InputList<T> nor InputMap<T>");
        }

        /// <summary>
        /// Adds a named entry into an <c>InputList&lt;T&gt;</c> by populating the item key and value fields.
        /// </summary>
        /// <param name="target">Target argument object.</param>
        /// <param name="listProp">List property being modified.</param>
        /// <param name="key">Logical item key or name.</param>
        /// <param name="value">Value assigned to the item.</param>
        /// <param name="context">Current deployment context.</param>
        private void AddKeyToList(object target, PropertyInfo listProp, string key, object value, DeploymentContext context)
        {
            var listType = listProp.PropertyType;
            var itemType = listType.GetGenericArguments()[0];

            // Создаём список, если его нет
            var list = listProp.GetValue(target);
            if (list == null)
            {
                list = Activator.CreateInstance(listType);
                listProp.SetValue(target, list);
            }

            // Создаём элемент T
            var item = Activator.CreateInstance(itemType);

            // Ищем Name или Key
            var nameProp = (itemType.GetProperty("Name") ?? itemType.GetProperty("Key"))
                ?? throw new InvalidOperationException($"{itemType.Name} must contain Name or Key property");

            // Ищем Value
            var valueProp = itemType.GetProperty("Value")?? itemType.GetProperty("ConnectionString")
                ?? throw new InvalidOperationException($"{itemType.Name} must contain Value or ConnectionString property");

            // Конвертируем значение
            var convertedValue = ConvertValue(valueProp.PropertyType, value, context);

            // Устанавливаем свойства
            nameProp.SetValue(item, (Input<string>)key);
            valueProp.SetValue(item, convertedValue);

            // Находим метод Add(params Input<T>[] inputs)
            var addMethod = listType.GetMethods().Where(m => m.Name == "Add")
                .Where(m =>
                {
                    var paramType = m.GetParameters().First().ParameterType;
                    return paramType.IsArray &&
                           paramType.GetElementType()!.IsGenericType &&
                           paramType.GetElementType()!.GetGenericTypeDefinition() == typeof(Input<>);
                })
                .Single();

            Type inputItemType = typeof(Input<>).MakeGenericType(itemType);
            var inputItem = ConvertValue(inputItemType, item!, context);

            // Создаём массив Input<T> из одного элемента
            var inputArray = Array.CreateInstance(inputItemType, 1);
            inputArray.SetValue(inputItem, 0);

            // Добавляем элемент
            addMethod.Invoke(list, [inputArray]);
        }

        /// <summary>
        /// Adds a keyed entry into an <c>InputMap&lt;T&gt;</c> property.
        /// </summary>
        /// <param name="target">Target argument object.</param>
        /// <param name="mapProp">Map property being modified.</param>
        /// <param name="key">Dictionary key.</param>
        /// <param name="value">Value assigned to the key.</param>
        /// <param name="context">Current deployment context.</param>
        private void AddKeyToMap(object target, PropertyInfo mapProp, string key, object value, DeploymentContext context)
        {
            var mapType = mapProp.PropertyType;
            var valueType = mapType.GetGenericArguments()[0]; // TValue

            // Создаём словарь, если его нет
            var map = mapProp.GetValue(target);
            if (map == null)
            {
                map = Activator.CreateInstance(mapType);
                mapProp.SetValue(target, map);
            }

            // Находим метод Add(string, Input<TValue>)
            var addMethod = mapType.GetMethods()
                .Where(m => m.Name == "Add" && m.GetParameters().Length == 2)
                .Single();

            // Конвертируем значение в Input<TValue>
            var inputValueType = typeof(Input<>).MakeGenericType(valueType);
            var convertedValue = ConvertValue(inputValueType, value, context);

            // Добавляем в словарь
            addMethod.Invoke(map, [key, convertedValue]);
        }

        /// <summary>
        /// Creates an instance for a nested input property and also exposes its mutable inner object when needed.
        /// </summary>
        /// <param name="type">Nested property type to instantiate.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="unwrapped">Mutable inner object used for recursive property assignment.</param>
        /// <returns>A value ready to be assigned into the parent property.</returns>
        private object CreateNestedInstance(Type type, DeploymentContext context, out object? unwrapped)
        {
            // Input<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>))
            {
                var inner = type.GetGenericArguments()[0];
                var instance = Activator.CreateInstance(inner)!;
                unwrapped = instance;
                return WrapInput(inner, instance);
            }

            // InputList<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                var elem = type.GetGenericArguments()[0];
                var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elem))!;
                unwrapped = list;
                return WrapInputList(elem, list, context);
            }

            // InputMap<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                var elem = type.GetGenericArguments()[0];
                var dict = Activator.CreateInstance(typeof(Dictionary<,>)
                    .MakeGenericType(typeof(string), elem))!;
                unwrapped = dict;
                return WrapInputMap(elem, dict, context);
            }

            // zwykły Args
            unwrapped = null;
            return Activator.CreateInstance(type)!;
        }

        private static Type GetUnderlyingArgsType(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>))
            {
                return type.GetGenericArguments()[0];
            }

            return type;
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
        private object ConvertValue(Type targetType, object sourceValue, DeploymentContext context)
        {
            if (sourceValue is string str)
            {
                sourceValue = SubstituteVariables(str, context);
            }

            if (sourceValue == null)
            {
                return null!;
            }

            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var sourceType = sourceValue.GetType();

            if (targetType == typeof(object) || targetType.IsAssignableFrom(sourceType))
            {
                return sourceValue;
            }

            // source value is Output
            if (IsGenericOutput(sourceType))
            {
                // target is Input
                if (IsGenericInput(targetType))
                {
                    var innerTargetType = targetType.GetGenericArguments()[0];
                    CheckGenericArguments(targetType, sourceType, innerTargetType);
                    return ConvertOutputToInput(innerTargetType, sourceValue);
                }

                if (IsGenericInputList(targetType))
                {
                    var innerTargetType = targetType.GetGenericArguments()[0];
                    CheckGenericArguments(targetType, sourceType, innerTargetType);
                    return ConvertOutputToInputList(innerTargetType, sourceValue);
                }
            }

            // Input<T>
            if (IsGenericInput(targetType))
            {
                var innerType = targetType.GetGenericArguments()[0];

                // если rawValue уже совместим с innerType → используем implicit operator
                if (innerType.IsAssignableFrom(sourceType))
                    return WrapInput(innerType, sourceValue);

                // иначе конвертируем и оборачиваем
                var converted = ConvertValue(innerType, sourceValue, context);
                return WrapInput(innerType, converted);
            }

            // InputList<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var list = ConvertToList(elemType, sourceValue, context);
                return WrapInputList(elemType, list, context);
            }

            // InputMap<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var dict = ConvertToDictionary(elemType, sourceValue, context);
                return WrapInputMap(elemType, dict, context);
            }

            // InputUnion<T0,T1>
            if (IsGenericInputUnion(targetType))
            {
                return ConvertToInputUnion(targetType, sourceValue, context);
            }

            // Union<T0,T1>
            if (IsGenericUnion(targetType))
            {
                return ConvertToUnion(targetType, sourceValue, context);
            }

            // Enum
            if (IsPulumiEnum(targetType))
            {
                return ConvertPulumiEnum(targetType, sourceValue, context);
            }

            // Primitives
            if (targetType == typeof(string)) return sourceValue.ToString()!;
            if (targetType == typeof(int)) return int.Parse(sourceValue.ToString()!);
            if (targetType == typeof(bool)) return bool.Parse(sourceValue.ToString()!);

            throw new NotSupportedException($"Cannot convert '{sourceValue}' to {targetType}");
        }

        /// <summary>
        /// Converts a raw value into an <c>InputUnion&lt;...&gt;</c> by trying each supported union branch.
        /// </summary>
        /// <param name="targetType">Target input union type.</param>
        /// <param name="rawValue">Raw value to convert.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>A wrapped input union value.</returns>
        private object ConvertToInputUnion(Type targetType, object rawValue, DeploymentContext context)
        {
            if (TryWrapIntoTargetType(targetType, rawValue, out var wrapped))
            {
                return wrapped;
            }

            var unionArgs = targetType.GetGenericArguments();
            for (var i = 0; i < unionArgs.Length; i++)
            {
                var unionArgType = unionArgs[i];
                if (!TryConvertToType(unionArgType, rawValue, context, out var convertedValue) || convertedValue == null)
                {
                    continue;
                }

                if (TryWrapIntoTargetType(targetType, convertedValue, out wrapped))
                {
                    return wrapped;
                }

                var inputType = typeof(Input<>).MakeGenericType(unionArgType);
                if (TryConvertToType(inputType, convertedValue, context, out var inputValue) &&
                    inputValue != null &&
                    TryWrapIntoTargetType(targetType, inputValue, out wrapped))
                {
                    return wrapped;
                }

                var unionType = typeof(Union<,>).MakeGenericType(unionArgs);
                var fromValue = unionType.GetMethod($"FromT{i}", BindingFlags.Public | BindingFlags.Static);
                if (fromValue != null)
                {
                    var unionValue = fromValue.Invoke(null, [convertedValue]);
                    if (unionValue != null && TryWrapIntoTargetType(targetType, unionValue, out wrapped))
                    {
                        return wrapped;
                    }
                }
            }

            throw new NotSupportedException($"Cannot convert '{rawValue}' to {targetType}");
        }

        private static object ConvertOutputToInputList(Type innerTargetType, object output)
        {

            var listType = typeof(List<>).MakeGenericType(output.GetType());
            var list = (IList)Activator.CreateInstance(listType)!;
            list.Add(output);

            var inputListType = typeof(InputList<>).MakeGenericType(innerTargetType);

            // szukamy: public static implicit operator InputList<T>(List<Output<T>> values)
            var op = inputListType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [listType],
                modifiers: null)!;

            return op.Invoke(null, [list])!;
        }

        private static void CheckGenericArguments(Type targetType, Type sourceType, Type innerTargetType)
        {
            var innerSourceType = sourceType.GetGenericArguments()[0];
            if (innerTargetType != innerSourceType)
            {
                throw new InvalidOperationException($"Connot convert {sourceType.FullName} to {targetType.FullName}");
            }
        }

        private static bool IsGenericOutput(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Output<>);
        }

        private static bool IsGenericInput(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>);
        }

        private static bool IsGenericInputList(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>);
        }

        private static bool IsGenericInputUnion(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputUnion<,>);
        }

        private static bool IsGenericUnion(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Union<,>);
        }

        private static bool IsPulumiEnum(Type type)
        {
            return type.GetCustomAttribute<EnumTypeAttribute>() != null;
        }

        private static bool TryWrapIntoTargetType(Type targetType, object value, out object wrapped)
        {
            var valueType = value.GetType();

            var implicitOperator = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "op_Implicit" && method.ReturnType == targetType)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(valueType);
                });

            if (implicitOperator != null)
            {
                wrapped = implicitOperator.Invoke(null, [value])!;
                return true;
            }

            wrapped = null!;
            return false;
        }

        /// <summary>
        /// Converts a string-like value into a Pulumi string-backed enum instance.
        /// </summary>
        /// <param name="enumType">Pulumi enum type.</param>
        /// <param name="sourceValue">Raw value to match.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>The matching enum instance.</returns>
        private object ConvertPulumiEnum(Type enumType, object sourceValue, DeploymentContext context)
        {
            var str = (string)ConvertValue(typeof(string), sourceValue, context);

            // znajdź wszystkie publiczne statyczne pola (np. SystemAssigned, UserAssigned)
            var props = enumType.GetProperties(BindingFlags.Public | BindingFlags.Static);

            foreach (var prop in props)
            {
                var propValue = prop.GetValue(null)!;

                // enumType ma prywatne pole "_value"
                var valueField = enumType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueField == null) continue;

                var enumString = valueField.GetValue(propValue)?.ToString();

                if (string.Equals(enumString, str, StringComparison.OrdinalIgnoreCase))
                {
                    return propValue;
                }
            }

            throw new NotSupportedException(
                $"Cannot convert '{sourceValue}' to Pulumi enum type {enumType.Name}");
        }

        private static object ConvertOutputToInput(Type innerType, object output)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);
            // implicit operator Input<T>(Output<T> value)
            var op1 = inputType.GetMethod("op_Implicit", [output.GetType()])!;
            return op1.Invoke(null, [output])!;
        }

        private static object WrapInput(Type innerType, object value)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);

            // implicit operator Input<T>(T value)
            var op1 = inputType.GetMethod("op_Implicit", [innerType]);
            if (op1 != null)
                return op1.Invoke(null, [value])!;

            // implicit operator Input<T>(Output<T> value)
            var outputType = typeof(Output<>).MakeGenericType(innerType);
            var op2 = inputType.GetMethod("op_Implicit", [outputType]);
            if (op2 != null)
            {
                var output = WrapOutput(innerType, value);
                return op2.Invoke(null, [output])!;
            }

            throw new InvalidOperationException($"Cannot wrap value into Input<{innerType}>");
        }

        private static object WrapOutput(Type innerType, object value)
        {
            var create = typeof(Output)
                .GetMethods()
                .First(m => m.Name == "Create" && m.IsGenericMethod)
                .MakeGenericMethod(innerType);

            return create.Invoke(null, [value])!;
        }

        /// <summary>
        /// Wraps a CLR list into an <c>InputList&lt;T&gt;</c>, converting elements when necessary.
        /// </summary>
        /// <param name="elemType">Element type expected by the input list.</param>
        /// <param name="listObj">List or enumerable to wrap.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>An <c>InputList&lt;T&gt;</c> instance.</returns>
        private object WrapInputList(Type elemType, object listObj, DeploymentContext context)
        {
            var listType = typeof(List<>).MakeGenericType(elemType);

            // jeśli to jeszcze nie jest List<T> – spróbuj skonwertować
            if (!listType.IsInstanceOfType(listObj))
            {
                var tmp = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in (IEnumerable)listObj)
                {
                    tmp.Add(ConvertValue(elemType, item!, context));
                }
                listObj = tmp;
            }

            var inputListType = typeof(InputList<>).MakeGenericType(elemType);

            // szukamy: public static implicit operator InputList<T>(List<T> values)
            var op = inputListType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [listType],
                modifiers: null);

            if (op == null)
            {
                throw new InvalidOperationException($"No implicit operator InputList<{elemType.Name}>(List<{elemType.Name}>) found.");
            }

            return op.Invoke(null, [listObj])!;
        }

        /// <summary>
        /// Wraps a CLR dictionary into an <c>InputMap&lt;T&gt;</c>, converting values when necessary.
        /// </summary>
        /// <param name="valueType">Value type expected by the input map.</param>
        /// <param name="dictObj">Dictionary to wrap.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>An <c>InputMap&lt;T&gt;</c> instance.</returns>
        private object WrapInputMap(Type valueType, object dictObj, DeploymentContext context)
        {
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);

            // если это ещё не Dictionary<string,T> — пробуем привести
            if (!dictType.IsInstanceOfType(dictObj))
            {
                var tmp = (IDictionary)Activator.CreateInstance(dictType)!;
                foreach (DictionaryEntry kv in (IDictionary)dictObj)
                {
                    tmp[kv.Key] = ConvertValue(valueType, kv.Value!, context);
                }
                dictObj = tmp;
            }

            var inputMapType = typeof(InputMap<>).MakeGenericType(valueType);

            // ищем: public static implicit operator InputMap<TValue>(Dictionary<string, TValue> values)
            var op = inputMapType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [dictType],
                modifiers: null);

            if (op == null)
            {
                throw new InvalidOperationException(
                    $"No implicit operator InputMap<{valueType.Name}>(Dictionary<string,{valueType.Name}>) found.");
            }

            return op.Invoke(null, [dictObj])!;
        }

        /// <summary>
        /// Converts a raw value into a two-branch <c>Union&lt;T0, T1&gt;</c>.
        /// </summary>
        /// <param name="unionType">Target union type.</param>
        /// <param name="rawValue">Raw value to convert.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>A union value containing the first matching branch.</returns>
        private object ConvertToUnion(Type unionType, object rawValue, DeploymentContext context)
        {
            var args = unionType.GetGenericArguments();
            var t0 = args[0];
            var t1 = args[1];

            // 1. Jeśli rawValue już jest Union<T0,T1>
            if (unionType.IsAssignableFrom(rawValue.GetType()))
                return rawValue;

            // 2. Spróbuj skonwertować rawValue do T0
            if (TryConvertToType(t0, rawValue, context, out var v0))
            {
                var fromT0 = unionType.GetMethod("FromT0", BindingFlags.Public | BindingFlags.Static)!;
                return fromT0.Invoke(null, [v0])!;
            }

            // 3. Spróbuj skonwertować rawValue do T1
            if (TryConvertToType(t1, rawValue, context, out var v1))
            {
                var fromT1 = unionType.GetMethod("FromT1", BindingFlags.Public | BindingFlags.Static)!;
                return fromT1.Invoke(null, [v1])!;
            }

            throw new NotSupportedException(
                $"Cannot convert '{rawValue}' to Union<{t0.Name},{t1.Name}>");
        }

        /// <summary>
        /// Attempts to convert a value into the specified target type without throwing on failure.
        /// </summary>
        /// <param name="targetType">Desired destination type.</param>
        /// <param name="rawValue">Raw value to convert.</param>
        /// <param name="context">Current deployment context.</param>
        /// <param name="result">Converted value when successful.</param>
        /// <returns><c>true</c> when conversion succeeds.</returns>
        private bool TryConvertToType(Type targetType, object rawValue, DeploymentContext context, out object? result)
        {
            try
            {
                result = ConvertValue(targetType, rawValue, context);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        /// Converts a raw value into a strongly typed CLR list.
        /// </summary>
        /// <param name="elemType">Element type expected in the resulting list.</param>
        /// <param name="raw">Raw scalar, string, or enumerable source value.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>A typed <c>List&lt;T&gt;</c> instance.</returns>
        private object ConvertToList(Type elemType, object raw, DeploymentContext context)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType))!;

            if (raw is string s)
            {
                foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    list.Add(ConvertValue(elemType, part.Trim(), context));
                }
            }
            else if (raw is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    list.Add(ConvertValue(elemType, item, context));
                }
            }
            else
            {
                list.Add(ConvertValue(elemType, raw, context));
            }

            return list;
        }

        /// <summary>
        /// Converts a raw dictionary into a typed <c>Dictionary&lt;string, T&gt;</c>.
        /// </summary>
        /// <param name="elemType">Value type expected in the resulting dictionary.</param>
        /// <param name="sourceValue">Raw source dictionary.</param>
        /// <param name="context">Current deployment context.</param>
        /// <returns>A typed dictionary ready for wrapping into Pulumi inputs.</returns>
        private object ConvertToDictionary(Type elemType, object sourceValue, DeploymentContext context)
        {
            var dict = (IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), elemType))!;

            if (sourceValue is IDictionary<string, object> rawDict)
            {
                foreach (var kv in rawDict)
                {
                    dict[kv.Key] = ConvertValue(elemType, kv.Value, context);
                }
            }
            else
            {
                throw new NotSupportedException($"Cannot convert '{sourceValue}' to Dictionary<string,{elemType.Name}>");
            }

            return dict;
        }
    }
}
