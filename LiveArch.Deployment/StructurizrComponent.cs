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

        private Func<string, object> SubstituteVariables(DeploymentContext context)
        {
            return s => SubstituteVariables(s, context);
        }

        private IEnumerable<RelationshipAdapter> GetRelationshipAdapters(IDeploymentAdapter deployNode)
        {
            return deployNode.Relationships.In(deploymentView);
        }

        private static bool HasMappedDependency(RelationshipAdapter relationship)
        {
            return relationship.Properties.ContainsKey("source") &&
                relationship.Properties.ContainsKey("target");
        }

        private bool HasExplicitDependency(RelationshipAdapter relationship, DeploymentContext context)
        {
            if (!relationship.Properties.TryGetValue("dependsOn", out var dependsOnValue))
            {
                return false;
            }

            return bool.TryParse(SubstituteVariables(dependsOnValue, context).ToString(), out var dependsOn) &&
                dependsOn;
        }

        private bool RequiresNodeDependency(RelationshipAdapter relationship, DeploymentContext context)
        {
            return HasMappedDependency(relationship) ||
                HasExplicitDependency(relationship, context);
        }

        private bool RequiresRelationshipDependency(RelationshipAdapter relationship, DeploymentContext context)
        {
            return !string.IsNullOrEmpty(relationship.Technology) ||
                RequiresNodeDependency(relationship, context);
        }

        private async Task ProcessDeploymentNodeAsync(DeploymentNode deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            var deploymentNode = new DeploymentNodeAdapter(deployNode, SubstituteVariables(context));
            if (deploymentNode.IsDisabled == false)
            {
                await CreateNodeAsync(deploymentNode, context, cancellationToken);
            }
        }

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

        private async Task ProcessContainerInstanceAsync(ContainerInstance containerInstance, DeploymentContext context, CancellationToken cancellationToken)
        {
            var container = new ContainerInstanceAdapter(containerInstance, SubstituteVariables(context));
            if (container.IsDisabled == false)
            {
                await CreateNodeAsync(container, context, cancellationToken);
            }
        }

        private async Task BuildContainerInstance(ContainerInstance containerInstance, DeploymentContext context, CancellationToken cancellationToken)
        {
            await CreateNodeAsync(new ContainerBuildAdapter(containerInstance.Container, SubstituteVariables(context)), context, cancellationToken);
        }

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

        private InfrastructureNodeAdapter? GetSourceNode(DeploymentNode deploymentNode, DeploymentContext context)
        {
            return deploymentNode.InfrastructureNodes
                .Where(x => x.Technology == ForEachSource.Technology)
                .Select(x => new InfrastructureNodeAdapter(x, SubstituteVariables(context)))
                .Where(x => x.IsDisabled == false)
                .FirstOrDefault();
        }

        private void RegisterWaitingNode(IDeploymentAdapter deployNode, DeploymentContext context, IReadOnlyCollection<ModelItem> missingDependencies)
        {
            var key = new ResourceKey(deployNode.Node, context.Scope.Id);

            var waitingNode = new WaitingNodeRegistration(deployNode, context, missingDependencies);
            waitingNodes[key] = waitingNode;
        }

        private void RemoveWaitingNode(ResourceKey key)
        {
            waitingNodes.Remove(key);
        }

        private void RemoveAncestorWaitingNodes(ModelItem node, ResourceScope scope)
        {
            for (var currentScope = scope.ParentScope; currentScope != null; currentScope = currentScope.ParentScope)
            {
                waitingNodes.Remove(new ResourceKey(node, currentScope.Id));
            }
        }

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

        private async Task PreProcessNodeAsync(IDeploymentAdapter deployNode, DeploymentContext context, CancellationToken cancellationToken)
        {
            switch (deployNode)
            {
                case ContainerInstanceAdapter when deployNode.Node is ContainerInstance containerInstance:
                    await BuildContainerInstance(containerInstance, context, cancellationToken);
                    break;
            }
        }

        private async Task PostProcessNodeAsync(IDeploymentAdapter deployNode, object? resource, DeploymentContext context, CancellationToken cancellationToken)
        {
            switch (deployNode)
            {
                case DeploymentNodeAdapter when deployNode.Node is DeploymentNode deploymentNode:
                    await PostProcessDeploymentNodeAsync(deploymentNode, resource, context, cancellationToken);
                    break;
            }
        }

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

        private DeploymentContext CreateChildContext(ResourceScope parentScope, object ownerResource, IReadOnlyDictionary<string, object> parentVariables, Action<Dictionary<string, object>>? configureVariables = null, DeploymentNode? loopDeploymentNode = null)
        {
            var scope = CreateScope(parentScope, ownerResource);
            var variables = new Dictionary<string, object>(parentVariables);
            configureVariables?.Invoke(variables);
            return CreateDeploymentContext(scope, variables, loopDeploymentNode);
        }

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

        private ResourceScope CreateScope(ResourceScope? parentScope, object ownerResource)
        {
            var createdScope = new ResourceScope(scopeId++, (parentScope?.Level ?? 0) + 1, parentScope, ownerResource);
            parentScope?.ChildScopes.Add(createdScope);
            return createdScope;
        }

        private static DeploymentContext CreateDeploymentContext(ResourceScope scope, IReadOnlyDictionary<string, object> variables, DeploymentNode? loopDeploymentNode = null)
        {
            var contextVariables = new Dictionary<string, object>(variables)
            {
                ["level"] = scope.Level
            };

            return new DeploymentContext(scope, contextVariables, loopDeploymentNode);
        }

        private void AddResource(ModelItem node, ResourceScope scope, object resource, bool isExistingResource)
        {
            var resources = isExistingResource ? scope.ReferencedResources : scope.CreatedResources;
            resources.Add(node, resource);
        }

        private IReadOnlyDictionary<ResourceKey, object> FlattenResources(Func<ResourceScope, Dictionary<ModelItem, object>> getResources)
        {
            var resources = new Dictionary<ResourceKey, object>();
            FlattenResources(rootContext.Scope, getResources, resources);
            return resources;
        }

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

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
                return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // source – уже созданный Pulumi ресурс или результат Get* (OutputType)
        // path – "name", "policy.objectId", "permissions.secrets"
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
            var valueProp = itemType.GetProperty("Value")
                ?? throw new InvalidOperationException($"{itemType.Name} must contain Value property");

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

            //
            // Primitives
            //
            if (targetType == typeof(string)) return sourceValue.ToString()!;
            if (targetType == typeof(int)) return int.Parse(sourceValue.ToString()!);
            if (targetType == typeof(bool)) return bool.Parse(sourceValue.ToString()!);

            throw new NotSupportedException($"Cannot convert '{sourceValue}' to {targetType}");
        }

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
